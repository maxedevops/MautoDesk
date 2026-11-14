using System.Net.Http.Headers;
using MautoDesk.Inventory.Application;
using MautoDesk.Inventory.Contracts;
using MautoDesk.Inventory.Domain;
using MautoDesk.SharedKernel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// A real API host against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not an in-memory provider. The entire tenant isolation design
/// is row-level security enforced by PostgreSQL; an in-memory database has no
/// RLS, so a test suite built on one would pass while proving nothing about the
/// property that matters most in this system.
/// </para>
/// <para>
/// The host connects as <c>mautodesk_app</c> — no superuser, no BYPASSRLS —
/// exactly as production does. Fixture setup uses a separate superuser
/// connection, because seeding two tenants is precisely the cross-tenant write
/// the application role is supposed to be unable to perform.
/// </para>
/// </remarks>
public sealed class ApiFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>
    /// How the application connects: as <c>mautodesk_app</c>, always.
    /// </summary>
    /// <remarks>
    /// Overridable by environment so CI can point at its own service container
    /// while a developer runs against docker-compose on an alternate port. The
    /// default is the compose setup from the repository README.
    /// </remarks>
    public static readonly string AppConnectionString =
        Environment.GetEnvironmentVariable("TEST_APP_CONNECTION")
        ?? "Host=localhost;Port=55432;Database=mautodesk;Username=mautodesk_app;Password=devpw;" +
           "Include Error Detail=true";

    /// <summary>
    /// A privileged connection, used only to build fixtures.
    /// </summary>
    /// <remarks>
    /// Seeding two tenants is exactly the cross-tenant write the application
    /// role must be unable to perform, so the fixture cannot use the app
    /// connection to set itself up.
    /// </remarks>
    private static readonly string SuperuserConnectionString =
        Environment.GetEnvironmentVariable("TEST_ADMIN_CONNECTION")
        ?? "Host=localhost;Port=55432;Database=mautodesk;Username=postgres;Password=devpw";

    internal static string AdminConnectionString => SuperuserConnectionString;

    /// <summary>
    /// The dispatcher's connection, as <c>mautodesk_dispatcher</c>.
    /// </summary>
    /// <remarks>
    /// A separate role on purpose (V0011). Pointing the dispatcher at the
    /// superuser connection would make the outbox tests pass while proving
    /// nothing about whether the least-privileged role can actually do the job.
    /// </remarks>
    public static readonly string DispatcherConnectionString =
        Environment.GetEnvironmentVariable("TEST_DISPATCHER_CONNECTION")
        ?? "Host=localhost;Port=55432;Database=mautodesk;Username=mautodesk_dispatcher;" +
           "Password=devpw;Include Error Detail=true";

    /// <summary>32 bytes, base64. Test-only, and never used outside the suite.</summary>
    public const string TestSigningKey = "dGVzdC1zaWduaW5nLWtleS0zMi1ieXRlcy1sb25nISE=";

    public const string TestMasterKey = "dGVzdC1tYXN0ZXIta2V5LTMyLWJ5dGVzLWxvbmchISE=";

    public static readonly Guid TenantA = Guid.Parse("aaaaaaaa-0000-4000-8000-00000000a001");
    public static readonly Guid TenantB = Guid.Parse("bbbbbbbb-0000-4000-8000-00000000b002");

    /// <summary>A fully signed-in user in tenant A, holding inventory permissions.</summary>
    public AuthenticatedUser UserA { get; private set; } = null!;

    /// <summary>The same, in tenant B. The counterparty for every isolation test.</summary>
    public AuthenticatedUser UserB { get; private set; } = null!;

    /// <summary>
    /// Sets configuration through the environment, not through
    /// <c>ConfigureAppConfiguration</c>.
    /// </summary>
    /// <remarks>
    /// <c>Program.cs</c> uses top-level statements, so
    /// <c>WebApplication.CreateBuilder</c> reads configuration and validates the
    /// connection string as the very first thing it does — before
    /// <see cref="WebApplicationFactory{T}"/> gets a chance to contribute an
    /// in-memory source. Environment variables are read by the default
    /// configuration sources during that initial build, so they are the only
    /// hook that lands early enough.
    /// </remarks>
    public ApiFixture()
    {
        Environment.SetEnvironmentVariable("ConnectionStrings__Default", AppConnectionString);
        Environment.SetEnvironmentVariable("ASPNETCORE_ENVIRONMENT", "Development");

        // Fixed test keys. Deterministic so a token minted in one test is valid
        // in another; both are 32 bytes, matching what the host demands in
        // production rather than a relaxed test-only path.
        Environment.SetEnvironmentVariable("Jwt__SigningKey", TestSigningKey);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "https://localhost:5080");
        Environment.SetEnvironmentVariable("Jwt__Audience", "mautodesk-api");
        Environment.SetEnvironmentVariable("Encryption__MasterKey", TestMasterKey);
        Environment.SetEnvironmentVariable("Encryption__KeyId", "test-1");

        // Object storage: the MinIO from docker-compose, with the buckets
        // minio-init creates. Overridable so CI can point at its own service.
        Environment.SetEnvironmentVariable(
            "Storage__ServiceUrl",
            Environment.GetEnvironmentVariable("TEST_STORAGE_URL") ?? "http://localhost:9000");
        Environment.SetEnvironmentVariable("Storage__AccessKey", "mautodesk");
        Environment.SetEnvironmentVariable("Storage__SecretKey", "devpassword");

        // No clamd in the test environment: it takes three minutes to load its
        // signature databases, which is not a price worth paying on every run.
        // The fail-closed behaviour it exists for is asserted directly in
        // MalwareScannerTests instead of being implied here.
        Environment.SetEnvironmentVariable("MalwareScanning__Required", "false");

        // The suite signs in dozens of times from one address, which the
        // production auth limit (10 per 15 minutes) would correctly refuse.
        // Raised here so the limiter does not rate-limit the tests; RateLimitingTests
        // configures a LOW limit separately and proves the control actually fires.
        Environment.SetEnvironmentVariable("RateLimits__AuthPermitsPerWindow", "10000");
        Environment.SetEnvironmentVariable("RateLimits__ReadTokenLimit", "100000");
        Environment.SetEnvironmentVariable("RateLimits__ReadTokensPerPeriod", "100000");
        Environment.SetEnvironmentVariable("RateLimits__WriteTokenLimit", "100000");
        Environment.SetEnvironmentVariable("RateLimits__WriteTokensPerPeriod", "100000");

        // The dispatcher's background loop is off, and the tests drive
        // RunOnceAsync directly. A loop polling every five seconds underneath a
        // suite that asserts on outbox state would dispatch messages out from
        // under the assertions and fail intermittently — the worst kind of test.
        // The claim/publish/record path itself is fully exercised; only the
        // `while` loop around it is not.
        Environment.SetEnvironmentVariable("Outbox__Enabled", "false");
        Environment.SetEnvironmentVariable("Outbox__ConnectionString", DispatcherConnectionString);
    }

    public Guid TenantAVehicleId { get; private set; }

    public Guid TenantBVehicleId { get; private set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureTestServices(services =>
        {
            // The real decoder calls NHTSA. A test suite that depends on a
            // government API being up is a test suite that fails for reasons
            // that have nothing to do with the code.
            services.AddScoped<IVinDecoder, StubVinDecoder>();
        });
    }

    // Explicit interface implementation: xUnit's IAsyncLifetime returns Task,
    // while WebApplicationFactory already defines a ValueTask DisposeAsync.
    // Implementing both implicitly is a signature collision.
    async Task IAsyncLifetime.InitializeAsync() => await SeedAsync().ConfigureAwait(false);

    async Task IAsyncLifetime.DisposeAsync() => await CleanupAsync().ConfigureAwait(false);

    private async Task SeedAsync()
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        // V0011 creates mautodesk_dispatcher without a password, exactly as
        // V0001 does for mautodesk_app, so that no migration file contains a
        // credential. Development and CI set one; here the fixture does.
        await ExecuteAsync(connection,
            "alter role mautodesk_dispatcher with password 'devpw'").ConfigureAwait(false);

        // Seeded as superuser, which bypasses RLS — that is the point. Creating
        // two tenants' data is exactly what the application role must not be
        // able to do, so the fixture cannot use it.
        await ExecuteAsync(connection, """
            insert into platform.tenant (id, slug, legal_name, state_code)
            values (@a, 'alpha-motors-it', 'Alpha Motors LLC', 'OK'),
                   (@b, 'bravo-auto-it',   'Bravo Auto Sales', 'TX')
            on conflict (id) do nothing
            """, ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);

        TenantAVehicleId = await SeedVehicleAsync(connection, TenantA, "IT-A-100", "1FTFW1ET5MFA48219")
            .ConfigureAwait(false);
        TenantBVehicleId = await SeedVehicleAsync(connection, TenantB, "IT-B-200", "2HGFC2F59LH551903")
            .ConfigureAwait(false);

        // Real users, signed in through the real flow: password -> mandatory MFA
        // enrolment -> TOTP -> tokens. No shortcut that mints a token directly,
        // because a shortcut would let the login path rot unnoticed.
        var permissions = new[]
        {
            "inventory.vehicle.read", "inventory.vehicle.write", "inventory.vehicle.delete",
            "inventory.price.write", "inventory.publish", "inventory.photo.write",

            // Sales and compliance. Gross is deliberately included here and
            // deliberately excluded from the user DealWorkflowTests builds for the
            // permission test, so both halves of that gate are exercised.
            "sales.deal.read", "sales.deal.write", "sales.deal.approve", "sales.deal.void",
            "sales.gross.read", "compliance.evaluation.read",
        };

        UserA = await AuthFlow.CreateUserAsync(
            CreateClient(), SuperuserConnectionString, TenantA,
            $"a-{Guid.NewGuid():N}@alpha.test", permissions).ConfigureAwait(false);

        UserB = await AuthFlow.CreateUserAsync(
            CreateClient(), SuperuserConnectionString, TenantB,
            $"b-{Guid.NewGuid():N}@bravo.test", permissions).ConfigureAwait(false);
    }

    /// <summary>Creates an extra user with an explicit permission set.</summary>
    public Task<AuthenticatedUser> CreateUserAsync(Guid tenantId, params string[] permissions) =>
        AuthFlow.CreateUserAsync(
            CreateClient(), SuperuserConnectionString, tenantId,
            $"u-{Guid.NewGuid():N}@test.local", permissions);

    private static async Task CleanupAsync()
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await ExecuteAsync(connection,
            "delete from app.outbox_message where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from app.idempotency_key where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        // Deals, calculations and compliance runs.
        //
        // These tables are append-only: a trigger blocks DELETE, and it blocks it
        // for the superuser too, because a trigger does not care who you are. So
        // the fixture disables them for the duration of the teardown and puts them
        // back — which is exactly the kind of privilege a test fixture is allowed
        // and the application role is not. Getting this wrong is not subtle: the
        // delete throws, teardown aborts before it reaches the vehicles, and the
        // NEXT run fails on a duplicate stock number a long way from the cause.
        await ExecuteAsync(connection, """
            alter table sales.deal_calculation disable trigger deal_calculation_immutable;
            alter table compliance.compliance_run disable trigger compliance_run_immutable;
            alter table compliance.compliance_result disable trigger compliance_result_immutable;
            """).ConfigureAwait(false);

        try
        {
            await ExecuteAsync(connection,
                "delete from compliance.compliance_result where tenant_id in (@a, @b)",
                ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "delete from compliance.compliance_run where tenant_id in (@a, @b)",
                ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);

            // The deal points at its current calculation and the calculation
            // points back at the deal, so the pointer is cleared first.
            await ExecuteAsync(connection,
                "update sales.deal set current_calculation_id = null where tenant_id in (@a, @b)",
                ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "delete from sales.deal_calculation where tenant_id in (@a, @b)",
                ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
            await ExecuteAsync(connection,
                "delete from sales.deal where tenant_id in (@a, @b)",
                ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        }
        finally
        {
            await ExecuteAsync(connection, """
                alter table sales.deal_calculation enable trigger deal_calculation_immutable;
                alter table compliance.compliance_run enable trigger compliance_run_immutable;
                alter table compliance.compliance_result enable trigger compliance_result_immutable;
                """).ConfigureAwait(false);
        }
        await ExecuteAsync(connection,
            "delete from inventory.vehicle where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        // Identity rows first: sessions and tokens reference users, users
        // reference tenants.
        await ExecuteAsync(connection,
            "delete from identity.refresh_token where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.session where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.mfa_factor where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.user_role where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.role_permission where role_id in " +
            "(select id from identity.role where tenant_id in (@a, @b))",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.role where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.\"user\" where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from identity.login_attempt where tenant_id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
        await ExecuteAsync(connection,
            "delete from platform.tenant where id in (@a, @b)",
            ("a", TenantA), ("b", TenantB)).ConfigureAwait(false);
    }

    /// <summary>An HTTP client carrying a real bearer token.</summary>
    public HttpClient ClientFor(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", user.AccessToken);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    /// <summary>A client with no tenant at all.</summary>
    public HttpClient AnonymousClient() => CreateClient();

    /// <summary>
    /// Every vehicle id genuinely owned by a tenant, read as superuser.
    /// </summary>
    /// <remarks>
    /// Isolation assertions compare against this rather than against a stock
    /// number prefix. Prefixes were the obvious first attempt and were wrong:
    /// other tests in the same collection add vehicles with their own naming, so
    /// a prefix assertion fails for reasons that have nothing to do with
    /// isolation. Ownership is the property actually under test.
    /// </remarks>
    public static async Task<HashSet<Guid>> VehicleIdsOwnedByAsync(Guid tenantId)
    {
        await using var connection = new NpgsqlConnection(SuperuserConnectionString);
        await connection.OpenAsync().ConfigureAwait(false);

        await using var command = new NpgsqlCommand(
            "select id from inventory.vehicle where tenant_id = @tenant and deleted_at is null",
            connection);
        command.Parameters.AddWithValue("tenant", tenantId);

        var ids = new HashSet<Guid>();
        await using var reader = await command.ExecuteReaderAsync().ConfigureAwait(false);

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    private static async Task<Guid> SeedVehicleAsync(
        NpgsqlConnection connection,
        Guid tenantId,
        string stockNumber,
        string vin)
    {
        var id = Guid.CreateVersion7();

        await ExecuteAsync(connection, """
            insert into inventory.vehicle
                (id, tenant_id, stock_number, vin, model_year, make, model, status,
                 list_price, acquired_at, is_published)
            values (@id, @tenant, @stock, @vin, 2021, 'Ford', 'F-150', 'available',
                    38450.00, current_date - 44, false)
            """,
            ("id", id), ("tenant", tenantId), ("stock", stockNumber), ("vin", vin))
            .ConfigureAwait(false);

        return id;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is authored in this file as a literal; all values are " +
                        "bound as parameters. No caller-supplied SQL reaches this method.")]
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

/// <summary>A VIN decoder that never touches the network.</summary>
internal sealed class StubVinDecoder : IVinDecoder
{
    public Task<Result<VinDecodeDto>> DecodeAsync(
        Vin vin,
        bool bypassCache,
        CancellationToken cancellationToken) =>
        Task.FromResult(Result<VinDecodeDto>.Success(new VinDecodeDto(
            vin.Value,
            "stub",
            FromCache: false,
            CheckDigitValid: vin.HasValidCheckDigit,
            ModelYear: 2021,
            Make: "Ford",
            Model: "F-150",
            Trim: "XLT",
            BodyStyle: "Pickup",
            DriveType: "4WD",
            Engine: "3.5L V6",
            FuelType: "Gasoline",
            Transmission: "Automatic",
            Manufacturer: "FORD MOTOR COMPANY",
            ErrorText: null)));
}

// CA1711 wants no 'Collection' suffix; xUnit's collection-fixture convention
// requires exactly this shape, and xUnit wins on its own test types.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "xUnit collection definition convention.")]
[CollectionDefinition(nameof(ApiCollection))]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>;
