using System.Text.Json;
using FluentAssertions;
using MautoDesk.Compliance.Application;
using MautoDesk.Compliance.Domain;
using Npgsql;
using Xunit;

namespace MautoDesk.IntegrationTests;

/// <summary>
/// Runs every rule's own stored test cases through the real evaluator.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes a regulatory pack safe to author. Each rule version ships
/// with rows in <c>compliance.regulatory_rule_test</c> — a case that makes it
/// fire and a case that does not — and this executes all of them, for every
/// jurisdiction, on every build.
/// </para>
/// <para>
/// It deliberately covers <b>draft</b> rules as well as approved ones. A draft is
/// invisible to production evaluation, which is exactly why it would otherwise
/// rot unnoticed between being written and being approved. Testing it from the
/// day it is written means the reviewer is looking at something that demonstrably
/// does what it claims.
/// </para>
/// <para>
/// The CI job that runs this is what turns "a failed regulatory test blocks
/// deployment" from a line in the specification into a mechanism.
/// </para>
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class RegulatoryRuleTests
{
    /// <summary>
    /// Every stored case, evaluated against the rule it belongs to.
    /// </summary>
    /// <remarks>
    /// One test rather than a theory over database rows: xUnit resolves theory
    /// data before the fixture has run, and a data source that queries the
    /// database at discovery time makes the suite unable to run against an empty
    /// one. Collecting failures and reporting them together also means a reviewer
    /// sees every broken case at once, not the first.
    /// </remarks>
    [Fact]
    public async Task Every_rule_passes_its_own_test_cases()
    {
        var cases = await LoadCasesAsync();

        cases.Should().NotBeEmpty(
            "a regulatory pack with no test cases is a pack nobody has checked. If this is " +
            "failing on an empty database, the migrations have not been applied");

        var failures = new List<string>();

        foreach (var testCase in cases)
        {
            var rule = ToRuleVersion(testCase);

            if (rule is null)
            {
                failures.Add($"{testCase.RuleCode} {testCase.RuleVersion}: definition did not parse");
                continue;
            }

            var context = BuildContext(testCase.Input);

            // The calendar is supplied unconditionally so a deadline-bearing rule
            // can be tested before its jurisdiction's real holiday calendar is
            // loaded. The holidays here are fictional and affect only the computed
            // date, which these cases do not assert on.
            var run = RuleEvaluator.Evaluate(new EvaluationRequest
            {
                TriggerEvent = rule.TriggerEvent,
                JurisdictionCode = rule.JurisdictionCode,
                EvaluatedOn = rule.EffectiveFrom.AddDays(1),
                RulePackVersion = "rule-test-harness",
                Context = context,
                Rules = [ApproveForTesting(rule)],
                Calendar = BusinessDayCalendar.ForJurisdiction(
                    rule.JurisdictionCode, [new DateOnly(2026, 1, 1)]),
            });

            var actual = Worst(run.Results.Select(result => result.Status));

            if (actual != testCase.Expectation)
            {
                failures.Add(
                    $"{testCase.RuleCode} {testCase.RuleVersion} — \"{testCase.Name}\": " +
                    $"expected {testCase.Expectation}, got {actual}");
            }
        }

        failures.Should().BeEmpty(
            "every rule must do what its own test cases say it does. A regulatory rule that " +
            "behaves differently from its documented cases is worse than no rule, because it " +
            "looks like compliance");
    }

    /// <summary>
    /// Every rule version has both a firing case and a non-firing case.
    /// </summary>
    /// <remarks>
    /// A rule with only blocking cases has been shown to be a constant. Both
    /// halves are what demonstrates it discriminates, and the master prompt asks
    /// for positive and negative tests on every rule for exactly this reason.
    /// </remarks>
    [Fact]
    public async Task Every_rule_has_a_positive_and_a_negative_case()
    {
        var cases = await LoadCasesAsync();

        var incomplete = cases
            .GroupBy(testCase => $"{testCase.RuleCode} {testCase.RuleVersion}")
            .Where(group =>
                !group.Any(testCase => testCase.Expectation == ComplianceStatus.Pass)
                || group.All(testCase => testCase.Expectation == ComplianceStatus.Pass))
            .Select(group => group.Key)
            .ToList();

        incomplete.Should().BeEmpty(
            "a rule needs a case that fires and a case that does not; one without the other " +
            "proves only that it is a constant");
    }

    /// <summary>
    /// Nothing in the Oklahoma pack is approved.
    /// </summary>
    /// <remarks>
    /// The pack is seeded as drafts, and approval is a human act by a named
    /// reviewer against a retrieved source. This is the same assertion V0012 makes
    /// at migration time, repeated here because the failure mode it guards — a
    /// stray update approving a draft — would otherwise turn unreviewed research
    /// into rules that block real deals.
    /// </remarks>
    [Fact]
    public async Task The_oklahoma_pack_is_entirely_unapproved()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select count(*)::int
              from compliance.regulatory_rule_version v
              join compliance.regulatory_rule r on r.id = v.rule_id
             where r.jurisdiction_code = 'US-OK'
               and (v.approved_at is not null or v.status <> 'draft')
            """, connection);

        var approved = (int)(await command.ExecuteScalarAsync())!;

        approved.Should().Be(
            0,
            "the Oklahoma rules are researched drafts, not reviewed law. Approving one is a " +
            "decision for dealer-compliance counsel, recorded with their name — not something " +
            "a migration or a developer does");
    }

    /// <summary>
    /// A source version with no archived document admits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The Oklahoma sources are now archived under <c>db/sources/us-ok/</c> and
    /// carry real hashes, so this currently has nothing to catch — which is the
    /// intended state, not a reason to delete it. It is the guard for the next
    /// source somebody cites from a URL they did not archive.
    /// </para>
    /// <para>
    /// A hash that is real but covers nothing verifiable is a subtler way to be
    /// misleading than an obvious placeholder, so an unarchived source must say so
    /// in its change summary.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Placeholder_provenance_is_labelled_as_placeholder()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select s.citation, v.change_summary, v.content_location
              from compliance.regulatory_source_version v
              join compliance.regulatory_source s on s.id = v.source_id
              join compliance.regulatory_authority a on a.id = s.authority_id
             where a.jurisdiction_code = 'US-OK'
            """, connection);

        var unlabelled = new List<string>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var citation = reader.GetString(0);
            var summary = await reader.IsDBNullAsync(1) ? string.Empty : reader.GetString(1);
            var archived = !await reader.IsDBNullAsync(2);

            // Either the document is archived, or the row admits it is not.
            if (!archived && !summary.Contains("PLACEHOLDER", StringComparison.Ordinal))
            {
                unlabelled.Add(citation);
            }
        }

        unlabelled.Should().BeEmpty(
            "a source version with no archived document must say its hash is a placeholder; " +
            "otherwise it reads as evidence that a rule was written against a specific text");
    }

    /// <summary>
    /// Every archived source still hashes to what the database recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what turns "this rule was written against this text" from an
    /// assertion into something checkable. If somebody edits an archived source
    /// after a rule was drafted against it, the hash stops matching and this goes
    /// red — which is the whole reason the column exists.
    /// </para>
    /// <para>
    /// Rows whose <c>content_location</c> is null are skipped: they are sources
    /// located but not archived, and <c>Placeholder_provenance_is_labelled_as_placeholder</c>
    /// is what holds those to account.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task Every_archived_source_hashes_to_what_was_recorded()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select s.citation, v.content_location, v.content_hash
              from compliance.regulatory_source_version v
              join compliance.regulatory_source s on s.id = v.source_id
             where v.content_location is not null
               and v.content_location not like 'fixture://%'
            """, connection);

        var mismatches = new List<string>();
        var checkedCount = 0;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            var citation = reader.GetString(0);
            var location = reader.GetString(1);
            var recorded = (byte[])reader[2];

            var path = Path.Combine(RepositoryRoot(), location.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                mismatches.Add($"{citation}: archived at {location}, but no such file");
                continue;
            }

            checkedCount++;

            var actual = System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(path));

            if (!actual.SequenceEqual(recorded))
            {
                mismatches.Add(
                    $"{citation}: {location} hashes to {Convert.ToHexString(actual)}, " +
                    $"recorded as {Convert.ToHexString(recorded)}");
            }
        }

        mismatches.Should().BeEmpty(
            "an archived source that no longer hashes to what was recorded means a rule was " +
            "drafted against text that has since changed — which is exactly what the hash is " +
            "for");

        checkedCount.Should().BeGreaterThan(
            0, "at least one source should be archived, or this test proves nothing");
    }

    /// <summary>Finds the repository root, from a worktree or an ordinary checkout.</summary>
    /// <remarks>
    /// A worktree's <c>.git</c> is a file rather than a directory; checking only
    /// for a directory walks past it and finds the main checkout instead.
    /// </remarks>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null
            && !Directory.Exists(Path.Combine(directory.FullName, ".git"))
            && !File.Exists(Path.Combine(directory.FullName, ".git")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    /// <summary>Turns a stored case's flat fact map into a context.</summary>
    /// <remarks>
    /// <c>_satisfied</c> is the one reserved key: an array of requirement codes the
    /// case treats as already met. Everything else is a fact path.
    /// </remarks>
    private static RegulatoryContext BuildContext(string inputJson)
    {
        using var document = JsonDocument.Parse(inputJson);
        var builder = RegulatoryContext.Create();

        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name == "_satisfied")
            {
                foreach (var code in property.Value.EnumerateArray())
                {
                    builder.Satisfying(code.GetString()!);
                }

                continue;
            }

            builder.With(property.Name, property.Value.ToString());
        }

        return builder.Build();
    }

    /// <summary>
    /// The worst verdict among a rule's results.
    /// </summary>
    /// <remarks>
    /// A rule with several requirements produces several results, and the case is
    /// asserting on the outcome for the deal — so the most severe one wins, the
    /// same way the readiness light does.
    /// </remarks>
    private static ComplianceStatus Worst(IEnumerable<ComplianceStatus> statuses)
    {
        var worst = ComplianceStatus.Pass;

        foreach (var status in statuses)
        {
            if (Rank(status) > Rank(worst))
            {
                worst = status;
            }
        }

        return worst;

        static int Rank(ComplianceStatus status) => status switch
        {
            ComplianceStatus.Block => 3,
            ComplianceStatus.Review => 2,
            ComplianceStatus.Warning => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Marks a rule approved for the duration of the harness.
    /// </summary>
    /// <remarks>
    /// The evaluator refuses unapproved rules, which is the correct production
    /// behaviour and would make every draft's tests vacuously pass. Approving the
    /// in-memory copy tests the rule as it would behave once a reviewer approves
    /// it — the database row is untouched.
    /// </remarks>
    private static RegulatoryRuleVersion ApproveForTesting(RegulatoryRuleVersion rule) => rule with
    {
        IsApproved = true,
        Status = RuleStatus.Active,
    };

    private static RegulatoryRuleVersion? ToRuleVersion(RuleTestCase testCase)
    {
        var definition = RuleDefinitionSerializer.Parse(testCase.RuleCode, testCase.DefinitionJson);

        if (definition.IsFailure)
        {
            return null;
        }

        return new RegulatoryRuleVersion
        {
            Id = testCase.RuleVersionId,
            RuleCode = testCase.RuleCode,
            Version = testCase.RuleVersion,
            JurisdictionCode = testCase.JurisdictionCode,
            TriggerEvent = testCase.TriggerEvent,
            Severity = Enum.Parse<RuleSeverity>(testCase.Severity, ignoreCase: true),
            Status = RuleStatus.Draft,
            EffectiveFrom = testCase.EffectiveFrom,
            IsApproved = false,
            SourceVersionId = testCase.SourceVersionId,
            Conditions = definition.Value!.Conditions,
            Actions = definition.Value.Actions,
        };
    }

    private static async Task<List<RuleTestCase>> LoadCasesAsync()
    {
        await using var connection = new NpgsqlConnection(ApiFixture.AdminConnectionString);
        await connection.OpenAsync();

        await using var command = new NpgsqlCommand("""
            select t.name, t.expectation, t.input::text,
                   v.id, r.rule_code, v.version, r.jurisdiction_code, v.trigger_event,
                   v.severity, v.effective_from, v.source_version_id, v.definition::text
              from compliance.regulatory_rule_test t
              join compliance.regulatory_rule_version v on v.id = t.rule_version_id
              join compliance.regulatory_rule r on r.id = v.rule_id
             order by r.rule_code, v.version, t.name
            """, connection);

        var cases = new List<RuleTestCase>();

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            cases.Add(new RuleTestCase(
                reader.GetString(0),
                Enum.Parse<ComplianceStatus>(reader.GetString(1), ignoreCase: true),
                reader.GetString(2),
                reader.GetGuid(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                DateOnly.FromDateTime(reader.GetDateTime(9)),
                reader.GetGuid(10),
                reader.GetString(11)));
        }

        return cases;
    }

    private sealed record RuleTestCase(
        string Name,
        ComplianceStatus Expectation,
        string Input,
        Guid RuleVersionId,
        string RuleCode,
        string RuleVersion,
        string JurisdictionCode,
        string TriggerEvent,
        string Severity,
        DateOnly EffectiveFrom,
        Guid SourceVersionId,
        string DefinitionJson);
}
