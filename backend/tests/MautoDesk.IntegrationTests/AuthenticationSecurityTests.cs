using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The attack paths, exercised against the running host.
/// </summary>
/// <remarks>
/// Authentication is the one area where "it works" and "it is correct" diverge
/// most sharply: a login flow with a stolen-token hole, a user-enumeration
/// oracle, or replayable MFA codes behaves perfectly for every honest user. So
/// these tests are written from the attacker's side.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class AuthenticationSecurityTests
{
    private readonly ApiFixture _fixture;

    public AuthenticationSecurityTests(ApiFixture fixture) => _fixture = fixture;

    /* ------------------------------------------------------- refresh tokens -- */

    /// <summary>
    /// The single most important test in this file.
    /// </summary>
    /// <remarks>
    /// Rotation alone does not stop token theft: whoever redeems the stolen
    /// token first silently takes over the session, and the victim's next
    /// refresh looks like an ordinary expiry. Detecting the reuse and revoking
    /// the whole family is what makes theft loud instead of silent.
    /// </remarks>
    [Fact]
    public async Task Replaying_a_rotated_refresh_token_revokes_the_entire_family()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        // The legitimate client refreshes once. Its original token is now rotated.
        var first = await RefreshAsync(client, user.RefreshToken);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var rotated = await ReadTokensAsync(first);

        // The attacker replays the token they stole before the rotation.
        var replay = await RefreshAsync(client, user.RefreshToken);
        replay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await replay.Content.ReadAsStringAsync()).Should().Contain("auth.refresh_reuse");

        // And the legitimate client's newer token is dead too. That is the point:
        // we cannot tell victim from thief, so both are logged out and a fresh
        // authentication is required.
        var afterRevocation = await RefreshAsync(client, rotated.RefreshToken);
        afterRevocation.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "the whole family is revoked, not just the replayed token");
    }

    [Fact]
    public async Task A_refresh_token_cannot_be_used_twice_even_without_theft()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        (await RefreshAsync(client, user.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await RefreshAsync(client, user.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Refreshing_issues_a_working_access_token()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var refreshed = await ReadTokensAsync(await RefreshAsync(client, user.RefreshToken));

        var authed = _fixture.AnonymousClient().WithToken(refreshed.AccessToken);
        var response = await authed.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logging_out_kills_the_session()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var logout = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/logout", UriKind.Relative),
            new { refreshToken = user.RefreshToken });

        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await RefreshAsync(client, user.RefreshToken)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /* ----------------------------------------------------------- enumeration -- */

    /// <summary>
    /// An unknown address and a wrong password must be indistinguishable.
    /// </summary>
    /// <remarks>
    /// Otherwise the login endpoint becomes a directory of who works at a
    /// dealership — a ready-made phishing target list.
    /// </remarks>
    [Fact]
    public async Task An_unknown_address_and_a_wrong_password_look_identical()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var unknown = await LoginAsync(client, $"nobody-{Guid.NewGuid():N}@nowhere.test", "whatever-9");
        var wrongPassword = await LoginAsync(client, user.Email, "definitely-not-the-password-9");

        unknown.StatusCode.Should().Be(wrongPassword.StatusCode);

        var unknownBody = Normalize(await unknown.Content.ReadAsStringAsync());
        var wrongBody = Normalize(await wrongPassword.Content.ReadAsStringAsync());

        unknownBody.Should().Be(
            wrongBody,
            "the response body must not reveal whether the account exists");
    }

    /// <summary>
    /// Timing must not leak account existence either.
    /// </summary>
    /// <remarks>
    /// Without a decoy hash the unknown-user path skips Argon2 entirely and
    /// returns in microseconds, while a real account costs ~50 ms. That gap is
    /// trivially measurable over the internet. This asserts the unknown path is
    /// not dramatically faster; it is a smoke test for the decoy, not a rigorous
    /// statistical study.
    /// </remarks>
    [Fact]
    public async Task An_unknown_address_is_not_conspicuously_faster_than_a_real_one()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        // Warm the path so JIT and connection setup do not dominate.
        await LoginAsync(client, user.Email, "wrong-9");
        await LoginAsync(client, $"warm-{Guid.NewGuid():N}@nowhere.test", "wrong-9");

        var knownMs = await MedianMillisecondsAsync(() => LoginAsync(client, user.Email, "wrong-9"));
        var unknownMs = await MedianMillisecondsAsync(
            () => LoginAsync(client, $"x-{Guid.NewGuid():N}@nowhere.test", "wrong-9"));

        // Generous bound: this catches "skipped Argon2 entirely", which is the
        // real defect, without being flaky on a loaded CI machine.
        unknownMs.Should().BeGreaterThan(
            knownMs * 0.25,
            "an unknown address must still pay the password-hashing cost");
    }

    /* ------------------------------------------------------------------ MFA -- */

    /// <summary>A correct password alone must never produce tokens.</summary>
    [Fact]
    public async Task A_correct_password_alone_never_yields_tokens()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var response = await LoginAsync(client, user.Email, user.Password);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("outcome").GetString().Should().Be(
            "mfa_required",
            "MFA is mandatory under the FTC Safeguards Rule; there is no path that skips it");

        root.TryGetProperty("tokens", out var tokens).Should().BeTrue();
        (tokens.ValueKind == JsonValueKind.Null).Should().BeTrue("no tokens until the second factor");
    }

    [Fact]
    public async Task A_wrong_totp_code_is_rejected()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var login = await LoginAsync(client, user.Email, user.Password);
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var challenge = document.RootElement.GetProperty("challengeToken").GetString();

        var verify = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = challenge, code = "000000" });

        verify.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    /// A TOTP code is single-use within its step.
    /// </summary>
    /// <remarks>
    /// A code stays valid for a whole 30-second window. Without replay
    /// prevention, anyone who observes it — over a shoulder, or through a
    /// phishing proxy — can reuse it inside that window.
    /// </remarks>
    [Fact]
    public async Task A_totp_code_cannot_be_replayed_within_its_step()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        await AuthFlow.WaitForNextStepAsync(user.TotpSecret);
        var code = AuthFlow.CurrentCode(user.TotpSecret);

        var firstLogin = await LoginAsync(client, user.Email, user.Password);
        var firstChallenge = await ChallengeTokenAsync(firstLogin);

        var first = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = firstChallenge, code });

        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Same code, fresh challenge, same 30-second step.
        var secondLogin = await LoginAsync(client, user.Email, user.Password);
        var secondChallenge = await ChallengeTokenAsync(secondLogin);

        var second = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = secondChallenge, code });

        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await second.Content.ReadAsStringAsync()).Should().Contain("auth.mfa_replay");
    }

    /// <summary>
    /// An enrolment challenge must not be accepted for verification.
    /// </summary>
    /// <remarks>
    /// Without the purpose check, a user with no confirmed factor could present
    /// their enrolment token to /mfa/verify and complete a login — defeating
    /// mandatory MFA entirely.
    /// </remarks>
    [Fact]
    public async Task A_challenge_token_is_not_valid_for_a_different_purpose()
    {
        var client = _fixture.AnonymousClient();

        // Seed a user but stop at enrolment, so the challenge has purpose
        // "mfa_enrol" rather than "mfa".
        var email = $"pending-{Guid.NewGuid():N}@test.local";
        await SeedUnenrolledUserAsync(email);

        var login = await LoginAsync(client, email, "correct-horse-battery-staple-9");
        using var document = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("outcome").GetString().Should().Be("mfa_enrolment_required");
        var enrolmentChallenge = root.GetProperty("challengeToken").GetString();
        var secret = root.GetProperty("enrolmentSecret").GetString()!;

        var misuse = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/verify", UriKind.Relative),
            new { challengeToken = enrolmentChallenge, code = AuthFlow.CurrentCode(secret) });

        misuse.StatusCode.Should().Be(
            HttpStatusCode.Forbidden,
            "an enrolment token must not complete a verification");
    }

    /* -------------------------------------------------------------- lockout -- */

    [Fact]
    public async Task Repeated_failures_lock_the_account()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await LoginAsync(client, user.Email, "wrong-password-9");
        }

        var afterLockout = await LoginAsync(client, user.Email, user.Password);

        afterLockout.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await afterLockout.Content.ReadAsStringAsync()).Should().Contain(
            "auth.locked",
            "a locked-out user must be told why they cannot get in — by this point the caller " +
            "already supplied a correct-looking address, so enumeration is moot and usability wins");
    }

    /* --------------------------------------------------------------- tokens -- */

    [Fact]
    public async Task A_tampered_access_token_is_rejected()
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        // Flip a character in the signature segment.
        var segments = user.AccessToken.Split('.');
        segments[2] = segments[2][0] == 'A' ? 'B' + segments[2][1..] : 'A' + segments[2][1..];

        var client = _fixture.AnonymousClient().WithToken(string.Join('.', segments));
        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// A token signed with a different key must not be honoured.
    /// </summary>
    /// <remarks>
    /// The forged token carries a valid-looking tenant claim. If signature
    /// validation were misconfigured, this is how an attacker would grant
    /// themselves access to any dealership's data.
    /// </remarks>
    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_rejected()
    {
        var forged = ForgeToken(ApiFixture.TenantA, Guid.CreateVersion7(), "wrong-key-32-bytes-exactly-here!");

        var client = _fixture.AnonymousClient().WithToken(forged);
        var response = await client.GetAsync(new Uri("/api/v1/vehicles", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Me_reports_the_tenant_and_permissions_from_the_token()
    {
        var client = _fixture.ClientFor(_fixture.UserA);

        var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        root.GetProperty("tenant").GetProperty("id").GetString()
            .Should().Be(ApiFixture.TenantA.ToString());
        root.GetProperty("mfaEnrolled").GetBoolean().Should().BeTrue();

        var permissions = root.GetProperty("permissions").EnumerateArray()
            .Select(p => p.GetString()).ToList();
        permissions.Should().Contain("inventory.vehicle.read");
    }

    /// <summary>
    /// Tenant isolation still holds with real tokens.
    /// </summary>
    /// <remarks>
    /// The isolation suite proved this under the dev-header shim. Now the tenant
    /// arrives in a signed claim, and the property must survive the change —
    /// this is the regression guard on that migration.
    /// </remarks>
    [Fact]
    public async Task A_real_token_still_cannot_reach_another_tenant()
    {
        var client = _fixture.ClientFor(_fixture.UserB);

        var response = await client.GetAsync(
            new Uri($"/api/v1/vehicles/{_fixture.TenantAVehicleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    /* ---------------------------------------------------------------- helpers */

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync(new Uri("/api/v1/auth/login", UriKind.Relative), new { email, password });

    private static Task<HttpResponseMessage> RefreshAsync(HttpClient client, string refreshToken) =>
        client.PostAsJsonAsync(new Uri("/api/v1/auth/refresh", UriKind.Relative), new { refreshToken });

    private static async Task<(string AccessToken, string RefreshToken)> ReadTokensAsync(
        HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        return (root.GetProperty("accessToken").GetString()!, root.GetProperty("refreshToken").GetString()!);
    }

    private static async Task<string> ChallengeTokenAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("challengeToken").GetString()!;
    }

    /// <summary>Strips the per-request trace id so two problem bodies compare equal.</summary>
    private static string Normalize(string problemJson)
    {
        using var document = JsonDocument.Parse(problemJson);
        var filtered = document.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("traceId" or "instance"))
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.Ordinal);

        return string.Join('|', filtered.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"{p.Key}={p.Value}"));
    }

    private static async Task<double> MedianMillisecondsAsync(Func<Task<HttpResponseMessage>> action)
    {
        var samples = new List<double>();

        for (var i = 0; i < 5; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            (await action()).Dispose();
            stopwatch.Stop();
            samples.Add(stopwatch.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static string ForgeToken(Guid tenantId, Guid userId, string key)
    {
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var credentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(
                System.Text.Encoding.UTF8.GetBytes(key)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: "https://localhost:5080",
            audience: "mautodesk-api",
            claims:
            [
                new System.Security.Claims.Claim("sub", userId.ToString()),
                new System.Security.Claims.Claim("tenant", tenantId.ToString()),
                new System.Security.Claims.Claim("perm", "inventory.vehicle.read"),
            ],
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: credentials);

        return handler.WriteToken(token);
    }

    private static async Task SeedUnenrolledUserAsync(string email)
    {
        var hasher = new MautoDesk.Identity.Infrastructure.Argon2PasswordHasher();

        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            """
            insert into identity."user"
                (id, tenant_id, email, password_hash, first_name, last_name, status, email_verified_at)
            values (@id, @tenant, @email, @hash, 'Pending', 'User', 'active', now())
            """,
            connection);

        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("tenant", ApiFixture.TenantA);
        command.Parameters.AddWithValue("email", email);
        command.Parameters.AddWithValue("hash", hasher.Hash("correct-horse-battery-staple-9"));

        await command.ExecuteNonQueryAsync();
    }
}
