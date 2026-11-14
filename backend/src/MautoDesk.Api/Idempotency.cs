using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using MautoDesk.SharedKernel;

namespace MautoDesk.Api;

/// <summary>Configuration for <see cref="IdempotencyMiddleware"/>.</summary>
public sealed class IdempotencyOptions
{
    public const string SectionName = "Idempotency";

    /// <summary>The header clients send. <c>Idempotency-Key</c>, per docs/04-api-contracts.md §6.</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>Longest key accepted.</summary>
    /// <remarks>
    /// A UUID is 36 characters. 255 leaves room for a client that prefixes its
    /// own correlation id, and refuses one that tries to store a document in the
    /// primary key.
    /// </remarks>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>
    /// Largest response body that will be stored for replay.
    /// </summary>
    /// <remarks>
    /// Idempotent endpoints create things and answer with the thing they created,
    /// which is small. A response past this cap releases the key instead of being
    /// stored, so a retry re-executes rather than being answered from a truncated
    /// copy. Silently storing half a body would be far worse than re-running.
    /// </remarks>
    public int MaxStoredResponseBytes { get; set; } = 256 * 1024;
}

/// <summary>
/// Makes a repeated <c>POST</c> carrying the same <c>Idempotency-Key</c> return
/// the original response instead of doing the work twice.
/// </summary>
/// <remarks>
/// <para>
/// This exists ahead of the endpoints that need it, on purpose. The next things
/// to be built — lender submissions, pre-registration, temporary-tag issuance,
/// payments — are all operations where a retried request that runs twice is a
/// regulatory or financial incident rather than a cosmetic bug. The mechanism
/// belongs in place before the first such endpoint, not after the first
/// duplicate.
/// </para>
/// <para>
/// <b>What it does not do:</b> it does not <em>require</em> a key. An endpoint
/// that must have one enforces that itself, because "which operations are
/// dangerous to repeat" is a question about the operation, not about HTTP.
/// </para>
/// <para>
/// <b>Placement.</b> After authentication and the tenant claim, because the
/// ledger is keyed by tenant and there is no such thing as a tenantless
/// idempotent write. After the rate limiter, so a client hammering a replay is
/// still throttled.
/// </para>
/// </remarks>
public sealed partial class IdempotencyMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IdempotencyOptions _options;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(
        RequestDelegate next,
        Microsoft.Extensions.Options.IOptions<IdempotencyOptions> options,
        ILogger<IdempotencyMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _options = options.Value;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(tenant);

        if (!ShouldHandle(context, tenant, out var key))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        if (key.Length > _options.MaxKeyLength)
        {
            await WriteProblemAsync(
                context,
                Error.Validation(
                    "idempotency.key_too_long",
                    $"{_options.HeaderName} must be at most {_options.MaxKeyLength} characters."))
                .ConfigureAwait(false);
            return;
        }

        var endpoint = $"{context.Request.Method} {context.Request.Path}";
        var requestHash = await HashRequestAsync(context).ConfigureAwait(false);

        var reservation = await store
            .TryReserveAsync(key, endpoint, requestHash, context.RequestAborted)
            .ConfigureAwait(false);

        switch (reservation.Outcome)
        {
            case IdempotencyOutcome.Replay:
                LogReplayed(_logger, endpoint);
                await ReplayAsync(context, reservation.Response!).ConfigureAwait(false);
                return;

            case IdempotencyOutcome.InProgress:
                // Not a 409-and-give-up: the first attempt may still succeed, and
                // the honest answer is "ask again shortly".
                context.Response.Headers.RetryAfter = "1";
                await WriteProblemAsync(
                    context,
                    Error.Conflict(
                        "idempotency.in_progress",
                        "An identical request with this idempotency key is still being " +
                        "processed. Retry in a moment to receive its result."))
                    .ConfigureAwait(false);
                return;

            case IdempotencyOutcome.Mismatch:
                LogKeyReused(_logger, endpoint);
                await WriteProblemAsync(
                    context,
                    Error.Conflict(
                        "idempotency.key_reused",
                        "This idempotency key was already used for a request with a different " +
                        "body. Use a new key for a new request."))
                    .ConfigureAwait(false);
                return;

            case IdempotencyOutcome.Reserved:
            default:
                break;
        }

        await ExecuteAndRecordAsync(context, store, key, endpoint).ConfigureAwait(false);
    }

    /// <summary>Runs the request with the response captured, then records it.</summary>
    private async Task ExecuteAndRecordAsync(
        HttpContext context,
        IIdempotencyStore store,
        string key,
        string endpoint)
    {
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch
        {
            // The request blew up. Release the key so the client's retry is a real
            // retry rather than a replay of a failure we chose to remember.
            context.Response.Body = originalBody;
            await ReleaseQuietlyAsync(store, key, endpoint).ConfigureAwait(false);
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Position = 0;

        var status = context.Response.StatusCode;

        // A 5xx says nothing about whether retrying would also fail, so it is
        // never stored. Storing it would turn a thirty-second outage into a
        // permanently poisoned key.
        if (status >= StatusCodes.Status500InternalServerError)
        {
            await ReleaseQuietlyAsync(store, key, endpoint).ConfigureAwait(false);
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        if (buffer.Length > _options.MaxStoredResponseBytes)
        {
            LogResponseTooLarge(_logger, endpoint, buffer.Length);
            await ReleaseQuietlyAsync(store, key, endpoint).ConfigureAwait(false);
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        var body = buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());

        // The column is jsonb. A non-JSON body — a 400 from the framework in
        // some other shape, an empty 204 — is stored as null and replays as the
        // status alone, which is still correct and still not a second execution.
        await store
            .CompleteAsync(key, endpoint, status, IsJson(context) ? body : null, context.RequestAborted)
            .ConfigureAwait(false);

        buffer.Position = 0;
        await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ReplayAsync(HttpContext context, IdempotentResponse stored)
    {
        context.Response.StatusCode = stored.Status;

        // So a client — and anyone reading a HAR file at 2am — can tell a replay
        // from a fresh execution.
        context.Response.Headers["Idempotency-Replayed"] = "true";

        if (stored.Body is null)
        {
            return;
        }

        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync(stored.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private bool ShouldHandle(HttpContext context, ITenantContext tenant, out string key)
    {
        key = string.Empty;

        // POST only. PUT and DELETE are idempotent by definition of the method;
        // PATCH gets its concurrency guarantee from If-Match (§6), not from here.
        if (!HttpMethods.IsPost(context.Request.Method))
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue(_options.HeaderName, out var values))
        {
            return false;
        }

        var candidate = values.FirstOrDefault();

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        // No tenant, no ledger: app.idempotency_key.tenant_id is NOT NULL, and it
        // is not null because an idempotency key belongs to a dealership. The
        // unauthenticated POSTs in this system are the login and refresh
        // endpoints, whose replay protection is refresh-token rotation with reuse
        // detection — a stronger mechanism than this one, and already in place.
        if (!tenant.IsAuthenticated || tenant.TenantId is null)
        {
            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>
    /// Hashes what makes two requests "the same request".
    /// </summary>
    /// <remarks>
    /// Method, path, and body. Deliberately <b>not</b> the query string or the
    /// headers: a client that retries with a different <c>Accept-Encoding</c> or
    /// a fresh access token is making the same request, and treating it as a
    /// mismatch would reject a legitimate retry — the exact scenario this
    /// middleware exists to make safe.
    /// </remarks>
    private static async Task<byte[]> HashRequestAsync(HttpContext context)
    {
        context.Request.EnableBuffering();

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(context.Request.Method));
        hasher.AppendData("\n"u8);
        hasher.AppendData(Encoding.UTF8.GetBytes(context.Request.Path.ToString()));
        hasher.AppendData("\n"u8);

        var rented = ArrayPool<byte>.Shared.Rent(8192);

        try
        {
            int read;
            while ((read = await context.Request.Body
                .ReadAsync(rented.AsMemory(0, rented.Length), context.RequestAborted)
                .ConfigureAwait(false)) > 0)
            {
                hasher.AppendData(rented.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }

        // Rewind, or the endpoint reads an empty body and every idempotent
        // request becomes a validation failure.
        context.Request.Body.Position = 0;

        return hasher.GetHashAndReset();
    }

    private static bool IsJson(HttpContext context) =>
        context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;

    private async Task ReleaseQuietlyAsync(IIdempotencyStore store, string key, string endpoint)
    {
        try
        {
            // CancellationToken.None on purpose: this runs on the failure path,
            // often because the client disconnected, and a key left reserved
            // would block the retry for fifteen minutes.
            await store.ReleaseAsync(key, endpoint, CancellationToken.None).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Cleanup must not replace the original failure with its own.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogReleaseFailed(_logger, endpoint, ex);
        }
    }

    /// <summary>
    /// Answers with the same problem+json shape every other failure in this API
    /// uses, rather than inventing a middleware-specific one.
    /// </summary>
    private static async Task WriteProblemAsync(HttpContext context, Error error)
    {
        var result = error.ToProblem(context);
        await result.ExecuteAsync(context).ConfigureAwait(false);
    }

    [LoggerMessage(EventId = 3200, Level = LogLevel.Information,
        Message = "Replayed a stored response for an idempotent request to {Endpoint}.")]
    private static partial void LogReplayed(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning,
        Message = "An idempotency key was reused for a different request body on {Endpoint}.")]
    private static partial void LogKeyReused(ILogger logger, string endpoint);

    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning,
        Message = "The response to {Endpoint} was {Bytes} bytes, past the idempotency store cap; " +
                  "the key was released and a retry will re-execute.")]
    private static partial void LogResponseTooLarge(ILogger logger, string endpoint, long bytes);

    [LoggerMessage(EventId = 3203, Level = LogLevel.Error,
        Message = "Failed to release the idempotency reservation for {Endpoint}; it will be " +
                  "pruned as abandoned.")]
    private static partial void LogReleaseFailed(ILogger logger, string endpoint, Exception exception);
}

/// <summary>Registration for the idempotency ledger.</summary>
public static class IdempotencyExtensions
{
    public static IServiceCollection AddMautoDeskIdempotency(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<IdempotencyOptions>(
            configuration.GetSection(IdempotencyOptions.SectionName));

        return services;
    }

    public static IApplicationBuilder UseMautoDeskIdempotency(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<IdempotencyMiddleware>();
    }
}
