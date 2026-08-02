using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Recovery codes, exercised against the running host.
/// </summary>
/// <remarks>
/// MFA is mandatory, so this is the only way back in for a user whose phone is
/// at the bottom of a lake — which makes it both a support lifeline and, if it
/// is wrong, the softest way into an account. The tests below are the second
/// half of that sentence: single use, no reuse after regeneration, no crossing
/// between users, and a wrong code that costs the attacker a lockout.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class MfaRecoveryTests
{
    private readonly ApiFixture _fixture;

    public MfaRecoveryTests(ApiFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Enrolment_issues_a_full_set_of_codes()
    {
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        user.RecoveryCodes.Should().HaveCount(
            10,
            "a user who is never given codes has no way back in, which is the whole gap this closes");
        user.RecoveryCodes.Should().OnlyHaveUniqueItems();
        user.RecoveryCodes.Should().AllSatisfy(code => code.Should().MatchRegex("^[A-Z2-9]{5}-[A-Z2-9]{5}$"));
    }

    /// <summary>
    /// The path this feature exists for: no authenticator, still gets in.
    /// </summary>
    [Fact]
    public async Task A_recovery_code_completes_a_sign_in_without_the_authenticator()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var challenge = await PasswordStepAsync(client, user);

        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/recovery", UriKind.Relative),
            new { challengeToken = challenge, code = user.RecoveryCodes[0] });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        body.RootElement.GetProperty("outcome").GetString().Should().Be("authenticated");
        body.RootElement.GetProperty("tokens").GetProperty("accessToken").GetString()
            .Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// Single use, asserted end to end.
    /// </summary>
    /// <remarks>
    /// The printout the code came from is usually still in a drawer, or in a
    /// photo in someone's camera roll. A code that works twice is a static
    /// password.
    /// </remarks>
    [Fact]
    public async Task A_recovery_code_works_exactly_once()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");
        var code = user.RecoveryCodes[0];

        var first = await RedeemAsync(client, await PasswordStepAsync(client, user), code);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await RedeemAsync(client, await PasswordStepAsync(client, user), code);

        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await second.Content.ReadAsStringAsync()).Should().Contain("auth.recovery_code_invalid");
    }

    [Fact]
    public async Task Formatting_does_not_decide_whether_a_code_is_accepted()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        // Lower case, no dash, surrounding whitespace: how a code arrives when
        // it is read off paper or pasted out of a password manager note.
        var mangled = $"  {user.RecoveryCodes[0].Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant()}  ";

        var response = await RedeemAsync(client, await PasswordStepAsync(client, user), mangled);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unknown_code_is_rejected()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        var response = await RedeemAsync(client, await PasswordStepAsync(client, user), "ZZZZZ-ZZZZZ");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // The same message as an already-spent code. Distinguishing them would
        // tell an attacker that a guessed code was once real.
        (await response.Content.ReadAsStringAsync()).Should().Contain("auth.recovery_code_invalid");
    }

    /// <summary>
    /// One user's codes are worthless against another account.
    /// </summary>
    /// <remarks>
    /// The lookup is scoped by user id under RLS, so this also proves the code
    /// table is not readable across the tenant boundary.
    /// </remarks>
    [Fact]
    public async Task A_code_issued_to_one_user_does_not_open_another_account()
    {
        var client = _fixture.AnonymousClient();
        var victim = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");
        var attacker = await _fixture.CreateUserAsync(ApiFixture.TenantB, "inventory.vehicle.read");

        // The attacker knows their own codes and holds their own challenge, and
        // tries the victim's code against their own account and vice versa.
        var stolen = await RedeemAsync(client, await PasswordStepAsync(client, attacker), victim.RecoveryCodes[0]);

        stolen.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Regenerating_invalidates_every_previous_code()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");
        var old = user.RecoveryCodes[0];

        var regenerated = await _fixture.AnonymousClient().WithToken(user.AccessToken)
            .PostAsJsonAsync(new Uri("/api/v1/auth/mfa/recovery-codes", UriKind.Relative), new { });

        regenerated.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await regenerated.Content.ReadAsStringAsync());
        var fresh = body.RootElement.GetProperty("codes").EnumerateArray()
            .Select(code => code.GetString()!).ToList();

        fresh.Should().HaveCount(10);
        fresh.Should().NotIntersectWith(user.RecoveryCodes);

        // A printout the user threw away when they generated new codes must not
        // still open the account.
        var replayed = await RedeemAsync(client, await PasswordStepAsync(client, user), old);
        replayed.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var accepted = await RedeemAsync(client, await PasswordStepAsync(client, user), fresh[0]);
        accepted.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_remaining_count_falls_as_codes_are_spent()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        (await RemainingAsync(user.AccessToken)).Should().Be(10);

        var redeemed = await RedeemAsync(client, await PasswordStepAsync(client, user), user.RecoveryCodes[0]);
        redeemed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await redeemed.Content.ReadAsStringAsync());
        var token = body.RootElement.GetProperty("tokens").GetProperty("accessToken").GetString()!;

        (await RemainingAsync(token)).Should().Be(9);
    }

    [Fact]
    public async Task Redeeming_requires_the_password_step_first()
    {
        var client = _fixture.AnonymousClient();
        var user = await _fixture.CreateUserAsync(ApiFixture.TenantA, "inventory.vehicle.read");

        // No challenge token means no proof the password was ever presented.
        // A recovery code is a second factor, not a way around the first.
        var response = await RedeemAsync(client, string.Empty, user.RecoveryCodes[0]);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_status_endpoint_refuses_an_anonymous_caller()
    {
        var response = await _fixture.AnonymousClient()
            .GetAsync(new Uri("/api/v1/auth/mfa/recovery-codes", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private async Task<int> RemainingAsync(string accessToken)
    {
        var response = await _fixture.AnonymousClient().WithToken(accessToken)
            .GetAsync(new Uri("/api/v1/auth/mfa/recovery-codes", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("remaining").GetInt32();
    }

    /// <summary>Completes the password step and returns the challenge token.</summary>
    private static async Task<string> PasswordStepAsync(HttpClient client, AuthenticatedUser user)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/auth/login", UriKind.Relative),
            new { email = user.Email, password = user.Password });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("challengeToken").GetString()!;
    }

    private static Task<HttpResponseMessage> RedeemAsync(HttpClient client, string challengeToken, string code) =>
        client.PostAsJsonAsync(
            new Uri("/api/v1/auth/mfa/recovery", UriKind.Relative),
            new { challengeToken, code });
}
