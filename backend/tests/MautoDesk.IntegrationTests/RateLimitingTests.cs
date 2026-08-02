using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Proves the rate limiter actually refuses traffic.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately its own host with a deliberately tiny limit, because the shared
/// fixture raises the limits so the rest of the suite can sign in freely. Leaving
/// the limiter untested because testing it is inconvenient is how a control ends
/// up configured but not working — which is worse than not having it, since it
/// appears on the compliance checklist either way.
/// </para>
/// <para>
/// Not part of <c>ApiCollection</c>: it needs its own configuration and must not
/// consume the shared host's partitions.
/// </para>
/// </remarks>
public sealed class RateLimitingTests : IAsyncLifetime, IDisposable
{
    private StrictLimitFactory _factory = null!;

    Task IAsyncLifetime.InitializeAsync()
    {
        _factory = new StrictLimitFactory();
        return Task.CompletedTask;
    }

    Task IAsyncLifetime.DisposeAsync()
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _factory?.Dispose();

    /// <summary>
    /// The auth endpoint refuses a burst.
    /// </summary>
    /// <remarks>
    /// This is the credential-stuffing defence. Account lockout bounds guessing
    /// against one account; nothing bounds an attacker trying one password
    /// against ten thousand accounts except this.
    /// </remarks>
    [Fact]
    public async Task Login_is_rate_limited_by_address()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();

        // Three permitted, then refused.
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new { email = $"stuffing-{attempt}@nowhere.test", password = "guess-9" });

            statuses.Add(response.StatusCode);
        }

        statuses.Should().Contain(
            HttpStatusCode.TooManyRequests,
            "a burst of login attempts from one address must be refused; got {0}",
            string.Join(", ", statuses.Select(s => (int)s)));
    }

    /// <summary>A 429 always says when to come back.</summary>
    [Fact]
    public async Task A_rejected_request_carries_retry_after_and_problem_details()
    {
        var client = _factory.CreateClient();
        HttpResponseMessage? limited = null;

        for (var attempt = 0; attempt < 10 && limited is null; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                new Uri("/api/v1/auth/login", UriKind.Relative),
                new { email = $"burst-{attempt}@nowhere.test", password = "guess-9" });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
        }

        limited.Should().NotBeNull("the limiter must engage within ten attempts at a limit of three");

        limited!.Headers.Contains("Retry-After").Should().BeTrue(
            "a limiter that refuses without saying when to return turns a well-behaved client " +
            "into a hot retry loop");

        limited.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        var body = await limited.Content.ReadAsStringAsync();
        body.Should().Contain("traceId", "support needs to correlate a rate-limit complaint");
        body.Should().NotContain("Npgsql").And.NotContain("   at MautoDesk");
    }

    /// <summary>
    /// A host with production-hostile limits, for this test only.
    /// </summary>
    private sealed class StrictLimitFactory : WebApplicationFactory<Program>
    {
        public StrictLimitFactory()
        {
            Environment.SetEnvironmentVariable("ConnectionStrings__Default", ApiFixture.AppConnectionString);
            Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");
            Environment.SetEnvironmentVariable("Jwt__SigningKey", ApiFixture.TestSigningKey);
            Environment.SetEnvironmentVariable("Jwt__Issuer", "https://localhost:5080");
            Environment.SetEnvironmentVariable("Jwt__Audience", "mautodesk-api");
            Environment.SetEnvironmentVariable("Encryption__MasterKey", ApiFixture.TestMasterKey);
            Environment.SetEnvironmentVariable("Encryption__KeyId", "test-1");

        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
        }

        /// <summary>
        /// Applies the strict limit for exactly as long as it takes to build.
        /// </summary>
        /// <remarks>
        /// <c>Program.cs</c> uses top-level statements, so configuration is read
        /// during the host build and environment variables are the only hook
        /// early enough. Setting the strict limit in the constructor instead
        /// leaves it applied while the SHARED fixture host is built — which
        /// silently rate-limits the whole rest of the suite. Set, build, restore.
        /// </remarks>
        protected override IHost CreateHost(IHostBuilder builder)
        {
            Environment.SetEnvironmentVariable("RateLimits__AuthPermitsPerWindow", "3");
            Environment.SetEnvironmentVariable("RateLimits__AuthWindowMinutes", "15");

            try
            {
                return base.CreateHost(builder);
            }
            finally
            {
                Environment.SetEnvironmentVariable("RateLimits__AuthPermitsPerWindow", "10000");
            }
        }
    }
}
