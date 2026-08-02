namespace MautoDesk.Identity.Contracts;

/// <summary>
/// What the caller must do next.
/// </summary>
/// <remarks>
/// Deliberately explicit rather than "success/failure". MFA is mandatory, so
/// a correct password is only ever a step — never a completed login — and the
/// client has to be told which step it is on.
/// </remarks>
public enum AuthOutcome
{
    /// <summary>Authenticated. Tokens are attached.</summary>
    Authenticated,

    /// <summary>Password accepted; a TOTP code is required.</summary>
    MfaRequired,

    /// <summary>
    /// Password accepted, but this account has no second factor yet.
    /// </summary>
    /// <remarks>
    /// The account cannot finish signing in until it enrols. Under the FTC
    /// Safeguards Rule, MFA is required for everyone with access to customer
    /// information — so there is no path that skips this.
    /// </remarks>
    MfaEnrolmentRequired,
}

/// <summary>The result of a login or MFA step.</summary>
public sealed record AuthResult(
    AuthOutcome Outcome,
    TokenPair? Tokens,
    string? ChallengeToken,
    string? EnrolmentSecret,
    string? EnrolmentUri,

    // Plaintext recovery codes, populated only on the response that issues them
    // — the enrolment that created them, or an explicit regeneration. They are
    // stored hashed, so this is the only time they can ever be shown.
    IReadOnlyList<string>? RecoveryCodes = null);

/// <summary>A newly issued set of recovery codes, shown once.</summary>
public sealed record RecoveryCodeSetDto(IReadOnlyList<string> Codes, DateTimeOffset IssuedAt);

/// <summary>How many recovery codes are left, for the "you are running low" nudge.</summary>
public sealed record RecoveryCodeStatusDto(int Remaining, int SetSize);

/// <summary>An access token and the refresh token that renews it.</summary>
/// <remarks>
/// Both are returned exactly once. The refresh token is stored only as a hash,
/// so it can never be recovered from the database.
/// </remarks>
public sealed record TokenPair(
    string AccessToken,
    string RefreshToken,
    string TokenType,
    int ExpiresIn);

/// <summary>The signed-in principal.</summary>
public sealed record CurrentUserDto(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    string DisplayName,
    TenantDto Tenant,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    bool MfaEnrolled);

public sealed record TenantDto(Guid Id, string Slug, string LegalName, string? StateCode);

/// <summary>An active session, as shown in "where am I signed in".</summary>
public sealed record SessionDto(
    Guid Id,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset ExpiresAt,
    string? IpAddress,
    string? DeviceLabel,
    bool IsCurrent);
