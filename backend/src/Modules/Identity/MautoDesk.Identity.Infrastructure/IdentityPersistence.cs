using System.Reflection;
using MautoDesk.Identity.Application;
using MautoDesk.Identity.Contracts;
using MautoDesk.Identity.Domain;
using MautoDesk.Infrastructure.Persistence;
using MautoDesk.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MautoDesk.Identity.Infrastructure;

public sealed class IdentitySchema : IModuleSchema
{
    public Assembly ConfigurationAssembly => typeof(IdentitySchema).Assembly;
}

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user", "identity");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.TenantId).HasColumnName("tenant_id");
        builder.Property(u => u.Email).HasColumnName("email");
        builder.Property(u => u.EmailVerifiedAt).HasColumnName("email_verified_at");
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash");
        builder.Property(u => u.PasswordAlgorithm).HasColumnName("password_algorithm");
        builder.Property(u => u.PasswordChangedAt).HasColumnName("password_changed_at");
        builder.Property(u => u.MustChangePassword).HasColumnName("must_change_password");
        builder.Property(u => u.FirstName).HasColumnName("first_name");
        builder.Property(u => u.LastName).HasColumnName("last_name");
        builder.Property(u => u.MfaEnrolledAt).HasColumnName("mfa_enrolled_at");
        builder.Property(u => u.FailedLoginCount).HasColumnName("failed_login_count");
        builder.Property(u => u.LockoutCount).HasColumnName("lockout_count");
        builder.Property(u => u.LockedUntil).HasColumnName("locked_until");
        builder.Property(u => u.LastLoginAt).HasColumnName("last_login_at");
        builder.Property(u => u.Locale).HasColumnName("locale");
        builder.Property(u => u.CreatedAt).HasColumnName("created_at");
        builder.Property(u => u.CreatedBy).HasColumnName("created_by");
        builder.Property(u => u.UpdatedAt).HasColumnName("updated_at");
        builder.Property(u => u.UpdatedBy).HasColumnName("updated_by");
        builder.Property(u => u.DeletedAt).HasColumnName("deleted_at");
        builder.Property(u => u.DeletedBy).HasColumnName("deleted_by");

        builder.Property(u => u.Status)
            .HasColumnName("status")
            .HasConversion(s => s.ToString().ToLowerInvariant(), v => ParseStatus(v));

        builder.Ignore(u => u.DomainEvents);
        builder.HasQueryFilter(u => u.DeletedAt == null);
    }

    private static UserStatus ParseStatus(string value) => value switch
    {
        "invited" => UserStatus.Invited,
        "active" => UserStatus.Active,
        "suspended" => UserStatus.Suspended,
        "locked" => UserStatus.Locked,
        "deactivated" => UserStatus.Deactivated,
        _ => throw new InvalidOperationException($"'{value}' is not a user status this build understands."),
    };
}

public sealed class MfaFactorConfiguration : IEntityTypeConfiguration<MfaFactor>
{
    public void Configure(EntityTypeBuilder<MfaFactor> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("mfa_factor", "identity");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id");
        builder.Property(f => f.TenantId).HasColumnName("tenant_id");
        builder.Property(f => f.UserId).HasColumnName("user_id");
        builder.Property(f => f.Type).HasColumnName("type");
        builder.Property(f => f.Label).HasColumnName("label");
        builder.Property(f => f.SecretEncrypted).HasColumnName("secret_enc");
        builder.Property(f => f.SecretKeyId).HasColumnName("secret_kid");
        builder.Property(f => f.ConfirmedAt).HasColumnName("confirmed_at");
        builder.Property(f => f.LastUsedAt).HasColumnName("last_used_at");
        builder.Property(f => f.LastAcceptedStep).HasColumnName("last_accepted_step");
        builder.Property(f => f.CreatedAt).HasColumnName("created_at");
        builder.Property(f => f.RevokedAt).HasColumnName("revoked_at");
    }
}

public sealed class MfaRecoveryCodeConfiguration : IEntityTypeConfiguration<MfaRecoveryCode>
{
    public void Configure(EntityTypeBuilder<MfaRecoveryCode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("mfa_recovery_code", "identity");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id");
        builder.Property(c => c.TenantId).HasColumnName("tenant_id");
        builder.Property(c => c.UserId).HasColumnName("user_id");
        builder.Property(c => c.CodeHash).HasColumnName("code_hash");
        builder.Property(c => c.UsedAt).HasColumnName("used_at");
        builder.Property(c => c.CreatedAt).HasColumnName("created_at");
    }
}

public sealed class SessionConfiguration : IEntityTypeConfiguration<Session>
{
    /// <summary>
    /// Bridges a string address onto PostgreSQL''s inet type.
    /// </summary>
    /// <remarks>
    /// The domain holds an address as text because it is only ever recorded and
    /// displayed, never queried by subnet. The column is inet so the database can
    /// still validate and index it. Values reaching here are already validated at
    /// the edge — see AuthEndpoints.ClientIp — because inet rejects malformed
    /// input, and CF-Connecting-IP is caller-supplied.
    /// </remarks>
    internal static readonly ValueConverter<string?, System.Net.IPAddress?> IpConverter =
        new(value => value == null ? null : System.Net.IPAddress.Parse(value),
            value => value == null ? null : value.ToString());
    public void Configure(EntityTypeBuilder<Session> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("session", "identity");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id");
        builder.Property(s => s.UserId).HasColumnName("user_id");
        builder.Property(s => s.FamilyId).HasColumnName("family_id");
        builder.Property(s => s.CreatedAt).HasColumnName("created_at");
        builder.Property(s => s.LastSeenAt).HasColumnName("last_seen_at");
        builder.Property(s => s.ExpiresAt).HasColumnName("expires_at");
        builder.Property(s => s.RevokedAt).HasColumnName("revoked_at");
        builder.Property(s => s.RevokedReason).HasColumnName("revoked_reason");
        // The column is inet, which Npgsql maps to IPAddress, not string.
        builder.Property(s => s.IpAddress).HasColumnName("ip_address").HasConversion(IpConverter);
        builder.Property(s => s.UserAgent).HasColumnName("user_agent");
        builder.Property(s => s.DeviceLabel).HasColumnName("device_label");
        builder.Property(s => s.MfaSatisfiedAt).HasColumnName("mfa_satisfied_at");
        builder.Property(s => s.Amr).HasColumnName("amr");

        // The session table has no updated_at/deleted_at columns; the base class
        // properties are mapped away rather than left to fail at runtime.
        builder.Ignore(s => s.UpdatedAt);
        builder.Ignore(s => s.UpdatedBy);
        builder.Ignore(s => s.DeletedAt);
        builder.Ignore(s => s.DeletedBy);
        builder.Ignore(s => s.CreatedBy);
        builder.Ignore(s => s.DomainEvents);
    }
}

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    private static ValueConverter<string?, System.Net.IPAddress?> IpConverter => SessionConfiguration.IpConverter;

    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_token", "identity");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).HasColumnName("id");
        builder.Property(t => t.TenantId).HasColumnName("tenant_id");
        builder.Property(t => t.SessionId).HasColumnName("session_id");
        builder.Property(t => t.FamilyId).HasColumnName("family_id");
        builder.Property(t => t.TokenHash).HasColumnName("token_hash");
        builder.Property(t => t.IssuedAt).HasColumnName("issued_at");
        builder.Property(t => t.ExpiresAt).HasColumnName("expires_at");
        builder.Property(t => t.RotatedAt).HasColumnName("rotated_at");
        builder.Property(t => t.ReplacedBy).HasColumnName("replaced_by");
        builder.Property(t => t.RevokedAt).HasColumnName("revoked_at");
        builder.Property(t => t.UsedFromIp).HasColumnName("used_from_ip").HasConversion(IpConverter);

        // Declares the dependency so EF orders the inserts correctly. Without it
        // EF has no idea a refresh token needs its session to exist first and
        // will happily try the child row first, which the foreign key rejects.
        // No navigation property: the aggregate boundary stays intact.
        builder.HasOne<Session>()
            .WithMany()
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Rotation points a token at its successor, so the successor must be
        // inserted before the predecessor is updated. Declaring the
        // self-reference is what tells EF that ordering; without it the update
        // lands first and the foreign key rejects it.
        builder.HasOne<RefreshToken>()
            .WithMany()
            .HasForeignKey(t => t.ReplacedBy)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/* ------------------------------------------------------------ repositories -- */

/// <summary>User and permission reads for authentication.</summary>
public sealed class UserRepository : IUserRepository
{
    private readonly MautoDeskDbContext _db;
    private readonly ITenantScopeSetter _scopeSetter;

    public UserRepository(MautoDeskDbContext db, ITenantScopeSetter scopeSetter)
    {
        _db = db;
        _scopeSetter = scopeSetter;
    }

    /// <summary>
    /// Finds the user behind a login, then establishes their tenant scope.
    /// </summary>
    /// <remarks>
    /// Goes through the SECURITY DEFINER function from V0008 because at this
    /// moment there is no tenant and RLS would deny everything. Having found the
    /// user, it immediately sets the tenant on both the ambient context and the
    /// database session, so every subsequent query in the request — factors,
    /// permissions, session writes — runs fully tenant-scoped again.
    ///
    /// The cross-tenant window is therefore exactly one function call wide.
    /// </remarks>
    public async Task<User?> FindByEmailForAuthenticationAsync(
        string email,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQueryRaw<AuthLookupRow>(
                """
                select user_id as "UserId", tenant_id as "TenantId"
                  from identity.find_user_for_authentication({0}::citext)
                """,
                email)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var found = rows.FirstOrDefault();
        if (found is null)
        {
            return null;
        }

        _scopeSetter.SetScope(found.TenantId, found.UserId);
        await ApplyDatabaseScopeAsync(found.TenantId, found.UserId, cancellationToken).ConfigureAwait(false);

        // Everything else — the password hash, status, lockout state — is read
        // through the ordinary tenant-scoped, tracked path now that the tenant is
        // known. The privileged function above learned only *which* tenant, which
        // keeps the cross-tenant surface to two columns.
        return await _db.Set<User>()
            .FirstOrDefaultAsync(u => u.Id == found.UserId, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Set<User>().FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public async Task EstablishScopeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken)
    {
        _scopeSetter.SetScope(tenantId, userId);
        await ApplyDatabaseScopeAsync(tenantId, userId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<MfaFactor>> GetActiveFactorsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _db.Set<MfaFactor>()
            .Where(f => f.UserId == userId && f.RevokedAt == null && f.ConfirmedAt != null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<MfaFactor?> GetPendingTotpFactorAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Set<MfaFactor>()
            .Where(f => f.UserId == userId && f.RevokedAt == null && f.ConfirmedAt == null && f.Type == "totp")
            .OrderByDescending(f => f.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

    public void AddFactor(MfaFactor factor) => _db.Set<MfaFactor>().Add(factor);

    public void AddRecoveryCodes(IEnumerable<MfaRecoveryCode> codes) =>
        _db.Set<MfaRecoveryCode>().AddRange(codes);

    public Task<MfaRecoveryCode?> FindUnusedRecoveryCodeAsync(
        Guid userId,
        string codeHash,
        CancellationToken cancellationToken) =>
        _db.Set<MfaRecoveryCode>()
            .FirstOrDefaultAsync(
                c => c.UserId == userId && c.CodeHash == codeHash && c.UsedAt == null,
                cancellationToken);

    public Task<int> CountUnusedRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken) =>
        _db.Set<MfaRecoveryCode>()
            .CountAsync(c => c.UserId == userId && c.UsedAt == null, cancellationToken);

    /// <summary>
    /// Removes unspent codes through the change tracker rather than ExecuteDelete.
    /// </summary>
    /// <remarks>
    /// Regeneration deletes the old set and inserts the new one, and those two
    /// have to land together — ExecuteDelete would run immediately, outside the
    /// SaveChanges transaction, leaving a user with no codes at all if the
    /// insert then failed. The set is ten rows, so the tracked path costs
    /// nothing.
    /// </remarks>
    public async Task DiscardUnusedRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken)
    {
        var existing = await _db.Set<MfaRecoveryCode>()
            .Where(c => c.UserId == userId && c.UsedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        _db.Set<MfaRecoveryCode>().RemoveRange(existing);
    }

    public async Task<IReadOnlyList<string>> GetPermissionsAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _db.Database
            .SqlQueryRaw<string>(
                """
                select distinct rp.permission_code as "Value"
                  from identity.user_role ur
                  join identity.role_permission rp on rp.role_id = ur.role_id
                 where ur.user_id = {0}
                 order by 1
                """,
                userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<string>> GetRoleNamesAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _db.Database
            .SqlQueryRaw<string>(
                """
                select r.name as "Value"
                  from identity.user_role ur
                  join identity.role r on r.id = ur.role_id
                 where ur.user_id = {0}
                 order by 1
                """,
                userId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    public async Task<TenantDto?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQueryRaw<TenantRow>(
                """
                select id as "Id", slug as "Slug", legal_name as "LegalName", state_code as "StateCode"
                  from platform.tenant where id = {0}
                """,
                tenantId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var row = rows.FirstOrDefault();
        return row is null ? null : new TenantDto(row.Id, row.Slug, row.LegalName, row.StateCode);
    }

    public Task RecordLoginAttemptAsync(
        Guid? tenantId,
        string email,
        bool succeeded,
        LoginFailureReason? reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken) =>
        _db.Database.ExecuteSqlRawAsync(
            "select identity.record_login_attempt(@tenant, @email::citext, @ok, @reason, @ip::inet, @ua)",
            // Explicit DbParameter instances: EF's raw-SQL overload takes
            // IEnumerable<object>, which cannot carry a null, and DBNull.Value
            // inside that array makes EF throw. Most of these are legitimately
            // null — a failed attempt for an unknown address has no tenant.
            new[]
            {
                Db("tenant", tenantId),
                Db("email", email),
                Db("ok", succeeded),
                Db("reason", reason is null ? null : ToWire(reason.Value)),
                Db("ip", string.IsNullOrWhiteSpace(ipAddress) ? null : ipAddress),
                Db("ua", string.IsNullOrWhiteSpace(userAgent) ? null : userAgent),
            },
            cancellationToken);

    private static Npgsql.NpgsqlParameter Db(string name, object? value) =>
        new(name, value ?? DBNull.Value);

    private async Task ApplyDatabaseScopeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken) =>
        await _db.Database.ExecuteSqlRawAsync(
            "select set_config('app.tenant_id', {0}, false), set_config('app.user_id', {1}, false)",
            [tenantId.ToString(), userId.ToString()],
            cancellationToken).ConfigureAwait(false);

    private static string ToWire(LoginFailureReason reason) => reason switch
    {
        LoginFailureReason.UnknownUser => "unknown_user",
        LoginFailureReason.BadPassword => "bad_password",
        LoginFailureReason.Locked => "locked",
        LoginFailureReason.Suspended => "suspended",
        LoginFailureReason.MfaFailed => "mfa_failed",
        _ => "unknown",
    };

    private sealed record TenantRow(Guid Id, string Slug, string LegalName, string? StateCode);

    /// <summary>The two columns the privileged login lookup is allowed to return.</summary>
    private sealed record AuthLookupRow(Guid UserId, Guid TenantId);
}

/// <summary>Sessions and refresh-token families.</summary>
public sealed class SessionRepository : ISessionRepository
{
    private readonly MautoDeskDbContext _db;
    private readonly ITenantScopeSetter _scopeSetter;

    public SessionRepository(MautoDeskDbContext db, ITenantScopeSetter scopeSetter)
    {
        _db = db;
        _scopeSetter = scopeSetter;
    }

    public void Add(Session session) => _db.Set<Session>().Add(session);

    public void AddToken(RefreshToken token) => _db.Set<RefreshToken>().Add(token);

    public async Task<bool> EstablishScopeForTokenAsync(byte[] hash, CancellationToken cancellationToken)
    {
        var rows = await _db.Database
            .SqlQueryRaw<Guid?>(
                """select identity.find_refresh_token_tenant({0}) as "Value" """,
                hash)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var tenantId = rows.FirstOrDefault();
        if (tenantId is null || tenantId == Guid.Empty)
        {
            return false;
        }

        _scopeSetter.SetScope(tenantId.Value);

        await _db.Database.ExecuteSqlRawAsync(
            "select set_config('app.tenant_id', {0}, false)",
            [tenantId.Value.ToString()],
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    public Task<RefreshToken?> FindByHashAsync(byte[] hash, CancellationToken cancellationToken) =>
        _db.Set<RefreshToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

    public Task<Session?> GetAsync(Guid sessionId, CancellationToken cancellationToken) =>
        _db.Set<Session>().FirstOrDefaultAsync(s => s.Id == sessionId, cancellationToken);

    public async Task<IReadOnlyList<Session>> GetActiveForUserAsync(
        Guid userId,
        CancellationToken cancellationToken) =>
        await _db.Set<Session>()
            .Where(s => s.UserId == userId && s.RevokedAt == null)
            .OrderByDescending(s => s.LastSeenAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Revokes a session and every refresh token descended from it.
    /// </summary>
    /// <remarks>
    /// Called on logout and — more importantly — when a rotated token is
    /// replayed. Revoking the family rather than the single token is what makes
    /// theft self-limiting: the attacker's stolen token dies along with the
    /// legitimate one, and the real user is forced to re-authenticate.
    /// </remarks>
    public async Task RevokeFamilyAsync(
        Guid familyId,
        DateTimeOffset now,
        string reason,
        CancellationToken cancellationToken)
    {
        var tokens = await _db.Set<RefreshToken>()
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var token in tokens)
        {
            token.Revoke(now);
        }

        var sessions = await _db.Set<Session>()
            .Where(s => s.FamilyId == familyId && s.RevokedAt == null)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var session in sessions)
        {
            session.Revoke(now, reason);
        }
    }
}

/// <summary>Commits the unit of work for the Identity module.</summary>
public sealed class IdentityUnitOfWork : IUnitOfWork
{
    private readonly MautoDeskDbContext _db;

    public IdentityUnitOfWork(MautoDeskDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken) =>
        _db.SaveChangesAsync(cancellationToken);
}
