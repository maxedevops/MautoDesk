using MautoDesk.SharedKernel;

namespace MautoDesk.Identity.Domain;

/// <summary>Why a session ended.</summary>
public static class RevocationReason
{
    public const string Logout = "logout";

    /// <summary>
    /// A refresh token was presented after it had already been rotated.
    /// </summary>
    /// <remarks>
    /// The important one. A rotated token being replayed means two parties hold
    /// it — the legitimate client and someone else. Since we cannot tell which
    /// is which, the only safe response is to revoke the entire family and force
    /// a fresh authentication.
    /// </remarks>
    public const string RotationReuse = "rotation_reuse";

    public const string Admin = "admin";
    public const string PasswordChange = "password_change";
    public const string Expiry = "expiry";
}

/// <summary>One login. Owns a family of refresh tokens.</summary>
public sealed class Session : AggregateRoot
{
    private Session(Guid id, Guid tenantId, Guid userId, Guid familyId)
        : base(id, tenantId)
    {
        UserId = userId;
        FamilyId = familyId;
    }

    private Session()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>Groups every refresh token descended from this one login.</summary>
    public Guid FamilyId { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public string? DeviceLabel { get; private set; }

    public DateTimeOffset? MfaSatisfiedAt { get; private set; }

    /// <summary>Authentication methods used, e.g. <c>pwd</c>, <c>totp</c>.</summary>
    public string[] Amr { get; private set; } = [];

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public static Session Start(
        Guid tenantId,
        Guid userId,
        DateTimeOffset now,
        TimeSpan lifetime,
        string[] amr,
        string? ipAddress,
        string? userAgent)
    {
        return new Session(Guid.CreateVersion7(), tenantId, userId, Guid.CreateVersion7())
        {
            LastSeenAt = now,
            ExpiresAt = now + lifetime,
            MfaSatisfiedAt = amr.Contains("totp", StringComparer.Ordinal) ? now : null,
            Amr = amr,
            IpAddress = ipAddress,
            UserAgent = userAgent,
        };
    }

    public void Touch(DateTimeOffset now) => LastSeenAt = now;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }
}

/// <summary>
/// A refresh token, stored only as a hash.
/// </summary>
/// <remarks>
/// The plaintext exists exactly once, in the response that issues it. A stolen
/// database therefore yields no usable tokens — the same reasoning as password
/// storage, applied to a credential that is often overlooked because it is
/// machine-generated.
/// </remarks>
public sealed class RefreshToken : Entity
{
    private RefreshToken(Guid id, Guid tenantId, Guid sessionId, Guid familyId, byte[] tokenHash)
        : base(id)
    {
        TenantId = tenantId;
        SessionId = sessionId;
        FamilyId = familyId;
        TokenHash = tokenHash;
    }

    private RefreshToken()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid SessionId { get; private set; }

    public Guid FamilyId { get; private set; }

    public byte[] TokenHash { get; private set; } = [];

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when this token was exchanged for a successor.</summary>
    public DateTimeOffset? RotatedAt { get; private set; }

    public Guid? ReplacedBy { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? UsedFromIp { get; private set; }

    public bool IsRotated => RotatedAt is not null;

    public bool IsUsable(DateTimeOffset now) =>
        RevokedAt is null && RotatedAt is null && ExpiresAt > now;

    public static RefreshToken Issue(
        Guid tenantId,
        Guid sessionId,
        Guid familyId,
        byte[] tokenHash,
        DateTimeOffset now,
        TimeSpan lifetime,
        string? ipAddress) =>
        new(Guid.CreateVersion7(), tenantId, sessionId, familyId, tokenHash)
        {
            IssuedAt = now,
            ExpiresAt = now + lifetime,
            UsedFromIp = ipAddress,
        };

    public void Rotate(DateTimeOffset now, Guid successorId)
    {
        RotatedAt = now;
        ReplacedBy = successorId;
    }

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;
}
