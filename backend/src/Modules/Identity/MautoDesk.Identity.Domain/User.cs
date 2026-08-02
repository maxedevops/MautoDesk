using System.Text.RegularExpressions;
using MautoDesk.SharedKernel;

namespace MautoDesk.Identity.Domain;

/// <summary>An email address, normalized for comparison.</summary>
public readonly partial record struct EmailAddress
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static Result<EmailAddress> Create(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Error.Validation("email.required", "An email address is required.", "email");
        }

        var normalized = candidate.Trim().ToLowerInvariant();

        // Deliberately permissive. Strict RFC 5322 validation rejects addresses
        // that genuinely deliver, and turning away a real dealer to satisfy a
        // regex is a worse failure than accepting one that bounces.
        return normalized.Length > 320 || !EmailShape().IsMatch(normalized)
            ? Error.Validation("email.format", "That does not look like an email address.", "email")
            : new EmailAddress(normalized);
    }

    public override string ToString() => Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s.]+(\.[^@\s.]+)+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}

/// <summary>Why an authentication attempt failed.</summary>
/// <remarks>
/// Recorded internally for the security log. It is <b>never</b> returned to the
/// caller: distinguishing "no such account" from "wrong password" turns the
/// login endpoint into a user-enumeration oracle.
/// </remarks>
public enum LoginFailureReason
{
    UnknownUser,
    BadPassword,
    Locked,
    Suspended,
    MfaFailed,
}

public enum UserStatus
{
    Invited,
    Active,
    Suspended,
    Locked,
    Deactivated,
}

/// <summary>A person who signs in to a dealership's MautoDesk tenant.</summary>
public sealed class User : AggregateRoot
{
    /// <summary>Failed attempts before the account locks.</summary>
    public const int MaxFailedAttempts = 5;

    /// <summary>Base lockout, doubling with each subsequent lockout.</summary>
    /// <remarks>
    /// Exponential rather than fixed so a determined attacker is slowed
    /// geometrically, while a user who fatfingers their password twice is
    /// inconvenienced for seconds rather than locked out of their workday.
    /// </remarks>
    public static readonly TimeSpan BaseLockout = TimeSpan.FromMinutes(1);

    public static readonly TimeSpan MaxLockout = TimeSpan.FromHours(1);

    private User(Guid id, Guid tenantId, EmailAddress email, string firstName, string lastName)
        : base(id, tenantId)
    {
        Email = email.Value;
        FirstName = firstName;
        LastName = lastName;
    }

    /// <summary>Required by EF Core materialization.</summary>
    private User()
    {
    }

    public string Email { get; private set; } = string.Empty;

    public DateTimeOffset? EmailVerifiedAt { get; private set; }

    /// <summary>The Argon2id encoded hash. Null for SSO-only accounts.</summary>
    public string? PasswordHash { get; private set; }

    public string PasswordAlgorithm { get; private set; } = "argon2id";

    public DateTimeOffset? PasswordChangedAt { get; private set; }

    public bool MustChangePassword { get; private set; }

    public string FirstName { get; private set; } = string.Empty;

    public string LastName { get; private set; } = string.Empty;

    /// <summary>
    /// When the user completed MFA enrolment.
    /// </summary>
    /// <remarks>
    /// Null means they have not enrolled and <b>cannot complete a login</b>. MFA
    /// is mandatory for everyone with access to customer information under the
    /// FTC Safeguards Rule, so this is a platform obligation, not a tenant
    /// preference — there is deliberately no "MFA enabled" flag to turn off.
    /// </remarks>
    public DateTimeOffset? MfaEnrolledAt { get; private set; }

    public UserStatus Status { get; private set; } = UserStatus.Invited;

    public int FailedLoginCount { get; private set; }

    public int LockoutCount { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public string? Locale { get; private set; } = "en-US";

    public static Result<User> Create(
        Guid tenantId,
        EmailAddress email,
        string firstName,
        string lastName)
    {
        if (string.IsNullOrWhiteSpace(firstName) || string.IsNullOrWhiteSpace(lastName))
        {
            return Error.Validation("user.name.required", "A first and last name are required.");
        }

        return new User(Guid.CreateVersion7(), tenantId, email, firstName.Trim(), lastName.Trim());
    }

    public bool IsLockedAt(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void SetPassword(string encodedHash, DateTimeOffset now)
    {
        PasswordHash = encodedHash;
        PasswordAlgorithm = "argon2id";
        PasswordChangedAt = now;
        MustChangePassword = false;
    }

    /// <summary>
    /// Replaces the stored hash after a successful login under outdated parameters.
    /// </summary>
    /// <remarks>
    /// Argon2 cost parameters rise as hardware improves. Rehashing transparently
    /// on the next successful login upgrades the whole user base over time
    /// without a password reset — a reset would train users to expect
    /// unsolicited "change your password" emails, which is its own security
    /// problem.
    /// </remarks>
    public void UpgradePasswordHash(string encodedHash) => PasswordHash = encodedHash;

    public void Activate(DateTimeOffset now)
    {
        Status = UserStatus.Active;
        EmailVerifiedAt ??= now;
    }

    public void CompleteMfaEnrolment(DateTimeOffset now) => MfaEnrolledAt = now;

    /// <summary>Records a failed attempt and locks the account if warranted.</summary>
    public void RecordFailedLogin(DateTimeOffset now)
    {
        FailedLoginCount++;

        if (FailedLoginCount < MaxFailedAttempts)
        {
            return;
        }

        LockoutCount++;
        FailedLoginCount = 0;

        // Exponential backoff, capped. Uncapped doubling would eventually lock a
        // real user out for days, which turns a security control into a support
        // burden and pressures staff into sharing accounts.
        var multiplier = Math.Min(LockoutCount - 1, 6);
        var duration = TimeSpan.FromTicks(BaseLockout.Ticks * (1L << multiplier));

        LockedUntil = now + (duration > MaxLockout ? MaxLockout : duration);
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockoutCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
    }

    /// <summary>Whether this account may complete an authentication right now.</summary>
    public Result CanAuthenticate(DateTimeOffset now)
    {
        if (Status is UserStatus.Suspended or UserStatus.Deactivated)
        {
            return Error.Forbidden("auth.account_disabled", "This account is not active.");
        }

        return IsLockedAt(now)
            ? Error.Forbidden(
                "auth.locked",
                "This account is temporarily locked after repeated failed sign-in attempts. " +
                "Try again shortly or ask an administrator to unlock it.")
            : Result.Success();
    }
}

/// <summary>A confirmed second factor.</summary>
public sealed class MfaFactor : Entity
{
    private MfaFactor(Guid id, Guid tenantId, Guid userId, string type)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        Type = type;
    }

    private MfaFactor()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Type { get; private set; } = "totp";

    public string? Label { get; private set; }

    /// <summary>
    /// The TOTP shared secret, envelope-encrypted.
    /// </summary>
    /// <remarks>
    /// Stored as ciphertext because a leaked TOTP secret defeats the second
    /// factor entirely — an attacker with the database could mint valid codes
    /// forever without the user ever knowing.
    /// </remarks>
    public byte[]? SecretEncrypted { get; private set; }

    public string? SecretKeyId { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>
    /// The last accepted TOTP step, to stop code replay.
    /// </summary>
    /// <remarks>
    /// A TOTP code stays valid for its whole 30-second step. Without this, an
    /// attacker who observes a code — over the user's shoulder, or in a phishing
    /// proxy — can reuse it within that window. Recording the step makes each
    /// code strictly single-use.
    /// </remarks>
    public long? LastAcceptedStep { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsActive => RevokedAt is null && ConfirmedAt is not null;

    /// <summary>
    /// Starts a TOTP enrolment. The secret is attached separately.
    /// </summary>
    /// <remarks>
    /// Two steps because the ciphertext is bound to this factor's id as
    /// additional authenticated data — so the id has to exist before the secret
    /// can be encrypted. That binding is what stops a ciphertext being copied
    /// into another user's row, or another tenant's.
    /// </remarks>
    public static MfaFactor CreateTotp(Guid tenantId, Guid userId, string? label, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), tenantId, userId, "totp")
        {
            Label = label,
            CreatedAt = now,
        };

    public void SetSecret(byte[] ciphertext, string keyId)
    {
        SecretEncrypted = ciphertext;
        SecretKeyId = keyId;
    }

    public void Confirm(DateTimeOffset now, long step)
    {
        ConfirmedAt = now;
        LastUsedAt = now;
        LastAcceptedStep = step;
    }

    public void RecordUse(DateTimeOffset now, long step)
    {
        LastUsedAt = now;
        LastAcceptedStep = step;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}
