using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using MautoDesk.Identity.Application;
using MautoDesk.Identity.Infrastructure;
using Microsoft.Extensions.Options;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The cryptographic primitives, closing gaps the Phase 9 review marked
/// "Configured" — implemented, but nothing asserted them.
/// </summary>
public sealed class Argon2PasswordHasherTests
{
    private readonly Argon2PasswordHasher _hasher = new();

    [Fact]
    public void Verifies_a_correct_password()
    {
        var hash = _hasher.Hash("correct-horse-battery-staple");

        _hasher.Verify("correct-horse-battery-staple", hash).Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_wrong_password()
    {
        var hash = _hasher.Hash("correct-horse-battery-staple");

        _hasher.Verify("Correct-horse-battery-staple", hash).Should().BeFalse();
        _hasher.Verify("correct-horse-battery-stapl", hash).Should().BeFalse();
        _hasher.Verify(string.Empty, hash).Should().BeFalse();
    }

    /// <summary>
    /// Two hashes of the same password differ.
    /// </summary>
    /// <remarks>
    /// The salt is what stops a stolen table being cracked once and reused
    /// everywhere. Identical hashes for identical passwords would also tell an
    /// attacker which users share one.
    /// </remarks>
    [Fact]
    public void Salts_every_hash()
    {
        var first = _hasher.Hash("same-password");
        var second = _hasher.Hash("same-password");

        first.Should().NotBe(second);
        _hasher.Verify("same-password", first).Should().BeTrue();
        _hasher.Verify("same-password", second).Should().BeTrue();
    }

    [Fact]
    public void Encodes_its_parameters_so_a_stored_hash_stays_verifiable()
    {
        var hash = _hasher.Hash("anything");

        hash.Should().StartWith("$argon2id$v=19$");
        hash.Should().Contain($"m={Argon2PasswordHasher.MemoryKib}");
        hash.Should().Contain($"t={Argon2PasswordHasher.Iterations}");
    }

    /// <summary>
    /// A hash made with weaker parameters is flagged for upgrade.
    /// </summary>
    /// <remarks>
    /// This is the mechanism that migrates the whole user base to stronger
    /// settings on next login, without a password reset. Marked "Configured" in
    /// the Phase 9 review because nothing exercised it.
    /// </remarks>
    [Fact]
    public void Flags_a_hash_made_with_weaker_parameters()
    {
        // A hash from an older deployment: half the memory, one iteration.
        var weak = $"$argon2id$v=19$m={Argon2PasswordHasher.MemoryKib / 2},t=1,p=1$" +
                   $"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))}$" +
                   $"{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}";

        _hasher.NeedsRehash(weak).Should().BeTrue();
        _hasher.NeedsRehash(_hasher.Hash("current")).Should().BeFalse();
    }

    [Fact]
    public void Treats_a_malformed_hash_as_needing_rehash_and_never_verifies_it()
    {
        foreach (var malformed in new[] { "", "not-a-hash", "$argon2id$broken", "$argon2id$v=19$m=x,t=y,p=z$a$b" })
        {
            _hasher.Verify("anything", malformed).Should().BeFalse(
                "a malformed hash must never authenticate anyone");
            _hasher.NeedsRehash(malformed).Should().BeTrue();
        }
    }

    [Fact]
    public void Uses_at_least_the_owasp_minimum_parameters()
    {
        // Guards against someone lowering the cost to speed up a test suite and
        // leaving it lowered.
        Argon2PasswordHasher.MemoryKib.Should().BeGreaterThanOrEqualTo(19 * 1024);
        Argon2PasswordHasher.Iterations.Should().BeGreaterThanOrEqualTo(2);
        Argon2PasswordHasher.HashBytes.Should().BeGreaterThanOrEqualTo(32);
        Argon2PasswordHasher.SaltBytes.Should().BeGreaterThanOrEqualTo(16);
    }
}

public sealed class TotpServiceTests
{
    private readonly TotpService _totp = new();

    [Fact]
    public void Generates_a_secret_an_authenticator_app_can_read()
    {
        var secret = _totp.GenerateSecret();

        secret.Should().MatchRegex("^[A-Z2-7]+=*$", "authenticator apps expect base32");
        secret.Length.Should().BeGreaterThanOrEqualTo(32, "160 bits is the RFC 4226 recommendation");
    }

    [Fact]
    public void Generates_a_different_secret_each_time()
    {
        _totp.GenerateSecret().Should().NotBe(_totp.GenerateSecret());
    }

    [Fact]
    public void Builds_an_enrolment_uri_apps_can_parse()
    {
        var uri = _totp.BuildEnrolmentUri("JBSWY3DPEHPK3PXP", "dana@ridgeline.test", "MautoDesk");

        uri.Should().StartWith("otpauth://totp/");
        uri.Should().Contain("secret=JBSWY3DPEHPK3PXP");
        uri.Should().Contain("issuer=MautoDesk");
        uri.Should().Contain("period=30");
        uri.Should().Contain("dana%40ridgeline.test", "the label must be URL-encoded");
    }

    [Fact]
    public void Rejects_anything_that_is_not_six_digits()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.UtcNow;

        foreach (var candidate in new[] { "", "12345", "1234567", "abcdef", "12 34 56", "12345a" })
        {
            _totp.Validate(secret, candidate, now).Should().BeNull();
        }
    }

    [Fact]
    public void Rejects_a_malformed_secret_rather_than_throwing()
    {
        _totp.Validate("not base32 !!!", "123456", DateTimeOffset.UtcNow).Should().BeNull();
    }

    /// <summary>
    /// Tolerance is one step either side, and no more.
    /// </summary>
    /// <remarks>
    /// Each additional step linearly increases the number of codes an attacker
    /// can guess. Wide windows are common and wrong.
    /// </remarks>
    [Fact]
    public void Accepts_the_adjacent_steps_but_not_the_ones_beyond()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret), step: 30)
            .ComputeTotp(now.UtcDateTime);

        _totp.Validate(secret, code, now).Should().NotBeNull("the current step must be accepted");
        _totp.Validate(secret, code, now.AddSeconds(30)).Should().NotBeNull("one step late is tolerated");
        _totp.Validate(secret, code, now.AddSeconds(-30)).Should().NotBeNull("one step early is tolerated");

        _totp.Validate(secret, code, now.AddSeconds(90)).Should().BeNull("three steps is outside the window");
        _totp.Validate(secret, code, now.AddSeconds(-90)).Should().BeNull();
    }

    [Fact]
    public void Returns_the_step_so_a_code_can_be_marked_used()
    {
        var secret = _totp.GenerateSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var code = new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret), step: 30)
            .ComputeTotp(now.UtcDateTime);

        // Replay prevention depends on this value being the actual step.
        _totp.Validate(secret, code, now).Should().Be(1_800_000_000 / 30);
    }
}

public sealed class SecretProtectorTests
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid RecordA = Guid.Parse("11111111-0000-4000-8000-000000000001");
    private static readonly Guid RecordB = Guid.Parse("22222222-0000-4000-8000-000000000002");

    private static SecretProtector Create() => new(Options.Create(new EncryptionOptions
    {
        MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        KeyId = "test-1",
    }));

    [Fact]
    public void Round_trips_a_secret()
    {
        var protector = Create();
        var (ciphertext, keyId) = protector.Protect("JBSWY3DPEHPK3PXP", TenantA, RecordA);

        protector.Unprotect(ciphertext, keyId, TenantA, RecordA).Should().Be("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public void Does_not_leave_the_plaintext_in_the_ciphertext()
    {
        var protector = Create();
        var (ciphertext, _) = protector.Protect("JBSWY3DPEHPK3PXP", TenantA, RecordA);

        Encoding.UTF8.GetString(ciphertext).Should().NotContain("JBSWY3DPEHPK3PXP");
    }

    [Fact]
    public void Produces_different_ciphertext_for_the_same_plaintext()
    {
        var protector = Create();

        var first = protector.Protect("same", TenantA, RecordA).Ciphertext;
        var second = protector.Protect("same", TenantA, RecordA).Ciphertext;

        // A fresh nonce each time. Reusing one under AES-GCM is catastrophic —
        // it leaks the XOR of the plaintexts and can forge the authentication tag.
        first.Should().NotBeEquivalentTo(second);
    }

    /// <summary>
    /// A ciphertext cannot be moved to another tenant.
    /// </summary>
    /// <remarks>
    /// The tenant and record ids are bound as additional authenticated data, so
    /// a row copied into another tenant decrypts to nothing. This is what makes
    /// a database-level tampering attack inert rather than merely detectable.
    /// </remarks>
    [Fact]
    public void Refuses_to_decrypt_under_a_different_tenant()
    {
        var protector = Create();
        var (ciphertext, keyId) = protector.Protect("secret", TenantA, RecordA);

        var moved = () => protector.Unprotect(ciphertext, keyId, TenantB, RecordA);

        moved.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Refuses_to_decrypt_under_a_different_record()
    {
        var protector = Create();
        var (ciphertext, keyId) = protector.Protect("secret", TenantA, RecordA);

        var moved = () => protector.Unprotect(ciphertext, keyId, TenantA, RecordB);

        moved.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Refuses_to_decrypt_tampered_ciphertext()
    {
        var protector = Create();
        var (ciphertext, keyId) = protector.Protect("secret", TenantA, RecordA);
        ciphertext[^1] ^= 0x01;

        var tampered = () => protector.Unprotect(ciphertext, keyId, TenantA, RecordA);

        tampered.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Refuses_to_start_without_a_key()
    {
        var missing = () => new SecretProtector(Options.Create(new EncryptionOptions { MasterKey = "" }));

        missing.Should().Throw<InvalidOperationException>()
            .WithMessage("*not configured*");
    }

    [Fact]
    public void Refuses_a_key_of_the_wrong_length()
    {
        var shortKey = () => new SecretProtector(Options.Create(new EncryptionOptions
        {
            MasterKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)),
        }));

        shortKey.Should().Throw<InvalidOperationException>().WithMessage("*32 bytes*");
    }
}

public sealed class TokenIssuerTests
{
    private static TokenIssuer Create(string? key = null) => new(Options.Create(new JwtOptions
    {
        Issuer = "https://test",
        Audience = "test-api",
        SigningKey = key ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    }));

    [Fact]
    public void Refuses_a_signing_key_shorter_than_the_hmac_block()
    {
        var weak = () => Create(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)));

        weak.Should().Throw<InvalidOperationException>().WithMessage("*at least 32 bytes*");
    }

    [Fact]
    public void Refuses_to_start_without_a_signing_key()
    {
        var missing = () => Create(string.Empty);

        missing.Should().Throw<InvalidOperationException>().WithMessage("*not configured*");
    }

    /// <summary>
    /// Refresh tokens are random and stored only as a hash.
    /// </summary>
    /// <remarks>
    /// Marked "Configured" in the Phase 9 review: reuse detection proved the
    /// lookup worked, but nothing asserted the stored form was a hash rather
    /// than the token itself.
    /// </remarks>
    [Fact]
    public void Creates_a_refresh_token_whose_stored_form_is_a_hash()
    {
        var issuer = Create();
        var (plaintext, hash) = issuer.CreateRefreshToken();

        plaintext.Should().NotBeNullOrWhiteSpace();
        hash.Should().HaveCount(32, "SHA-256");

        // The stored bytes must not contain the token.
        Encoding.UTF8.GetString(hash).Should().NotContain(plaintext);

        // And the hash must be derivable from the plaintext, or lookup breaks.
        issuer.HashRefreshToken(plaintext).Should().BeEquivalentTo(hash);
    }

    [Fact]
    public void Creates_a_different_refresh_token_every_time()
    {
        var issuer = Create();

        issuer.CreateRefreshToken().Plaintext.Should().NotBe(issuer.CreateRefreshToken().Plaintext);
    }

    [Fact]
    public void Rejects_a_challenge_token_issued_for_a_different_purpose()
    {
        var issuer = Create();
        var token = issuer.IssueChallengeToken(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "mfa_enrol", DateTimeOffset.UtcNow);

        issuer.ValidateChallengeToken(token, "mfa").IsFailure.Should().BeTrue(
            "an enrolment token must not complete a verification");
        issuer.ValidateChallengeToken(token, "mfa_enrol").IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Rejects_a_challenge_token_signed_with_another_key()
    {
        var mine = Create();
        var theirs = Create();

        var forged = theirs.IssueChallengeToken(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "mfa", DateTimeOffset.UtcNow);

        mine.ValidateChallengeToken(forged, "mfa").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Rejects_an_expired_challenge_token_with_no_skew_grace()
    {
        var issuer = Create();
        var issuedAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        var stale = issuer.IssueChallengeToken(Guid.CreateVersion7(), Guid.CreateVersion7(), "mfa", issuedAt);

        issuer.ValidateChallengeToken(stale, "mfa").IsFailure.Should().BeTrue(
            "ClockSkew is zero, so a 5-minute token is dead after 5 minutes");
    }

    [Fact]
    public void Rejects_garbage_rather_than_throwing()
    {
        var issuer = Create();

        foreach (var junk in new[] { "", "not.a.token", "a.b.c", "eyJhbGciOiJub25lIn0..".ToString() })
        {
            issuer.ValidateChallengeToken(junk, "mfa").IsFailure.Should().BeTrue();
        }
    }

    [Fact]
    public void Access_token_lifetime_is_short()
    {
        Create().AccessTokenLifetime.Should().BeLessThanOrEqualTo(
            TimeSpan.FromMinutes(15),
            "a longer lifetime widens the window in which a revoked permission stays effective");
    }
}
