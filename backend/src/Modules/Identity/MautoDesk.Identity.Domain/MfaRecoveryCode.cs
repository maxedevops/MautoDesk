using MautoDesk.SharedKernel;

namespace MautoDesk.Identity.Domain;

/// <summary>
/// One single-use code that stands in for the authenticator app.
/// </summary>
/// <remarks>
/// MFA is mandatory and a phone is a thing people lose, break, and replace. The
/// alternative to a recovery code is an administrator turning a second factor
/// off for someone who says on the phone that they are locked out — which is
/// the social-engineering path this whole control exists to close. Codes are
/// issued as a set, shown exactly once, and stored only as a hash.
/// </remarks>
public sealed class MfaRecoveryCode : Entity
{
    /// <summary>How many codes are issued in one set.</summary>
    /// <remarks>
    /// Ten is enough that a user who burns one on a lost phone, one on a new
    /// laptop, and one on a mistake still has a comfortable margin before they
    /// need a new set — and few enough that verifying a submitted code against
    /// every unused row stays a single indexed lookup.
    /// </remarks>
    public const int SetSize = 10;

    private MfaRecoveryCode(Guid id, Guid tenantId, Guid userId, string codeHash, DateTimeOffset now)
        : base(id)
    {
        TenantId = tenantId;
        UserId = userId;
        CodeHash = codeHash;
        CreatedAt = now;
    }

    /// <summary>Required by EF Core materialization.</summary>
    private MfaRecoveryCode()
    {
    }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    /// <summary>The hash of the code. The code itself is never stored.</summary>
    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset? UsedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public bool IsUsable => UsedAt is null;

    public static MfaRecoveryCode Issue(Guid tenantId, Guid userId, string codeHash, DateTimeOffset now) =>
        new(Guid.CreateVersion7(), tenantId, userId, codeHash, now);

    /// <summary>Spends the code. A second attempt with the same code fails.</summary>
    public Result Redeem(DateTimeOffset now)
    {
        if (UsedAt is not null)
        {
            return Error.Forbidden(
                "auth.recovery_code_used",
                "That recovery code has already been used. Each code works once.");
        }

        UsedAt = now;
        return Result.Success();
    }
}
