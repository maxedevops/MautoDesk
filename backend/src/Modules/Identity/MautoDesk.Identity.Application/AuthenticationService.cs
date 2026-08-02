using MautoDesk.Identity.Contracts;
using MautoDesk.Identity.Domain;
using MautoDesk.SharedKernel;

namespace MautoDesk.Identity.Application;

/* ------------------------------------------------------------------- ports -- */

public interface IPasswordHasher
{
    public string Hash(string password);

    /// <summary>Verifies a password in constant time relative to the hash.</summary>
    public bool Verify(string password, string encodedHash);

    /// <summary>True when the hash used weaker parameters than we now require.</summary>
    public bool NeedsRehash(string encodedHash);
}

public interface ITotpService
{
    /// <summary>Generates a new shared secret, base32-encoded.</summary>
    public string GenerateSecret();

    /// <summary>The otpauth:// URI an authenticator app scans.</summary>
    public string BuildEnrolmentUri(string secret, string email, string issuer);

    /// <summary>
    /// Validates a code, returning the time step it matched.
    /// </summary>
    /// <returns>The matched step, or null when the code is not valid.</returns>
    public long? Validate(string secret, string code, DateTimeOffset now);
}

public interface IRecoveryCodeService
{
    /// <summary>Generates one recovery code in its display form.</summary>
    public string Generate();

    /// <summary>
    /// Hashes a code for storage and comparison.
    /// </summary>
    /// <remarks>
    /// Deterministic and unsalted on purpose: a code is ~50 bits of uniform
    /// randomness that we generated, not a human-chosen password, so there is
    /// nothing for a slow KDF to defend and an unsalted digest lets the lookup
    /// be a single indexed comparison instead of an Argon2 run against every
    /// unused row.
    /// </remarks>
    public string Hash(string code);

    /// <summary>Strips formatting so "abcde-fghij" and "ABCDEFGHIJ" match.</summary>
    public string Normalize(string code);
}

public interface ITokenIssuer
{
    public string IssueAccessToken(
        Guid tenantId,
        Guid userId,
        string email,
        IReadOnlyCollection<string> permissions,
        Guid sessionId,
        DateTimeOffset now);

    /// <summary>A short-lived token proving the password step was passed.</summary>
    public string IssueChallengeToken(Guid tenantId, Guid userId, string purpose, DateTimeOffset now);

    public Result<ChallengePrincipal> ValidateChallengeToken(string token, string expectedPurpose);

    /// <summary>Opaque refresh token: (plaintext, sha256 hash).</summary>
    public (string Plaintext, byte[] Hash) CreateRefreshToken();

    public byte[] HashRefreshToken(string plaintext);

    public TimeSpan AccessTokenLifetime { get; }

    public TimeSpan RefreshTokenLifetime { get; }
}

public sealed record ChallengePrincipal(Guid TenantId, Guid UserId);

public interface IUserRepository
{
    /// <summary>
    /// Finds a user by email across all tenants.
    /// </summary>
    /// <remarks>
    /// The one query in the system that legitimately crosses the tenant
    /// boundary: at login there is no tenant context yet, because the tenant is
    /// derived <em>from</em> the user. It runs on a dedicated elevated
    /// connection, is confined to this method, and is covered by its own test.
    /// </remarks>
    public Task<User?> FindByEmailForAuthenticationAsync(string email, CancellationToken cancellationToken);

    public Task<User?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Establishes the tenant scope for a half-finished login.
    /// </summary>
    /// <remarks>
    /// The MFA endpoints carry a challenge token, not a bearer token, so no
    /// principal exists yet and the tenant middleware has set nothing. Without
    /// this, every row-level security predicate denies and the user cannot even
    /// be read back to check their code. The tenant comes from the SIGNED
    /// challenge token, so it is still never caller-supplied.
    /// </remarks>
    public Task EstablishScopeAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<MfaFactor>> GetActiveFactorsAsync(Guid userId, CancellationToken cancellationToken);

    public Task<MfaFactor?> GetPendingTotpFactorAsync(Guid userId, CancellationToken cancellationToken);

    public void AddFactor(MfaFactor factor);

    public void AddRecoveryCodes(IEnumerable<MfaRecoveryCode> codes);

    /// <summary>Finds an unspent code by its hash. Null when it is unknown or already spent.</summary>
    public Task<MfaRecoveryCode?> FindUnusedRecoveryCodeAsync(
        Guid userId,
        string codeHash,
        CancellationToken cancellationToken);

    public Task<int> CountUnusedRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    /// Discards every unspent code for a user, so a new set replaces the old one.
    /// </summary>
    /// <remarks>
    /// Regeneration has to invalidate what came before, or a printout the user
    /// threw away because they generated new codes still opens the account.
    /// </remarks>
    public Task DiscardUnusedRecoveryCodesAsync(Guid userId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<string>> GetRoleNamesAsync(Guid userId, CancellationToken cancellationToken);

    public Task<TenantDto?> GetTenantAsync(Guid tenantId, CancellationToken cancellationToken);

    public Task RecordLoginAttemptAsync(
        Guid? tenantId,
        string email,
        bool succeeded,
        LoginFailureReason? reason,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken);
}

public interface ISessionRepository
{
    public void Add(Session session);

    public void AddToken(RefreshToken token);

    /// <summary>
    /// Resolves the tenant that owns a refresh-token hash, then scopes to it.
    /// </summary>
    /// <remarks>
    /// /auth/refresh and /auth/logout are anonymous by necessity — the caller has
    /// only an opaque token — so no tenant scope exists and RLS would deny the
    /// lookup outright. This establishes the scope from the token itself; every
    /// validity decision after it runs under ordinary tenant-scoped policies.
    /// </remarks>
    public Task<bool> EstablishScopeForTokenAsync(byte[] hash, CancellationToken cancellationToken);

    public Task<RefreshToken?> FindByHashAsync(byte[] hash, CancellationToken cancellationToken);

    public Task<Session?> GetAsync(Guid sessionId, CancellationToken cancellationToken);

    public Task<IReadOnlyList<Session>> GetActiveForUserAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Revokes an entire family after a reuse is detected.</summary>
    public Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, string reason, CancellationToken cancellationToken);
}

public interface ISecretProtector
{
    public (byte[] Ciphertext, string KeyId) Protect(string plaintext, Guid tenantId, Guid recordId);

    public string Unprotect(byte[] ciphertext, string keyId, Guid tenantId, Guid recordId);
}

/* ---------------------------------------------------------------- commands -- */

public sealed record LoginCommand(string? Email, string? Password, string? IpAddress, string? UserAgent);

public sealed record VerifyMfaCommand(string? ChallengeToken, string? Code, string? IpAddress, string? UserAgent);

public sealed record EnrolMfaCommand(string? ChallengeToken, string? Code, string? IpAddress, string? UserAgent);

public sealed record RefreshCommand(string? RefreshToken, string? IpAddress);

public sealed record RedeemRecoveryCodeCommand(
    string? ChallengeToken,
    string? Code,
    string? IpAddress,
    string? UserAgent);

/* ----------------------------------------------------------------- service -- */

/// <summary>
/// Password, MFA, and token lifecycle.
/// </summary>
/// <remarks>
/// The security decisions concentrated here, each commented at its site:
/// no user enumeration, mandatory MFA, refresh rotation with reuse detection,
/// TOTP replay prevention, and exponential lockout.
/// </remarks>
public sealed class AuthenticationService
{
    private const string ChallengePurposeMfa = "mfa";
    private const string ChallengePurposeEnrol = "mfa_enrol";
    private const string Issuer = "MautoDesk";

    /// <summary>
    /// A valid Argon2id hash of a random value, used when no account exists.
    /// </summary>
    /// <remarks>
    /// Verifying against this makes the "unknown user" path cost the same as the
    /// "wrong password" path. Without it, an unknown address returns in
    /// microseconds while a known one takes ~50 ms of Argon2 work — a timing
    /// difference big enough to enumerate a dealership's staff over the
    /// internet, which is a phishing target list.
    /// </remarks>
    private readonly string _decoyHash;

    private readonly IUserRepository _users;
    private readonly ISessionRepository _sessions;
    private readonly IPasswordHasher _passwords;
    private readonly ITotpService _totp;
    private readonly ITokenIssuer _tokens;
    private readonly ISecretProtector _protector;
    private readonly IRecoveryCodeService _recoveryCodes;
    private readonly IUnitOfWork _unitOfWork;
    private readonly IClock _clock;

    public AuthenticationService(
        IUserRepository users,
        ISessionRepository sessions,
        IPasswordHasher passwords,
        ITotpService totp,
        ITokenIssuer tokens,
        ISecretProtector protector,
        IRecoveryCodeService recoveryCodes,
        IUnitOfWork unitOfWork,
        IClock clock)
    {
        _users = users;
        _sessions = sessions;
        _passwords = passwords;
        _totp = totp;
        _tokens = tokens;
        _protector = protector;
        _recoveryCodes = recoveryCodes;
        _unitOfWork = unitOfWork;
        _clock = clock;
        _decoyHash = passwords.Hash(Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// A single, deliberately vague failure.
    /// </summary>
    /// <remarks>
    /// Returned for an unknown address, a wrong password, and a disabled
    /// account alike. The specific reason goes to the security log; telling the
    /// caller which one it was would confirm whether an address has an account.
    /// </remarks>
    private static Error InvalidCredentials() => Error.Forbidden(
        "auth.invalid_credentials",
        "That email address and password combination is not correct.");

    public async Task<Result<AuthResult>> LoginAsync(
        LoginCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var email = EmailAddress.Create(command.Email);
        if (email.IsFailure)
        {
            return InvalidCredentials();
        }

        if (string.IsNullOrEmpty(command.Password))
        {
            return InvalidCredentials();
        }

        var now = _clock.UtcNow;
        var user = await _users
            .FindByEmailForAuthenticationAsync(email.Value.Value, cancellationToken)
            .ConfigureAwait(false);

        if (user is null)
        {
            // Do the work anyway, then fail. See _decoyHash.
            _ = _passwords.Verify(command.Password, _decoyHash);

            await RecordAttemptAsync(
                null, email.Value.Value, false, LoginFailureReason.UnknownUser, command, cancellationToken)
                .ConfigureAwait(false);

            return InvalidCredentials();
        }

        var eligibility = user.CanAuthenticate(now);
        if (eligibility.IsFailure)
        {
            await RecordAttemptAsync(
                user.TenantId,
                email.Value.Value,
                false,
                user.IsLockedAt(now) ? LoginFailureReason.Locked : LoginFailureReason.Suspended,
                command,
                cancellationToken).ConfigureAwait(false);

            // Lockout IS surfaced, unlike a wrong password. A locked-out user
            // needs to know why they cannot get in, and by this point the
            // attacker already supplied a correct-looking address — the
            // enumeration horse has bolted, and usability wins.
            return eligibility.Error!;
        }

        if (user.PasswordHash is null ||
            !_passwords.Verify(command.Password, user.PasswordHash))
        {
            user.RecordFailedLogin(now);

            await RecordAttemptAsync(
                user.TenantId, email.Value.Value, false, LoginFailureReason.BadPassword, command, cancellationToken)
                .ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return InvalidCredentials();
        }

        // Correct password under outdated cost parameters: upgrade silently.
        if (_passwords.NeedsRehash(user.PasswordHash))
        {
            user.UpgradePasswordHash(_passwords.Hash(command.Password));
        }

        var factors = await _users.GetActiveFactorsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        // MFA is mandatory. A correct password alone never yields tokens.
        if (factors.Count == 0)
        {
            var enrolment = await BeginEnrolmentAsync(user, now, cancellationToken).ConfigureAwait(false);

            // Saved AFTER enrolment begins: the pending factor is created inside
            // BeginEnrolmentAsync, so committing first would return a secret to
            // the user that was never persisted, and the confirmation step would
            // then find nothing to confirm.
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return enrolment;
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AuthResult(
            AuthOutcome.MfaRequired,
            Tokens: null,
            ChallengeToken: _tokens.IssueChallengeToken(user.TenantId, user.Id, ChallengePurposeMfa, now),
            EnrolmentSecret: null,
            EnrolmentUri: null);
    }

    public async Task<Result<AuthResult>> VerifyMfaAsync(
        VerifyMfaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var principal = _tokens.ValidateChallengeToken(command.ChallengeToken ?? string.Empty, ChallengePurposeMfa);
        if (principal.IsFailure)
        {
            return principal.Error!;
        }

        await _users
            .EstablishScopeAsync(principal.Value.TenantId, principal.Value.UserId, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var user = await _users.GetAsync(principal.Value.UserId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return InvalidCredentials();
        }

        var eligibility = user.CanAuthenticate(now);
        if (eligibility.IsFailure)
        {
            return eligibility.Error!;
        }

        var factors = await _users.GetActiveFactorsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var factor = factors.FirstOrDefault(f => f.Type == "totp");

        if (factor?.SecretEncrypted is null || factor.SecretKeyId is null)
        {
            return InvalidCredentials();
        }

        var secret = _protector.Unprotect(factor.SecretEncrypted, factor.SecretKeyId, user.TenantId, factor.Id);
        var step = _totp.Validate(secret, command.Code ?? string.Empty, now);

        if (step is null)
        {
            user.RecordFailedLogin(now);
            await RecordAttemptAsync(
                user.TenantId, user.Email, false, LoginFailureReason.MfaFailed,
                new LoginCommand(user.Email, null, command.IpAddress, command.UserAgent),
                cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Error.Forbidden("auth.mfa_invalid", "That code is not valid. Codes expire every 30 seconds.");
        }

        // A TOTP code is valid for its whole step, so accepting it twice would
        // let anyone who glimpsed it reuse it inside that window. One step, one
        // use.
        if (factor.LastAcceptedStep is { } lastStep && step.Value <= lastStep)
        {
            return Error.Forbidden(
                "auth.mfa_replay",
                "That code has already been used. Wait for your authenticator to show the next one.");
        }

        factor.RecordUse(now, step.Value);
        user.RecordSuccessfulLogin(now);

        return await CompleteAuthenticationAsync(
            user, ["pwd", "totp"], command.IpAddress, command.UserAgent, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Confirms a pending TOTP enrolment and completes the login.</summary>
    public async Task<Result<AuthResult>> ConfirmEnrolmentAsync(
        EnrolMfaCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var principal = _tokens.ValidateChallengeToken(command.ChallengeToken ?? string.Empty, ChallengePurposeEnrol);
        if (principal.IsFailure)
        {
            return principal.Error!;
        }

        await _users
            .EstablishScopeAsync(principal.Value.TenantId, principal.Value.UserId, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var user = await _users.GetAsync(principal.Value.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null)
        {
            return InvalidCredentials();
        }

        var factor = await _users.GetPendingTotpFactorAsync(user.Id, cancellationToken).ConfigureAwait(false);
        if (factor?.SecretEncrypted is null || factor.SecretKeyId is null)
        {
            return Error.Conflict("auth.mfa_no_pending", "There is no pending enrolment to confirm. Sign in again.");
        }

        var secret = _protector.Unprotect(factor.SecretEncrypted, factor.SecretKeyId, user.TenantId, factor.Id);
        var step = _totp.Validate(secret, command.Code ?? string.Empty, now);

        if (step is null)
        {
            return Error.Forbidden(
                "auth.mfa_invalid",
                "That code is not valid. Check your authenticator app and try the current code.");
        }

        factor.Confirm(now, step.Value);
        user.CompleteMfaEnrolment(now);
        user.Activate(now);
        user.RecordSuccessfulLogin(now);

        // Issued at enrolment rather than offered as a later opt-in. A recovery
        // code the user never generated is worth nothing on the day they drop
        // their phone, and that day is the whole reason this exists.
        var codes = await IssueRecoveryCodesAsync(user, now, cancellationToken).ConfigureAwait(false);

        return await CompleteAuthenticationAsync(
            user, ["pwd", "totp"], command.IpAddress, command.UserAgent, now, cancellationToken, codes)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Completes a login with a recovery code instead of a TOTP code.
    /// </summary>
    /// <remarks>
    /// This is a second factor, not a bypass: it still requires the challenge
    /// token, which is only issued after a correct password. A wrong code counts
    /// toward lockout exactly as a wrong TOTP code does, because otherwise this
    /// endpoint would be the cheapest way to brute-force an account.
    /// </remarks>
    public async Task<Result<AuthResult>> RedeemRecoveryCodeAsync(
        RedeemRecoveryCodeCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var principal = _tokens.ValidateChallengeToken(command.ChallengeToken ?? string.Empty, ChallengePurposeMfa);
        if (principal.IsFailure)
        {
            return principal.Error!;
        }

        await _users
            .EstablishScopeAsync(principal.Value.TenantId, principal.Value.UserId, cancellationToken)
            .ConfigureAwait(false);

        var now = _clock.UtcNow;
        var user = await _users.GetAsync(principal.Value.UserId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return InvalidCredentials();
        }

        var eligibility = user.CanAuthenticate(now);
        if (eligibility.IsFailure)
        {
            return eligibility.Error!;
        }

        var submitted = _recoveryCodes.Normalize(command.Code ?? string.Empty);
        var code = submitted.Length == 0
            ? null
            : await _users
                .FindUnusedRecoveryCodeAsync(user.Id, _recoveryCodes.Hash(submitted), cancellationToken)
                .ConfigureAwait(false);

        if (code is null)
        {
            user.RecordFailedLogin(now);
            await RecordAttemptAsync(
                user.TenantId, user.Email, false, LoginFailureReason.MfaFailed,
                new LoginCommand(user.Email, null, command.IpAddress, command.UserAgent),
                cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            // Says nothing about whether the code was unknown or already spent.
            // Both mean the same thing to a legitimate user, and telling them
            // apart would let an attacker confirm a guessed code after the fact.
            return Error.Forbidden(
                "auth.recovery_code_invalid",
                "That recovery code is not valid. Each code works once — try another from your list.");
        }

        var redeemed = code.Redeem(now);
        if (redeemed.IsFailure)
        {
            return redeemed.Error!;
        }

        user.RecordSuccessfulLogin(now);

        // "recovery" rather than "totp" so the session records honestly how the
        // second factor was satisfied. An auditor asking which logins bypassed
        // the authenticator gets an answer from the session row.
        return await CompleteAuthenticationAsync(
            user, ["pwd", "recovery"], command.IpAddress, command.UserAgent, now, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces a signed-in user's recovery codes with a fresh set.
    /// </summary>
    /// <remarks>
    /// Requires an established session, so the caller has already passed
    /// password and a second factor. Every previous code is discarded in the
    /// same transaction.
    /// </remarks>
    public async Task<Result<RecoveryCodeSetDto>> RegenerateRecoveryCodesAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var user = await _users.GetAsync(userId, cancellationToken).ConfigureAwait(false);

        if (user is null)
        {
            return Error.Forbidden("auth.required", "You are not signed in.");
        }

        if (user.MfaEnrolledAt is null)
        {
            return Error.Conflict(
                "auth.mfa_not_enrolled",
                "Set up your authenticator first. Recovery codes stand in for it, so there is " +
                "nothing for them to recover yet.");
        }

        var codes = await IssueRecoveryCodesAsync(user, now, cancellationToken).ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RecoveryCodeSetDto(codes, now);
    }

    public async Task<Result<RecoveryCodeStatusDto>> GetRecoveryCodeStatusAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var remaining = await _users
            .CountUnusedRecoveryCodesAsync(userId, cancellationToken)
            .ConfigureAwait(false);

        return new RecoveryCodeStatusDto(remaining, MfaRecoveryCode.SetSize);
    }

    /// <summary>Discards any existing codes and stores the hashes of a new set.</summary>
    private async Task<IReadOnlyList<string>> IssueRecoveryCodesAsync(
        User user,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _users.DiscardUnusedRecoveryCodesAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var plaintext = new List<string>(MfaRecoveryCode.SetSize);
        var rows = new List<MfaRecoveryCode>(MfaRecoveryCode.SetSize);

        for (var i = 0; i < MfaRecoveryCode.SetSize; i++)
        {
            var code = _recoveryCodes.Generate();
            plaintext.Add(code);
            rows.Add(MfaRecoveryCode.Issue(
                user.TenantId, user.Id, _recoveryCodes.Hash(_recoveryCodes.Normalize(code)), now));
        }

        _users.AddRecoveryCodes(rows);
        return plaintext;
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, rotating it.
    /// </summary>
    /// <remarks>
    /// The security-critical path. Rotation alone is not enough: an attacker who
    /// steals a token and uses it first would silently take over the session. So
    /// presenting an already-rotated token is treated as proof that two parties
    /// hold it, and the whole family is revoked — the legitimate user is logged
    /// out and must re-authenticate, which is the correct outcome when we cannot
    /// tell which party is which.
    /// </remarks>
    public async Task<Result<TokenPair>> RefreshAsync(
        RefreshCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            return Error.Forbidden("auth.refresh_invalid", "That session has expired. Please sign in again.");
        }

        var now = _clock.UtcNow;
        var hash = _tokens.HashRefreshToken(command.RefreshToken);

        // Establish the tenant from the token before anything else, or every
        // query below runs unscoped and returns nothing.
        await _sessions.EstablishScopeForTokenAsync(hash, cancellationToken).ConfigureAwait(false);

        var existing = await _sessions.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);

        if (existing is null)
        {
            return Error.Forbidden("auth.refresh_invalid", "That session has expired. Please sign in again.");
        }

        if (existing.IsRotated)
        {
            await _sessions
                .RevokeFamilyAsync(existing.FamilyId, now, RevocationReason.RotationReuse, cancellationToken)
                .ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            return Error.Forbidden(
                "auth.refresh_reuse",
                "This session has been signed out for security reasons. Please sign in again.");
        }

        if (!existing.IsUsable(now))
        {
            return Error.Forbidden("auth.refresh_invalid", "That session has expired. Please sign in again.");
        }

        var session = await _sessions.GetAsync(existing.SessionId, cancellationToken).ConfigureAwait(false);
        if (session is null || !session.IsActive(now))
        {
            return Error.Forbidden("auth.refresh_invalid", "That session has expired. Please sign in again.");
        }

        var user = await _users.GetAsync(session.UserId, cancellationToken).ConfigureAwait(false);
        if (user is null || user.CanAuthenticate(now).IsFailure)
        {
            return Error.Forbidden("auth.refresh_invalid", "That session is no longer valid.");
        }

        var (plaintext, newHash) = _tokens.CreateRefreshToken();
        var successor = RefreshToken.Issue(
            session.TenantId, session.Id, session.FamilyId, newHash, now,
            _tokens.RefreshTokenLifetime, command.IpAddress);

        _sessions.AddToken(successor);
        existing.Rotate(now, successor.Id);
        session.Touch(now);

        // Permissions are re-read on every refresh, so a role change takes
        // effect within one access-token lifetime rather than lingering until
        // the user signs out.
        var permissions = await _users.GetPermissionsAsync(user.Id, cancellationToken).ConfigureAwait(false);

        var accessToken = _tokens.IssueAccessToken(
            session.TenantId, user.Id, user.Email, permissions, session.Id, now);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new TokenPair(
            accessToken, plaintext, "Bearer", (int)_tokens.AccessTokenLifetime.TotalSeconds);
    }

    public async Task<Result> LogoutAsync(string? refreshToken, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return Result.Success();
        }

        var now = _clock.UtcNow;
        var hash = _tokens.HashRefreshToken(refreshToken);

        await _sessions.EstablishScopeForTokenAsync(hash, cancellationToken).ConfigureAwait(false);

        var token = await _sessions.FindByHashAsync(hash, cancellationToken).ConfigureAwait(false);

        if (token is null)
        {
            // Idempotent: signing out twice, or with a token we do not recognize,
            // is a success. Reporting an error would leak whether the token was
            // real and would make a client's retry look like a failure.
            return Result.Success();
        }

        await _sessions
            .RevokeFamilyAsync(token.FamilyId, now, RevocationReason.Logout, cancellationToken)
            .ConfigureAwait(false);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return Result.Success();
    }

    private async Task<Result<AuthResult>> BeginEnrolmentAsync(
        User user,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pending = await _users.GetPendingTotpFactorAsync(user.Id, cancellationToken).ConfigureAwait(false);
        string secret;

        if (pending?.SecretEncrypted is not null && pending.SecretKeyId is not null)
        {
            // Reuse the pending secret so a user who reloads the enrolment page
            // does not invalidate the QR code they already scanned.
            secret = _protector.Unprotect(pending.SecretEncrypted, pending.SecretKeyId, user.TenantId, pending.Id);
        }
        else
        {
            secret = _totp.GenerateSecret();

            // The factor is created first so its id exists, then the secret is
            // encrypted bound to that id. One factor, not two.
            var factor = MfaFactor.CreateTotp(user.TenantId, user.Id, "Authenticator app", now);
            var (ciphertext, keyId) = _protector.Protect(secret, user.TenantId, factor.Id);
            factor.SetSecret(ciphertext, keyId);
            _users.AddFactor(factor);
        }

        return new AuthResult(
            AuthOutcome.MfaEnrolmentRequired,
            Tokens: null,
            ChallengeToken: _tokens.IssueChallengeToken(user.TenantId, user.Id, ChallengePurposeEnrol, now),
            EnrolmentSecret: secret,
            EnrolmentUri: _totp.BuildEnrolmentUri(secret, user.Email, Issuer));
    }

    private async Task<Result<AuthResult>> CompleteAuthenticationAsync(
        User user,
        string[] amr,
        string? ipAddress,
        string? userAgent,
        DateTimeOffset now,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? recoveryCodes = null)
    {
        var session = Session.Start(
            user.TenantId, user.Id, now, _tokens.RefreshTokenLifetime, amr, ipAddress, userAgent);

        var (plaintext, hash) = _tokens.CreateRefreshToken();
        var refresh = RefreshToken.Issue(
            user.TenantId, session.Id, session.FamilyId, hash, now, _tokens.RefreshTokenLifetime, ipAddress);

        _sessions.Add(session);
        _sessions.AddToken(refresh);

        var permissions = await _users.GetPermissionsAsync(user.Id, cancellationToken).ConfigureAwait(false);
        var accessToken = _tokens.IssueAccessToken(
            user.TenantId, user.Id, user.Email, permissions, session.Id, now);

        await RecordAttemptAsync(
            user.TenantId, user.Email, true, null,
            new LoginCommand(user.Email, null, ipAddress, userAgent), cancellationToken)
            .ConfigureAwait(false);

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AuthResult(
            AuthOutcome.Authenticated,
            new TokenPair(accessToken, plaintext, "Bearer", (int)_tokens.AccessTokenLifetime.TotalSeconds),
            ChallengeToken: null,
            EnrolmentSecret: null,
            EnrolmentUri: null,
            recoveryCodes);
    }

    private Task RecordAttemptAsync(
        Guid? tenantId,
        string email,
        bool succeeded,
        LoginFailureReason? reason,
        LoginCommand command,
        CancellationToken cancellationToken) =>
        _users.RecordLoginAttemptAsync(
            tenantId, email, succeeded, reason, command.IpAddress, command.UserAgent, cancellationToken);
}

/// <summary>Commits the unit of work. Mirrors the Inventory port.</summary>
public interface IUnitOfWork
{
    public Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
