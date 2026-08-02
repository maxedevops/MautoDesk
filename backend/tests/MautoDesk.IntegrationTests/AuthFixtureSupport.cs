using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MautoDesk.Identity.Infrastructure;
using Npgsql;

namespace MautoDesk.IntegrationTests;

/// <summary>A signed-in test user and the credentials that got them there.</summary>
public sealed record AuthenticatedUser(
    Guid TenantId,
    Guid UserId,
    string Email,
    string Password,
    string TotpSecret,
    string AccessToken,
    string RefreshToken);

/// <summary>
/// Drives the real authentication flow to obtain real tokens.
/// </summary>
/// <remarks>
/// Deliberately does <b>not</b> mint tokens directly. Going through login →
/// enrolment → TOTP means the tests exercise the same path a dealer does, so a
/// break anywhere in it fails the suite rather than hiding behind a shortcut
/// that only tests exist to satisfy.
/// </remarks>
public static class AuthFlow
{
    private const string Password = "correct-horse-battery-staple-9";

    /// <summary>Seeds a user, enrols an authenticator, and signs them in.</summary>
    public static async Task<AuthenticatedUser> CreateUserAsync(
        HttpClient client,
        string adminConnectionString,
        Guid tenantId,
        string email,
        params string[] permissions)
    {
        var userId = await SeedUserAsync(adminConnectionString, tenantId, email, permissions)
            .ConfigureAwait(false);

        // Step 1: password. MFA is mandatory, so this never returns tokens.
        var login = await PostAsync(client, "/api/v1/auth/login", new { email, password = Password })
            .ConfigureAwait(false);

        var outcome = login.GetProperty("outcome").GetString();
        if (outcome != "mfa_enrolment_required")
        {
            throw new InvalidOperationException(
                $"Expected a new account to require MFA enrolment, got '{outcome}'. " +
                "If this changed, mandatory MFA may have regressed.");
        }

        var challenge = login.GetProperty("challengeToken").GetString()!;
        var secret = login.GetProperty("enrolmentSecret").GetString()!;

        // Step 2: prove possession of the authenticator.
        var confirmed = await PostAsync(
            client,
            "/api/v1/auth/mfa/enrol",
            new { challengeToken = challenge, code = CurrentCode(secret) })
            .ConfigureAwait(false);

        var tokens = confirmed.GetProperty("tokens");

        return new AuthenticatedUser(
            tenantId,
            userId,
            email,
            Password,
            secret,
            tokens.GetProperty("accessToken").GetString()!,
            tokens.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>Signs an existing user in again, returning a fresh token pair.</summary>
    public static async Task<(string AccessToken, string RefreshToken)> SignInAsync(
        HttpClient client,
        AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var login = await PostAsync(client, "/api/v1/auth/login", new { email = user.Email, password = user.Password })
            .ConfigureAwait(false);

        var challenge = login.GetProperty("challengeToken").GetString()!;

        // A TOTP code is single-use, so a second sign-in inside the same 30
        // second step must wait for the next one rather than replaying.
        await WaitForNextStepAsync(user.TotpSecret).ConfigureAwait(false);

        var verified = await PostAsync(
            client,
            "/api/v1/auth/mfa/verify",
            new { challengeToken = challenge, code = CurrentCode(user.TotpSecret) })
            .ConfigureAwait(false);

        var tokens = verified.GetProperty("tokens");
        return (tokens.GetProperty("accessToken").GetString()!, tokens.GetProperty("refreshToken").GetString()!);
    }

    /// <summary>The current TOTP code for a secret.</summary>
    public static string CurrentCode(string secret) =>
        new OtpNet.Totp(OtpNet.Base32Encoding.ToBytes(secret), step: 30).ComputeTotp(DateTime.UtcNow);

    /// <summary>Waits until the TOTP step rolls over, so the next code differs.</summary>
    public static async Task WaitForNextStepAsync(string secret)
    {
        var before = CurrentCode(secret);
        var deadline = DateTime.UtcNow.AddSeconds(35);

        while (DateTime.UtcNow < deadline)
        {
            if (CurrentCode(secret) != before)
            {
                return;
            }

            await Task.Delay(500).ConfigureAwait(false);
        }
    }

    public static HttpClient WithToken(this HttpClient client, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static async Task<JsonElement> PostAsync(HttpClient client, string path, object body)
    {
        var response = await client.PostAsJsonAsync(new Uri(path, UriKind.Relative), body).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"POST {path} returned {(int)response.StatusCode}: {payload}");
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    /// <summary>
    /// Inserts a user with a real Argon2id hash and their role grants.
    /// </summary>
    /// <remarks>
    /// The hash is produced by the production hasher, not a fixture stub, so a
    /// change to the Argon2 parameters is exercised by every test that signs in.
    /// </remarks>
    private static async Task<Guid> SeedUserAsync(
        string adminConnectionString,
        Guid tenantId,
        string email,
        string[] permissions)
    {
        var userId = Guid.CreateVersion7();
        var roleId = Guid.CreateVersion7();
        var hash = new Argon2PasswordHasher().Hash(Password);

        await using var connection = new NpgsqlConnection(adminConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection, """
            insert into identity."user"
                (id, tenant_id, email, password_hash, first_name, last_name, status, email_verified_at)
            values (@id, @tenant, @email, @hash, 'Test', 'User', 'active', now())
            """,
            ("id", userId), ("tenant", tenantId), ("email", email), ("hash", hash))
            .ConfigureAwait(false);

        await ExecuteAsync(connection, """
            insert into identity.role (id, tenant_id, code, name, is_system)
            values (@id, @tenant, @code, @name, false)
            """,
            ("id", roleId), ("tenant", tenantId),
            ("code", $"test-{roleId:N}"[..20]), ("name", "Test Role"))
            .ConfigureAwait(false);

        foreach (var permission in permissions)
        {
            await ExecuteAsync(connection,
                "insert into identity.role_permission (role_id, permission_code) values (@role, @perm)",
                ("role", roleId), ("perm", permission)).ConfigureAwait(false);
        }

        await ExecuteAsync(connection,
            "insert into identity.user_role (tenant_id, user_id, role_id) values (@tenant, @user, @role)",
            ("tenant", tenantId), ("user", userId), ("role", roleId)).ConfigureAwait(false);

        return userId;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Statements are literals in this file; every value is a parameter.")]
    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);

        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync().ConfigureAwait(false);
    }
}
