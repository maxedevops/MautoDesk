using System.Security.Claims;
using MautoDesk.Identity.Application;
using MautoDesk.Identity.Contracts;
using MautoDesk.Identity.Infrastructure;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Mvc;

namespace MautoDesk.Api;

/// <summary>Authentication surface.</summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var auth = app.MapGroup("/api/v1/auth")
            .WithTags("Authentication")
            // Tight per-IP limit: account lockout stops guessing at ONE account,
            // this is what stops credential stuffing across thousands.
            .RequireRateLimiting(RateLimiting.AuthPolicy);

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithName("login")
            .WithSummary("Authenticate with email and password")
            .WithDescription(
                "Returns tokens only when no second factor is outstanding. Because MFA is " +
                "mandatory, the normal responses are mfaRequired or mfaEnrolmentRequired. The " +
                "response for an unknown address is identical to the response for a wrong " +
                "password, and the timing is equalised, so this endpoint cannot be used to " +
                "discover which addresses have accounts.")
            .Produces<AuthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/mfa/verify", VerifyMfaAsync)
            .AllowAnonymous()
            .WithName("verifyMfa")
            .WithSummary("Complete authentication with a TOTP code")
            .Produces<AuthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/mfa/enrol", ConfirmEnrolmentAsync)
            .AllowAnonymous()
            .WithName("confirmMfaEnrolment")
            .WithSummary("Confirm a new authenticator and finish signing in")
            .Produces<AuthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/mfa/recovery", RedeemRecoveryCodeAsync)
            .AllowAnonymous()
            .WithName("redeemRecoveryCode")
            .WithSummary("Complete authentication with a single-use recovery code")
            .WithDescription(
                "For a user who has lost access to their authenticator. It is a second factor, " +
                "not a bypass: the challenge token from the password step is still required, each " +
                "code works exactly once, and a wrong code counts toward lockout exactly as a " +
                "wrong TOTP code does.")
            .Produces<AuthResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/mfa/recovery-codes", RegenerateRecoveryCodesAsync)
            .RequireAuthorization()
            .WithName("regenerateRecoveryCodes")
            .WithSummary("Issue a fresh set of recovery codes, discarding the old ones")
            .WithDescription(
                "The codes are returned in plaintext exactly once. They are stored hashed, so a " +
                "user who loses them has to generate a new set rather than retrieve the old one.")
            .Produces<RecoveryCodeSetDto>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        auth.MapGet("/mfa/recovery-codes", GetRecoveryCodeStatusAsync)
            .RequireAuthorization()
            .WithName("getRecoveryCodeStatus")
            .WithSummary("How many recovery codes remain unused")
            .Produces<RecoveryCodeStatusDto>(StatusCodes.Status200OK);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("refreshToken")
            .WithSummary("Exchange a refresh token for a new pair")
            .WithDescription(
                "Refresh tokens rotate on every use. Presenting a token that has already been " +
                "rotated is treated as evidence of theft: the entire token family is revoked and " +
                "a fresh sign-in is required.")
            .Produces<TokenPair>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        auth.MapPost("/logout", LogoutAsync)
            .AllowAnonymous()
            .WithName("logout")
            .WithSummary("Revoke the current session")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/me", MeAsync)
            .RequireAuthorization()
            .WithName("getCurrentUser")
            .WithSummary("The authenticated principal, tenant, and effective permissions")
            .Produces<CurrentUserDto>(StatusCodes.Status200OK);

        return app;
    }

    private static async Task<IResult> LoginAsync(
        [FromServices] AuthenticationService service,
        [FromBody] LoginRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await service
            .LoginAsync(
                new LoginCommand(request.Email, request.Password, ClientIp(context), UserAgent(context)),
                cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, context);
    }

    private static async Task<IResult> VerifyMfaAsync(
        [FromServices] AuthenticationService service,
        [FromBody] VerifyMfaRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await service
            .VerifyMfaAsync(
                new VerifyMfaCommand(request.ChallengeToken, request.Code, ClientIp(context), UserAgent(context)),
                cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, context);
    }

    private static async Task<IResult> ConfirmEnrolmentAsync(
        [FromServices] AuthenticationService service,
        [FromBody] VerifyMfaRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await service
            .ConfirmEnrolmentAsync(
                new EnrolMfaCommand(request.ChallengeToken, request.Code, ClientIp(context), UserAgent(context)),
                cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, context);
    }

    private static async Task<IResult> RedeemRecoveryCodeAsync(
        [FromServices] AuthenticationService service,
        [FromBody] RedeemRecoveryCodeRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await service
            .RedeemRecoveryCodeAsync(
                new RedeemRecoveryCodeCommand(
                    request.ChallengeToken, request.Code, ClientIp(context), UserAgent(context)),
                cancellationToken)
            .ConfigureAwait(false);

        return ToResponse(result, context);
    }

    private static async Task<IResult> RegenerateRecoveryCodesAsync(
        [FromServices] AuthenticationService service,
        [FromServices] ITenantContext tenant,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (tenant.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "You are not signed in.").ToProblem(context);
        }

        var result = await service
            .RegenerateRecoveryCodesAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> GetRecoveryCodeStatusAsync(
        [FromServices] AuthenticationService service,
        [FromServices] ITenantContext tenant,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (tenant.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "You are not signed in.").ToProblem(context);
        }

        var result = await service
            .GetRecoveryCodeStatusAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> RefreshAsync(
        [FromServices] AuthenticationService service,
        [FromBody] RefreshRequest request,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await service
            .RefreshAsync(new RefreshCommand(request.RefreshToken, ClientIp(context)), cancellationToken)
            .ConfigureAwait(false);

        return result.ToHttp(context);
    }

    private static async Task<IResult> LogoutAsync(
        [FromServices] AuthenticationService service,
        [FromBody] RefreshRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await service.LogoutAsync(request.RefreshToken, cancellationToken).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static async Task<IResult> MeAsync(
        [FromServices] IUserRepository users,
        [FromServices] ITenantContext tenant,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tenant);

        if (tenant.UserId is not { } userId)
        {
            return Error.Forbidden("auth.required", "You are not signed in.").ToProblem(context);
        }

        var user = await users.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return Error.Forbidden("auth.required", "You are not signed in.").ToProblem(context);
        }

        var tenantDto = await users.GetTenantAsync(tenant.RequireTenantId(), cancellationToken)
            .ConfigureAwait(false);
        var roles = await users.GetRoleNamesAsync(userId, cancellationToken).ConfigureAwait(false);

        return TypedResults.Ok(new CurrentUserDto(
            user.Id,
            user.Email,
            user.FirstName,
            user.LastName,
            $"{user.FirstName} {user.LastName}",
            tenantDto ?? new TenantDto(tenant.RequireTenantId(), string.Empty, string.Empty, null),
            roles,
            // Straight from the token, so what the UI hides matches exactly what
            // the server will enforce on the next call.
            [.. tenant.Permissions],
            user.MfaEnrolledAt is not null));
    }

    private static IResult ToResponse(Result<AuthResult> result, HttpContext context)
    {
        if (result.IsFailure)
        {
            return result.Error!.ToProblem(context);
        }

        var value = result.Value;

        return TypedResults.Ok(new AuthResponse(
            value.Outcome switch
            {
                AuthOutcome.Authenticated => "authenticated",
                AuthOutcome.MfaRequired => "mfa_required",
                AuthOutcome.MfaEnrolmentRequired => "mfa_enrolment_required",
                _ => "unknown",
            },
            value.Tokens,
            value.ChallengeToken,
            value.EnrolmentSecret,
            value.EnrolmentUri,
            value.RecoveryCodes));
    }

    /// <summary>
    /// The caller's address for the security log.
    /// </summary>
    /// <remarks>
    /// Cloudflare terminates TLS in front of the origin, so the socket address
    /// is Cloudflare's. CF-Connecting-IP is the real client — and it is trusted
    /// <em>only</em> because the origin is locked to Cloudflare
    /// (docs/02-architecture.md §10). On a directly-exposed origin this header
    /// would be attacker-controlled and must not be trusted.
    /// </remarks>
    private static string? ClientIp(HttpContext context)
    {
        var forwarded = context.Request.Headers["CF-Connecting-IP"].FirstOrDefault();

        // Parsed, not trusted verbatim. The header is caller-supplied, and the
        // column it lands in is `inet` — an unparseable value would turn a login
        // attempt into a 500. Falling back to the socket address is both safer
        // and more accurate when the header is junk.
        if (!string.IsNullOrWhiteSpace(forwarded) &&
            System.Net.IPAddress.TryParse(forwarded, out var parsed))
        {
            return parsed.ToString();
        }

        return context.Connection.RemoteIpAddress?.ToString();
    }

    private static string? UserAgent(HttpContext context) =>
        context.Request.Headers.UserAgent.FirstOrDefault();

    public sealed record LoginRequest(string? Email, string? Password);

    public sealed record VerifyMfaRequest(string? ChallengeToken, string? Code);

    public sealed record RefreshRequest(string? RefreshToken);

    public sealed record RedeemRecoveryCodeRequest(string? ChallengeToken, string? Code);

    /// <summary>The result of an authentication step.</summary>
    public sealed record AuthResponse(
        string Outcome,
        TokenPair? Tokens,
        string? ChallengeToken,
        string? EnrolmentSecret,
        string? EnrolmentUri,
        IReadOnlyList<string>? RecoveryCodes);
}

/// <summary>
/// Establishes the tenant scope from the validated access token.
/// </summary>
/// <remarks>
/// <b>This is the real implementation of ADR-0002.</b> The tenant comes from a
/// signed claim and from nothing else — no header, no subdomain, no query
/// parameter. A caller cannot choose their own tenant because they cannot forge
/// the signature.
/// </remarks>
public sealed class TenantClaimMiddleware
{
    private readonly RequestDelegate _next;

    public TenantClaimMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ITenantScopeSetter scopeSetter)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(scopeSetter);

        var principal = context.User;

        if (principal.Identity?.IsAuthenticated == true)
        {
            var tenantClaim = principal.FindFirstValue(MautoDeskClaims.Tenant);
            var subjectClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue("sub");

            if (Guid.TryParse(tenantClaim, out var tenantId))
            {
                Guid? userId = Guid.TryParse(subjectClaim, out var parsed) ? parsed : null;

                var permissions = principal.FindAll(MautoDeskClaims.Permission)
                    .Select(claim => claim.Value)
                    .ToHashSet(StringComparer.Ordinal);

                scopeSetter.SetScope(tenantId, userId, permissions);
            }

            // A token without a usable tenant claim leaves the scope unset, so
            // every RLS predicate denies. Fail closed rather than guessing.
        }

        await _next(context).ConfigureAwait(false);
    }
}
