using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using MautoDesk.Identity.Application;
using MautoDesk.SharedKernel;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace MautoDesk.Identity.Infrastructure;

/// <summary>Token configuration. Bound from <c>Jwt:*</c>.</summary>
public sealed class JwtOptions
{
    public string Issuer { get; set; } = "https://localhost:5080";

    public string Audience { get; set; } = "mautodesk-api";

    /// <summary>Base64 signing key, at least 32 bytes.</summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>How long a half-finished login may sit before it must restart.</summary>
    public int ChallengeTokenMinutes { get; set; } = 5;
}

/// <summary>Well-known claim names used across the platform.</summary>
public static class MautoDeskClaims
{
    /// <summary>
    /// The tenant. <b>The only source of tenancy in the system.</b>
    /// </summary>
    /// <remarks>
    /// ADR-0002: never a header, subdomain, query parameter, or body. This claim
    /// is signed, so a caller cannot choose their own tenant.
    /// </remarks>
    public const string Tenant = "tenant";

    public const string Permission = "perm";

    public const string SessionId = "sid";

    public const string Purpose = "purpose";
}

/// <summary>Issues and validates access, challenge, and refresh tokens.</summary>
/// <remarks>
/// <para>
/// <b>HS256, deliberately.</b> The API is the only verifier, so a symmetric key
/// is sufficient and avoids the key-distribution work RS256 implies. If a second
/// service ever needs to verify a token independently, this becomes RS256 — and
/// that is a configuration change here, not a change anywhere else.
/// </para>
/// <para>
/// <b>Permissions are embedded in the access token.</b> That makes authorization
/// a signature check rather than a database read on every request. The cost is
/// staleness: a revoked permission remains effective until the access token
/// expires. With a 15-minute lifetime that window is bounded and acceptable, and
/// permissions are re-read on every refresh. An immediate revocation still works
/// — it revokes the session, which stops the next refresh.
/// </para>
/// </remarks>
public sealed class TokenIssuer : ITokenIssuer
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _credentials;
    private readonly TokenValidationParameters _challengeValidation;

    public TokenIssuer(IOptions<JwtOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;

        var keyBytes = DecodeKey(_options.SigningKey);

        if (keyBytes.Length < 32)
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey must be at least 32 bytes. A shorter key weakens HMAC-SHA256 " +
                "below its designed strength. Generate one with: openssl rand -base64 32");
        }

        var key = new SymmetricSecurityKey(keyBytes);
        _credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        _challengeValidation = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = _options.Issuer,
            ValidateAudience = true,
            ValidAudience = _options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ValidateLifetime = true,
            // No leniency on expiry. The default five-minute skew would triple
            // the life of a five-minute challenge token.
            ClockSkew = TimeSpan.Zero,
        };
    }

    public TimeSpan AccessTokenLifetime => TimeSpan.FromMinutes(_options.AccessTokenMinutes);

    public TimeSpan RefreshTokenLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public string IssueAccessToken(
        Guid tenantId,
        Guid userId,
        string email,
        IReadOnlyCollection<string> permissions,
        Guid sessionId,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            new(MautoDeskClaims.Tenant, tenantId.ToString()),
            new(MautoDeskClaims.SessionId, sessionId.ToString()),
        };

        claims.AddRange(permissions.Select(permission => new Claim(MautoDeskClaims.Permission, permission)));

        return Write(claims, now, AccessTokenLifetime);
    }

    public string IssueChallengeToken(Guid tenantId, Guid userId, string purpose, DateTimeOffset now) =>
        Write(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(MautoDeskClaims.Tenant, tenantId.ToString()),
                new Claim(MautoDeskClaims.Purpose, purpose),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7().ToString()),
            ],
            now,
            TimeSpan.FromMinutes(_options.ChallengeTokenMinutes));

    public Result<ChallengePrincipal> ValidateChallengeToken(string token, string expectedPurpose)
    {
        var handler = new JwtSecurityTokenHandler();

        // Without this the handler helpfully rewrites `sub` to the long-form
        // ClaimTypes.NameIdentifier URI, so looking it up by its JWT name returns
        // null and every challenge is rejected as malformed. The bearer handler
        // is configured the same way, so both read the raw claim names the tokens
        // actually carry.
        handler.InboundClaimTypeMap.Clear();

        try
        {
            var principal = handler.ValidateToken(token, _challengeValidation, out _);

            // A challenge token proves only that the password step passed. Without
            // this purpose check, an enrolment token would be accepted where a
            // verification token is required — letting a user with no confirmed
            // factor complete a login and defeating mandatory MFA entirely.
            var purpose = principal.FindFirstValue(MautoDeskClaims.Purpose);
            if (!string.Equals(purpose, expectedPurpose, StringComparison.Ordinal))
            {
                return Error.Forbidden("auth.challenge_invalid", "That sign-in attempt is no longer valid.");
            }

            var tenant = principal.FindFirstValue(MautoDeskClaims.Tenant);
            var subject = principal.FindFirstValue(JwtRegisteredClaimNames.Sub);

            return Guid.TryParse(tenant, out var tenantId) && Guid.TryParse(subject, out var userId)
                ? new ChallengePrincipal(tenantId, userId)
                : Error.Forbidden("auth.challenge_invalid", "That sign-in attempt is no longer valid.");
        }
        catch (SecurityTokenException)
        {
            return Error.Forbidden(
                "auth.challenge_invalid",
                "That sign-in attempt has expired. Please start again.");
        }
        catch (ArgumentException)
        {
            return Error.Forbidden("auth.challenge_invalid", "That sign-in attempt is not valid.");
        }
    }

    /// <summary>
    /// Creates an opaque refresh token and its storage hash.
    /// </summary>
    /// <remarks>
    /// Opaque and random, not a JWT: a refresh token carries no claims anyone
    /// needs to read, and 256 bits of entropy from a CSPRNG is stronger and
    /// smaller than a signed document. Only the SHA-256 is stored, so the
    /// database never holds a usable token.
    /// </remarks>
    public (string Plaintext, byte[] Hash) CreateRefreshToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var plaintext = Base64UrlEncoder.Encode(bytes);
        return (plaintext, HashRefreshToken(plaintext));
    }

    public byte[] HashRefreshToken(string plaintext) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(plaintext));

    private string Write(IEnumerable<Claim> claims, DateTimeOffset now, TimeSpan lifetime)
    {
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: _credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static byte[] DecodeKey(string configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            throw new InvalidOperationException(
                "Jwt:SigningKey is not configured. Refusing to start rather than falling back to " +
                "a default key — a predictable signing key lets anyone mint a token for any tenant.");
        }

        try
        {
            return Convert.FromBase64String(configured);
        }
        catch (FormatException)
        {
            return Encoding.UTF8.GetBytes(configured);
        }
    }
}

/// <summary>RFC 6238 TOTP.</summary>
public sealed class TotpService : ITotpService
{
    /// <summary>
    /// Steps of tolerance either side of now.
    /// </summary>
    /// <remarks>
    /// One step (±30 s) accommodates ordinary phone clock drift and the seconds
    /// a user spends typing. Wider windows are common and wrong: each extra step
    /// linearly increases the number of codes an attacker can guess, and three
    /// or four steps start to matter.
    /// </remarks>
    private const int WindowSteps = 1;

    private const int StepSeconds = 30;

    public string GenerateSecret() =>
        OtpNet.Base32Encoding.ToString(RandomNumberGenerator.GetBytes(20));

    public string BuildEnrolmentUri(string secret, string email, string issuer)
    {
        var label = Uri.EscapeDataString($"{issuer}:{email}");
        var issuerParameter = Uri.EscapeDataString(issuer);
        return $"otpauth://totp/{label}?secret={secret}&issuer={issuerParameter}&algorithm=SHA1&digits=6&period={StepSeconds}";
    }

    public long? Validate(string secret, string code, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length != 6 || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        byte[] key;
        try
        {
            key = OtpNet.Base32Encoding.ToBytes(secret);
        }
        catch (ArgumentException)
        {
            return null;
        }

        var totp = new OtpNet.Totp(key, step: StepSeconds);
        var currentStep = now.ToUnixTimeSeconds() / StepSeconds;

        for (var offset = -WindowSteps; offset <= WindowSteps; offset++)
        {
            var step = currentStep + offset;
            var expected = totp.ComputeTotp(DateTimeOffset.FromUnixTimeSeconds(step * StepSeconds).UtcDateTime);

            // Constant-time even here: a timing oracle on code comparison would
            // let an attacker discover a valid code digit by digit.
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expected),
                Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }
}

/// <summary>
/// Envelope encryption for secrets held in the database.
/// </summary>
/// <remarks>
/// <para>
/// AES-256-GCM with the tenant id and record id bound as additional
/// authenticated data. That binding means a ciphertext lifted from one row
/// cannot be decrypted in another, or under another tenant — a copied TOTP
/// secret is inert.
/// </para>
/// <para>
/// The master key comes from configuration. DigitalOcean has no managed KMS,
/// which is <c>RISK-SEC-001</c> in the architecture debt register; the
/// <see cref="ISecretProtector"/> seam is what makes closing it a provider swap.
/// </para>
/// </remarks>
public sealed class SecretProtector : ISecretProtector
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    private readonly byte[] _masterKey;
    private readonly string _keyId;

    public SecretProtector(IOptions<EncryptionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Value.MasterKey))
        {
            throw new InvalidOperationException(
                "Encryption:MasterKey is not configured. Refusing to start rather than storing " +
                "MFA secrets and customer identifiers unprotected.");
        }

        _masterKey = Convert.FromBase64String(options.Value.MasterKey);
        _keyId = options.Value.KeyId;

        if (_masterKey.Length != 32)
        {
            throw new InvalidOperationException(
                $"Encryption:MasterKey must decode to exactly 32 bytes for AES-256; got {_masterKey.Length}.");
        }
    }

    public (byte[] Ciphertext, string KeyId) Protect(string plaintext, Guid tenantId, Guid recordId)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagBytes];

        using var aes = new AesGcm(_masterKey, TagBytes);
        aes.Encrypt(nonce, plainBytes, cipher, tag, AssociatedData(tenantId, recordId));

        var envelope = new byte[NonceBytes + TagBytes + cipher.Length];
        nonce.CopyTo(envelope, 0);
        tag.CopyTo(envelope, NonceBytes);
        cipher.CopyTo(envelope, NonceBytes + TagBytes);

        return (envelope, _keyId);
    }

    public string Unprotect(byte[] ciphertext, string keyId, Guid tenantId, Guid recordId)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);

        if (ciphertext.Length < NonceBytes + TagBytes)
        {
            throw new CryptographicException("The stored ciphertext is malformed.");
        }

        var nonce = ciphertext.AsSpan(0, NonceBytes);
        var tag = ciphertext.AsSpan(NonceBytes, TagBytes);
        var cipher = ciphertext.AsSpan(NonceBytes + TagBytes);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(_masterKey, TagBytes);

        // Throws if the tag does not verify — including when the tenant or
        // record id differs from the one the value was sealed under.
        aes.Decrypt(nonce, cipher, tag, plain, AssociatedData(tenantId, recordId));

        return Encoding.UTF8.GetString(plain);
    }

    private static byte[] AssociatedData(Guid tenantId, Guid recordId) =>
        Encoding.UTF8.GetBytes(string.Create(
            CultureInfo.InvariantCulture,
            $"{tenantId:N}:{recordId:N}"));
}

public sealed class EncryptionOptions
{
    public string MasterKey { get; set; } = string.Empty;

    public string KeyId { get; set; } = "dev-1";
}
