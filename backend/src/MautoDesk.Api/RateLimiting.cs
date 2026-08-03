using System.Globalization;
using System.Threading.RateLimiting;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.RateLimiting;

namespace MautoDesk.Api;

/// <summary>
/// Composite rate limiting, per docs/04-api-contracts.md §7.
/// </summary>
/// <remarks>
/// <para>
/// Cloudflare enforces the outer edge limit in deployed environments. This is
/// the layer Cloudflare cannot provide: limits that are aware of <em>who</em> is
/// calling — the tenant and the user — rather than only the source address. A
/// single dealership behind one office NAT looks like one IP to the edge.
/// </para>
/// <para>
/// <b>Auth endpoints are limited by IP and by account.</b> Account lockout
/// already bounds guessing against one account; the IP limiter is what bounds
/// credential stuffing, where an attacker tries one password against thousands
/// of accounts and never trips a single lockout.
/// </para>
/// <para>
/// Partitions are in-process. That is honest but limited: with several
/// instances, an attacker gets N times the budget. Correct at one instance, and
/// the seam for a Valkey-backed distributed limiter is this file alone. Recorded
/// as a finding rather than left implied.
/// </para>
/// </remarks>
public static class RateLimiting
{
    public const string AuthPolicy = "auth";
    public const string ReadPolicy = "read";
    public const string WritePolicy = "write";

    /// <summary>
    /// Limits, with production values as the defaults.
    /// </summary>
    /// <remarks>
    /// Configurable because the integration suite legitimately performs dozens
    /// of logins from one address and would otherwise rate-limit itself. The
    /// defaults are the real values, so an environment that sets nothing gets
    /// production behaviour — and <c>RateLimitingTests</c> deliberately
    /// configures a low limit to prove the limiter actually fires, rather than
    /// leaving it untested because it is inconvenient.
    /// </remarks>
    public sealed class Options
    {
        public int AuthPermitsPerWindow { get; set; } = 10;

        public int AuthWindowMinutes { get; set; } = 15;

        public int ReadTokenLimit { get; set; } = 200;

        public int ReadTokensPerPeriod { get; set; } = 100;

        public int WriteTokenLimit { get; set; } = 60;

        public int WriteTokensPerPeriod { get; set; } = 30;
    }

    public static IServiceCollection AddMautoDeskRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var limits = configuration.GetSection("RateLimits").Get<Options>() ?? new Options();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.OnRejected = async (context, cancellationToken) =>
            {
                // Retry-After is always present on a 429, per the contract. A
                // limiter that refuses without saying when to come back turns a
                // well-behaved client into a hot loop.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    context.HttpContext.Response.Headers.RetryAfter = "60";
                }

                // The contentType argument is load-bearing: setting
                // Response.ContentType beforehand is overwritten by
                // WriteAsJsonAsync, which would emit application/json and break
                // the "every non-2xx is problem+json" rule from
                // docs/04-api-contracts.md §4. A client branching on content type
                // would silently mis-handle a 429.
                await context.HttpContext.Response.WriteAsJsonAsync(
                    new
                    {
                        type = "https://api.mautodesk.com/problems/rate-limited",
                        title = "Too many requests.",
                        status = 429,
                        detail = "You are sending requests faster than we allow. Try again shortly.",
                        traceId = context.HttpContext.TraceIdentifier,
                    },
                    options: null,
                    contentType: "application/problem+json",
                    cancellationToken).ConfigureAwait(false);
            };

            // --- Authentication -------------------------------------------
            // Tight, and keyed on the client address. Deliberately does NOT key
            // on the submitted email: an attacker choosing a different address
            // each request would then get an unlimited budget, which is exactly
            // the credential-stuffing shape this is meant to stop.
            options.AddPolicy(AuthPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                ClientKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.AuthPermitsPerWindow,
                    Window = TimeSpan.FromMinutes(limits.AuthWindowMinutes),
                    QueueLimit = 0,
                }));

            // --- Reads ----------------------------------------------------
            // Per user when authenticated, per address otherwise. Generous: a
            // dealer scrolling an inventory grid legitimately makes bursts, and
            // a limiter that fires during normal work is one that gets removed.
            options.AddPolicy(ReadPolicy, context => RateLimitPartition.GetTokenBucketLimiter(
                PrincipalKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.ReadTokenLimit,
                    TokensPerPeriod = limits.ReadTokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));

            // --- Writes ---------------------------------------------------
            // Lower, and per tenant rather than per user: the resource being
            // protected is the tenant's database, and ten users in one
            // dealership share it.
            options.AddPolicy(WritePolicy, context => RateLimitPartition.GetTokenBucketLimiter(
                TenantKey(context),
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.WriteTokenLimit,
                    TokensPerPeriod = limits.WriteTokensPerPeriod,
                    ReplenishmentPeriod = TimeSpan.FromSeconds(10),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
        });

        return services;
    }

    /// <summary>
    /// The caller's address, as a partition key.
    /// </summary>
    /// <remarks>
    /// Resolved once at the edge by <see cref="ClientAddressMiddleware"/>, which
    /// believes a forwarded header only from a configured trusted proxy. That
    /// matters more here than anywhere else: a header trusted without checking
    /// the sender lets an attacker rotate it for an unlimited budget, and a
    /// header ignored behind a proxy collapses every caller into one bucket.
    /// Both turn the credential-stuffing control off without failing anything.
    /// </remarks>
    private static string ClientKey(HttpContext context) =>
        "ip:" + (ClientAddress.Of(context) ?? "unknown");

    private static string PrincipalKey(HttpContext context)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>();

        return tenant?.UserId is { } userId
            ? "user:" + userId
            : ClientKey(context);
    }

    private static string TenantKey(HttpContext context)
    {
        var tenant = context.RequestServices.GetService<ITenantContext>();

        return tenant?.TenantId is { } tenantId
            ? "tenant:" + tenantId
            : ClientKey(context);
    }
}
