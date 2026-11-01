using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MautoDesk.Sales.Application;
using MautoDesk.Sales.Contracts;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// The deal engine and the regulatory engine, driven through the real host
/// against a real PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests prove the engines compute and decide correctly. These prove the
/// wiring: that a snapshot lands in an append-only table, that a second
/// calculation supersedes rather than overwrites, that a compliance run and its
/// verdicts commit together, and that none of it crosses a tenant boundary.
/// </para>
/// <para>
/// <b>All jurisdictions and rules here are fictional</b> — a state code of
/// <c>ZZ</c> and rules named <c>ZZ-TEST-*</c>. The seeded Oklahoma, Kansas and
/// Texas rule sets are left exactly as they are: unapproved skeletons. One test
/// below depends on that and would fail if somebody approved one to make a demo
/// work, which is the intent.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class DealWorkflowTests : IAsyncLifetime
{
    private static readonly Guid FixtureJurisdiction = Guid.Parse("77777777-7777-4777-8777-000000000001");
    private static readonly Guid FixtureRuleSet = Guid.Parse("77777777-7777-4777-8777-000000000002");

    private readonly ApiFixture _fixture;

    public DealWorkflowTests(ApiFixture fixture) => _fixture = fixture;

    async Task IAsyncLifetime.InitializeAsync() => await SeedFixtureJurisdictionAsync();

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    // -----------------------------------------------------------------------
    // The refusal that matters most.
    // -----------------------------------------------------------------------

    /// <summary>
    /// The seeded Oklahoma rule set will not price a deal.
    /// </summary>
    /// <remarks>
    /// The single most important assertion in this file. Oklahoma is seeded with
    /// <c>'UNVERIFIED'</c> placeholders and <c>approved_at IS NULL</c>, so the
    /// engine refuses — and it must keep refusing until a named reviewer approves
    /// a rule set against a cited primary source. If this test ever goes red
    /// because somebody approved the skeleton, that is the test working.
    /// </remarks>
    [Fact]
    public async Task The_seeded_oklahoma_rule_set_refuses_to_price_a_deal()
    {
        var oklahoma = await JurisdictionIdAsync("OK");
        var client = _fixture.ClientFor(_fixture.UserA);

        var deal = await CreateDealAsync(client, oklahoma);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = "20000.00" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var problem = await response.Content.ReadAsStringAsync();
        problem.Should().Contain("deal.rule_set_unapproved");
        problem.Should().Contain("named approver");
    }

    /// <summary>A deal with no jurisdiction cannot be priced at all.</summary>
    [Fact]
    public async Task A_deal_without_a_jurisdiction_cannot_be_priced()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, jurisdictionId: null);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = "20000.00" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("deal.no_jurisdiction");
    }

    // -----------------------------------------------------------------------
    // Pricing, against an approved fixture rule set.
    // -----------------------------------------------------------------------

    /// <summary>
    /// An approved rule set prices the deal and the figures reach the database.
    /// </summary>
    /// <remarks>
    /// 20,000 at the fixture's 5% is 1,000 of tax, so 21,000 due. Worked by hand,
    /// not copied from a run.
    /// </remarks>
    [Fact]
    public async Task An_approved_rule_set_prices_the_deal_and_persists_the_snapshot()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = "20000.00" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var calculation = await response.Content.ReadFromJsonAsync<DealCalculationDto>();
        calculation.Should().NotBeNull();

        calculation!.Version.Should().Be(1);
        calculation.SalesTax.Should().Be("1000.00");
        calculation.TotalSalePrice.Should().Be("21000.00");
        calculation.BalanceDue.Should().Be("21000.00");
        calculation.ContentHash.Should().NotBeNullOrEmpty();

        // Money is a string on the wire, never a JSON number. Asserted on the raw
        // payload because a DTO round-trip would hide a numeric field.
        var raw = await client.GetStringAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculation", UriKind.Relative));
        raw.Should().Contain("\"totalSalePrice\":\"21000.00\"");

        var stored = await ReadSnapshotAsync(deal.Id, version: 1);
        stored.TotalSalePrice.Should().Be(21_000.00m);
        stored.SupersededAt.Should().BeNull();
    }

    /// <summary>
    /// A second calculation appends; it does not overwrite.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The property the whole snapshot design exists for. After the second run
    /// there are two rows and version 1's figures are exactly what they were — so
    /// "what were the numbers when the customer signed" is still answerable.
    /// </para>
    /// <para>
    /// Note what is <em>not</em> asserted: that version 1 was marked superseded.
    /// <c>superseded_at</c> exists as a column but the immutability trigger
    /// forbids setting it, so which version is current is expressed by the deal's
    /// pointer instead — evidence that cannot be set to something untrue.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Recalculating_appends_a_version_rather_than_overwriting()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        var first = await CalculateAsync(client, deal.Id, "20000.00");
        var second = await CalculateAsync(client, deal.Id, "18500.00");

        second.Version.Should().Be(2);
        second.TotalSalePrice.Should().Be("19425.00", "18,500 plus 5% is 19,425");
        second.Id.Should().NotBe(first.Id);

        var original = await ReadSnapshotAsync(deal.Id, version: 1);
        original.TotalSalePrice.Should().Be(21_000.00m, "the original figures are untouched");

        (await CountSnapshotsAsync(deal.Id)).Should().Be(2, "both versions are kept");

        // The pointer, not a flag, is what says which one is current.
        var current = await client.GetFromJsonAsync<DealDto>(
            new Uri($"/api/v1/deals/{deal.Id}", UriKind.Relative));

        current!.CurrentCalculationId.Should().Be(second.Id);
        current.CurrentCalculation!.Version.Should().Be(2);
        current.CurrentCalculation.TotalSalePrice.Should().Be("19425.00");
    }

    /// <summary>The snapshot table refuses an update, even to the application role.</summary>
    /// <remarks>
    /// The trigger and the revoked grant are both in place; this proves it end to
    /// end on a row the application itself wrote.
    /// </remarks>
    [Fact]
    public async Task A_written_snapshot_cannot_be_edited()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);
        await CalculateAsync(client, deal.Id, "20000.00");

        await using var connection = new NpgsqlConnection(ApiFixture.AppConnectionString);
        await connection.OpenAsync();

        await using var scope = new NpgsqlCommand(
            "select set_config('app.tenant_id', @tenant, false)", connection);
        scope.Parameters.AddWithValue("tenant", ApiFixture.TenantA.ToString());
        await scope.ExecuteNonQueryAsync();

        await using var update = new NpgsqlCommand(
            "update sales.deal_calculation set total_sale_price = 1 where deal_id = @deal",
            connection);
        update.Parameters.AddWithValue("deal", deal.Id);

        var act = async () => await update.ExecuteNonQueryAsync();

        await act.Should().ThrowAsync<PostgresException>(
            "sales.deal_calculation is append-only: a correction is a new version");
    }

    /// <summary>Gross is absent from the payload when the caller cannot see it.</summary>
    /// <remarks>
    /// Absent rather than zero. Zero is a number a manager might act on; absent is
    /// the truth.
    /// </remarks>
    [Fact]
    public async Task Gross_is_withheld_from_a_caller_without_the_permission()
    {
        var withoutGross = await _fixture.CreateUserAsync(
            ApiFixture.TenantA, "sales.deal.read", "sales.deal.write");

        var client = _fixture.ClientFor(withoutGross);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = "20000.00", VehicleCost = "16000.00" });

        var calculation = await response.Content.ReadFromJsonAsync<DealCalculationDto>();
        calculation!.Gross.Should().BeNull();

        // The same deal, read by somebody who may see it.
        var privileged = _fixture.ClientFor(_fixture.UserA);
        var visible = await privileged.GetFromJsonAsync<DealCalculationDto>(
            new Uri($"/api/v1/deals/{deal.Id}/calculation", UriKind.Relative));

        visible!.Gross.Should().NotBeNull();
        visible.Gross!.FrontGross.Should().Be("4000.00", "20,000 selling less 16,000 cost");
    }

    // -----------------------------------------------------------------------
    // Tenancy.
    // -----------------------------------------------------------------------

    /// <summary>One dealership's deal is invisible to another, by id.</summary>
    [Fact]
    public async Task Another_tenants_deal_is_not_found()
    {
        var deal = await CreateDealAsync(_fixture.ClientFor(_fixture.UserA), FixtureJurisdiction);

        var intruder = _fixture.ClientFor(_fixture.UserB);

        var read = await intruder.GetAsync(new Uri($"/api/v1/deals/{deal.Id}", UriKind.Relative));
        read.StatusCode.Should().Be(
            HttpStatusCode.NotFound,
            "404 rather than 403: answering 403 would confirm the deal exists");

        var calculate = await intruder.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = "1.00" });

        calculate.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // -----------------------------------------------------------------------
    // Compliance.
    // -----------------------------------------------------------------------

    /// <summary>A jurisdiction with no approved rules evaluates green.</summary>
    /// <remarks>
    /// Which is the honest answer: no rule has been approved, so nothing blocks.
    /// It is emphatically <em>not</em> a claim that the deal is lawful — that is
    /// what the empty rule pack means, and why Phase 5 exists.
    /// </remarks>
    [Fact]
    public async Task A_deal_in_a_jurisdiction_with_no_rules_evaluates_green()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);
        await CalculateAsync(client, deal.Id, "20000.00");

        var response = await client.PostAsync(
            new Uri($"/api/v1/deals/{deal.Id}/evaluate", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await response.Content.ReadFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>();
        run!.OverallStatus.Should().Be("green");
        run.BlockingCount.Should().Be(0);
        run.InputHash.Should().NotBeNullOrEmpty();
        run.ResultHash.Should().NotBeNullOrEmpty();
    }

    /// <summary>An approved blocking rule turns the deal red and is recorded.</summary>
    [Fact]
    public async Task An_approved_blocking_rule_turns_the_deal_red_and_is_persisted()
    {
        await SeedBlockingRuleAsync();

        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        // Financed, so the fixture rule fires.
        await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/calculate", UriKind.Relative),
            new CalculateDealRequest
            {
                VehiclePrice = "20000.00",
                Finance = new FinanceRequestDto("6.00", 60),
            });

        var response = await client.PostAsync(
            new Uri($"/api/v1/deals/{deal.Id}/evaluate", UriKind.Relative), null);

        var run = await response.Content.ReadFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>();

        run!.OverallStatus.Should().Be("red");
        run.BlockingCount.Should().Be(1);
        run.Results.Should().ContainSingle(result => result.RuleCode == "ZZ-TEST-FIN-001");

        var blocker = run.Results.Single(result => result.RuleCode == "ZZ-TEST-FIN-001");
        blocker.Status.Should().Be("block");
        blocker.RequirementCode.Should().Be("SECURITY_AGREEMENT");
        blocker.EvaluationDetail.Should().NotBeEmpty("the trace is the evidence a reviewer reads");

        // Persisted, and readable back through the GET.
        var latest = await client.GetFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>(
            new Uri($"/api/v1/deals/{deal.Id}/compliance", UriKind.Relative));

        latest!.Id.Should().Be(run.Id);
        latest.Results.Should().HaveCount(run.Results.Count);

        (await CountComplianceResultsAsync(run.Id)).Should().Be(run.Results.Count);
    }

    /// <summary>Evaluating twice with the same facts reproduces the same hashes.</summary>
    /// <remarks>
    /// The reproducibility guarantee, end to end rather than in the unit tests: the
    /// same deal, the same rules, the same answer, provably.
    /// </remarks>
    [Fact]
    public async Task Two_evaluations_of_an_unchanged_deal_agree()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);
        await CalculateAsync(client, deal.Id, "20000.00");

        var uri = new Uri($"/api/v1/deals/{deal.Id}/evaluate", UriKind.Relative);

        // The status is asserted before the body is read. Without it a failed
        // evaluation surfaces as a hash mismatch or a null-reference from
        // deserializing problem+json into a run — which says nothing about what
        // actually went wrong, and cost a long time to diagnose once already.
        var firstResponse = await client.PostAsync(uri, null);
        firstResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, await firstResponse.Content.ReadAsStringAsync());

        var secondResponse = await client.PostAsync(uri, null);
        secondResponse.StatusCode.Should().Be(
            HttpStatusCode.OK, await secondResponse.Content.ReadAsStringAsync());

        var first = await firstResponse.Content
            .ReadFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>();
        var second = await secondResponse.Content
            .ReadFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>();

        second!.InputHash.Should().Be(first!.InputHash);
        second.ResultHash.Should().Be(first.ResultHash);
        second.Id.Should().NotBe(first.Id, "each evaluation is its own recorded run");
    }

    /// <summary>
    /// Evaluating a deal lands in the audit ledger.
    /// </summary>
    /// <remarks>
    /// It did not, until now. <c>IAuditLog.Record</c> only adds to the change
    /// tracker and lands when the caller saves; the compliance handler owned no
    /// unit of work, so every evaluation went unrecorded — quietly, in the one
    /// ledger that exists to be trusted. The run and its audit entry now commit in
    /// the same transaction.
    /// </remarks>
    [Fact]
    public async Task Evaluating_a_deal_is_recorded_in_the_audit_ledger()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);
        await CalculateAsync(client, deal.Id, "20000.00");

        var response = await client.PostAsync(
            new Uri($"/api/v1/deals/{deal.Id}/evaluate", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var run = await response.Content
            .ReadFromJsonAsync<MautoDesk.Compliance.Contracts.ComplianceRunDto>();

        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select count(*)::int from audit.event " +
            "where action = 'compliance.evaluated' and entity_id = @id",
            connection);
        command.Parameters.AddWithValue("id", run!.Id);

        var entries = (int)(await command.ExecuteScalarAsync())!;

        entries.Should().Be(
            1,
            "the audit entry names the run it describes, and commits with it — a run without " +
            "one would be a compliance decision nobody can trace");
    }

    // -----------------------------------------------------------------------
    // Deal lifecycle.
    // -----------------------------------------------------------------------

    /// <summary>A deal cannot skip from quote to funded.</summary>
    [Fact]
    public async Task An_impossible_status_transition_is_refused()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/status", UriKind.Relative),
            new { Status = "funded" });

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("deal.invalid_transition");
    }

    /// <summary>Cancelling without a reason is refused.</summary>
    [Fact]
    public async Task Cancelling_needs_a_reason()
    {
        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, FixtureJurisdiction);

        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{deal.Id}/status", UriKind.Relative),
            new { Status = "cancelled" });

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await response.Content.ReadAsStringAsync()).Should().Contain("deal.reason_required");
    }

    // -----------------------------------------------------------------------
    // Helpers and fixtures. Fictional throughout.
    // -----------------------------------------------------------------------

    /// <summary>
    /// A deal in a state the system holds no law for is refused, not a 500.
    /// </summary>
    /// <remarks>
    /// Before the handler checked, the first thing to object was
    /// <c>compliance_run</c>'s foreign key on the jurisdiction, which reached the
    /// caller as an unhandled 500. Every dealer in an uncovered state would have
    /// seen compliance appear broken rather than be told it does not run there
    /// yet — and 500s do not carry a reason a dealer can act on.
    /// </remarks>
    [Fact]
    public async Task A_deal_in_an_uncovered_jurisdiction_is_refused_with_a_reason()
    {
        var uncovered = Guid.Parse("77777777-0000-4000-8000-000000000001");

        await using (var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString))
        {
            await connection.OpenAsync();

            // A taxing jurisdiction with no matching regulatory jurisdiction: the
            // state exists for tax purposes, no rule pack has been loaded for it.
            await using var command = new NpgsqlCommand("""
                insert into sales.tax_jurisdiction (id, state_code, level, name)
                values (@id, 'YY', 'state', 'Uncovered Jurisdiction (fixture only)')
                on conflict (id) do nothing
                """, connection);
            command.Parameters.AddWithValue("id", uncovered);
            await command.ExecuteNonQueryAsync();
        }

        var client = _fixture.ClientFor(_fixture.UserA);
        var deal = await CreateDealAsync(client, uncovered);

        var response = await client.PostAsync(
            new Uri($"/api/v1/deals/{deal.Id}/evaluate", UriKind.Relative), null);

        response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        var body = await response.Content.ReadAsStringAsync();

        body.Should().Contain(
            "compliance.jurisdiction_not_covered",
            "the refusal has to name its reason, or the dealer cannot tell it apart from a fault");
        body.Should().Contain("US-YY", "the message names the jurisdiction that is not covered");
    }

    private static async Task<DealDto> CreateDealAsync(HttpClient client, Guid? jurisdictionId)
    {
        var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/deals", UriKind.Relative),
            new CreateDealCommand
            {
                DealNumber = "IT-" + Guid.NewGuid().ToString("N")[..10],
                JurisdictionId = jurisdictionId,
            });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        return (await response.Content.ReadFromJsonAsync<DealDto>())!;
    }

    private static async Task<DealCalculationDto> CalculateAsync(
        HttpClient client, Guid dealId, string price)
    {
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/deals/{dealId}/calculate", UriKind.Relative),
            new CalculateDealRequest { VehiclePrice = price });

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<DealCalculationDto>())!;
    }

    /// <summary>
    /// A fictional jurisdiction with an approved rule set: a flat 5%, no trade credit.
    /// </summary>
    /// <remarks>
    /// Created as a separate jurisdiction rather than by approving the seeded
    /// Oklahoma row. Approving real-state placeholders in a test fixture would put
    /// invented rates one careless copy away from a production database.
    /// </remarks>
    private static async Task SeedFixtureJurisdictionAsync()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        // The regulatory jurisdiction is seeded here, not only in
        // SeedBlockingRuleAsync. It used to live there alone, so a test that
        // evaluated a deal without seeding a blocking rule depended on some
        // earlier test having created US-ZZ. On a developer machine with an
        // accumulated database that always held; against a fresh one it failed on
        // compliance_run's foreign key and surfaced as a 500.
        await using var jurisdiction = new NpgsqlCommand("""
            insert into sales.tax_jurisdiction (id, state_code, level, name)
            values (@id, 'ZZ', 'state', 'Test Jurisdiction (fixture only)')
            on conflict (id) do nothing;

            insert into compliance.regulatory_jurisdiction (code, level, name)
            values ('US-ZZ', 'state', 'Test Jurisdiction (fixture only)')
            on conflict (code) do nothing;
            """, connection);
        jurisdiction.Parameters.AddWithValue("id", FixtureJurisdiction);
        await jurisdiction.ExecuteNonQueryAsync();

        await using var ruleSet = new NpgsqlCommand("""
            insert into sales.rule_set
              (id, jurisdiction_id, version, effective_from, rules, source_citation,
               approved_at, approved_by)
            values
              (@id, @jurisdiction, 1, date '2020-01-01',
               @rules::jsonb,
               'Fixture only. Not a real jurisdiction, not a real rate.',
               now(), 'integration test fixture')
            on conflict (id) do nothing
            """, connection);
        ruleSet.Parameters.AddWithValue("id", FixtureRuleSet);
        ruleSet.Parameters.AddWithValue("jurisdiction", FixtureJurisdiction);
        ruleSet.Parameters.AddWithValue("rules", """
            {
              "stateRate": "5.0",
              "localRate": "0.0",
              "tradeInCreditPolicy": "none",
              "rebateTreatment": "taxable",
              "accessoriesTaxable": true
            }
            """);
        await ruleSet.ExecuteNonQueryAsync();
    }

    /// <summary>A fictional blocking rule: a financed deal needs a security agreement.</summary>
    private static async Task SeedBlockingRuleAsync()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            insert into compliance.regulatory_jurisdiction (code, level, name)
            values ('US-ZZ', 'state', 'Test Jurisdiction (fixture only)')
            on conflict (code) do nothing;

            insert into compliance.regulatory_authority (id, jurisdiction_code, code, name)
            values ('88888888-0000-4000-8000-000000000001', 'US-ZZ', 'TEST', 'Test Authority')
            on conflict (jurisdiction_code, code) do nothing;

            insert into compliance.regulatory_source (id, authority_id, source_type, title, citation)
            values ('88888888-0000-4000-8000-000000000002',
                    '88888888-0000-4000-8000-000000000001',
                    'statute', 'Fixture Statute', 'ZZ-FIXTURE-1')
            on conflict (authority_id, citation) do nothing;

            -- content_location is set because this fixture APPROVES a rule, and
            -- rule_pack_approval.sql refuses an approval that cites a source
            -- nobody archived. A fixture that models approval must model it
            -- properly, or it teaches the wrong shape.
            insert into compliance.regulatory_source_version
              (id, source_id, version, retrieved_at, retrieved_by, content_hash,
               content_location)
            values ('88888888-0000-4000-8000-000000000003',
                    '88888888-0000-4000-8000-000000000002',
                    1, now(), 'fixture', sha256('fixture'::bytea),
                    'fixture://archived/zz-fixture-1.txt')
            on conflict (id) do nothing;

            insert into compliance.regulatory_rule (id, rule_code, jurisdiction_code, category, title)
            values ('88888888-0000-4000-8000-000000000004', 'ZZ-TEST-FIN-001', 'US-ZZ',
                    'financing', 'Financed deals need a security agreement')
            on conflict (rule_code) do nothing;

            insert into compliance.regulatory_rule_version
              (id, rule_id, version, status, effective_from, source_version_id, trigger_event,
               definition, severity, approved_at, approved_by, approval_note)
            values ('88888888-0000-4000-8000-000000000005',
                    '88888888-0000-4000-8000-000000000004',
                    '1.0.0', 'active', date '2020-01-01',
                    '88888888-0000-4000-8000-000000000003',
                    'DEAL_SOLD',
                    @definition::jsonb,
                    'blocking', now(), 'integration test fixture',
                    'A financed deal needs a signed security agreement.')
            on conflict (id) do nothing;
            """, connection);

        command.Parameters.AddWithValue("definition", """
            {
              "conditions": {
                "all": [
                  { "field": "deal.isFinanced", "operator": "equals", "value": "true" }
                ]
              },
              "actions": [
                { "require": { "code": "SECURITY_AGREEMENT",
                               "name": "Retail instalment and security agreement" } }
              ]
            }
            """);

        await command.ExecuteNonQueryAsync();
    }

    private static async Task<Guid> JurisdictionIdAsync(string stateCode)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select id from sales.tax_jurisdiction where state_code = @state and level = 'state' limit 1",
            connection);
        command.Parameters.AddWithValue("state", stateCode);

        return (Guid)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<(decimal TotalSalePrice, DateTimeOffset? SupersededAt)> ReadSnapshotAsync(
        Guid dealId, int version)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(
            "select total_sale_price, superseded_at from sales.deal_calculation " +
            "where deal_id = @deal and version = @version",
            connection);
        command.Parameters.AddWithValue("deal", dealId);
        command.Parameters.AddWithValue("version", version);

        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue($"version {version} should exist");

        return (
            reader.GetDecimal(0),
            await reader.IsDBNullAsync(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static async Task<int> CountSnapshotsAsync(Guid dealId) =>
        await ScalarAsync(
            "select count(*)::int from sales.deal_calculation where deal_id = @id", dealId);

    private static async Task<int> CountComplianceResultsAsync(Guid runId) =>
        await ScalarAsync(
            "select count(*)::int from compliance.compliance_result where run_id = @id", runId);

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Security",
        "CA2100:Review SQL queries for security vulnerabilities",
        Justification = "Every statement is a literal authored in this file; the only value is " +
                        "bound as a parameter.")]
    private static async Task<int> ScalarAsync(string sql, Guid id)
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);

        return (int)(await command.ExecuteScalarAsync())!;
    }
}
