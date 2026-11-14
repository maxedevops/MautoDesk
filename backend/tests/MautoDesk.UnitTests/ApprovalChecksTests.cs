using FluentAssertions;
using MautoDesk.Compliance.RuleKeeper;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The preconditions for approving a regulatory rule.
/// </summary>
/// <remarks>
/// <para>
/// These are the only tests in the suite whose subject is a human process. Each
/// one corresponds to a mistake that is easy to make at the keyboard and
/// expensive to find later: approving a draft against a document nobody archived,
/// approving a rule whose own test cases disagree with it, approving a
/// business-day deadline into a jurisdiction with no calendar, or approving a
/// second reading that covers the same dates as the first.
/// </para>
/// <para>
/// None of them judges whether a reading of the law is correct. That is the
/// reviewer's work and no test can stand in for it.
/// </para>
/// </remarks>
public sealed class ApprovalChecksTests
{
    private const string FiresOnRetail = """
        {
          "conditions": {
            "all": [ { "field": "deal.type", "operator": "equals", "value": "retail" } ]
          },
          "actions": [
            { "require": { "code": "A_THING", "name": "A thing the rule requires" } }
          ]
        }
        """;

    private const string WithBusinessDayDeadline = """
        {
          "conditions": {
            "all": [ { "field": "deal.type", "operator": "equals", "value": "retail" } ]
          },
          "actions": [
            { "require": { "code": "A_THING", "name": "A thing the rule requires" } },
            { "deadline": { "code": "A_THING", "days": 2, "businessDays": true } }
          ]
        }
        """;

    private static DraftVersion Draft(
        string definitionJson = FiresOnRetail,
        bool approved = false,
        string? sourceLocation = "db/sources/us-ok/something.md") =>
        new()
        {
            Id = Guid.Parse("0b000000-0000-4000-8000-000000000001"),
            RuleId = Guid.Parse("0a000000-0000-4000-8000-000000000001"),
            RuleCode = "OK-PROBE-001",
            JurisdictionCode = "US-OK",
            Version = "0.1.0-draft",
            IsApproved = approved,
            TriggerEvent = "DEAL_SOLD",
            Severity = "blocking",
            DefinitionJson = definitionJson,
            SourceVersionId = Guid.Parse("05100000-0000-4000-8000-000000000002"),
            SourceLocation = sourceLocation,
            SourceHash = [1, 2, 3],
            SourceCitation = "Some archived document",
            ApprovalNote = "DRAFT. INTERPRETATION TO CONFIRM: something a lawyer must settle.",
        };

    private static ApprovalRequest Request(
        string version = "1.0.0",
        string reviewer = "Dale Cooper",
        DateOnly? from = null,
        DateOnly? to = null) =>
        new()
        {
            DraftVersionId = Guid.Parse("0b000000-0000-4000-8000-000000000001"),
            Version = version,
            ReviewedBy = reviewer,
            OperatorName = "an operator",
            EffectiveFrom = from ?? new DateOnly(2026, 11, 1),
            EffectiveTo = to,
            Note = new string('x', 80),
            NotePath = "/tmp/sign-off.txt",
            Commit = false,
        };

    private static readonly TestCase[] GoodCorpus =
    [
        new("A retail deal needs the thing", "block", """{"deal.type":"retail"}"""),
        new("The thing on file passes", "pass",
            """{"deal.type":"retail","_satisfied":["A_THING"]}"""),
        new("A wholesale deal is out of scope", "pass", """{"deal.type":"wholesale"}"""),
    ];

    private static IReadOnlyList<Check> Run(
        ApprovalRequest? request = null,
        DraftVersion? draft = null,
        IReadOnlyList<TestCase>? tests = null,
        IReadOnlyList<(DateOnly From, DateOnly? To, string Version)>? approved = null,
        IReadOnlySet<int>? holidayYears = null) =>
        ApprovalChecks.Run(
            request ?? Request(),
            draft ?? Draft(),
            tests ?? GoodCorpus,
            approved ?? [],
            holidayYears ?? new HashSet<int> { 2026, 2027 });

    private static Check Find(IReadOnlyList<Check> checks, string name) =>
        checks.Single(check => check.Name == name);

    [Fact]
    public void A_well_formed_approval_passes_every_check()
    {
        var checks = Run();

        checks.Should().NotBeEmpty();
        checks.Where(check => !check.Passed).Should().BeEmpty(
            "this is the case the other tests are variations on; if it fails, they prove nothing");
    }

    [Fact]
    public void An_already_approved_version_cannot_be_approved_again()
    {
        var checks = Run(draft: Draft(approved: true));

        Find(checks, "The draft is a draft").Passed.Should().BeFalse(
            "an approval is a new row, so approving an approved row is a sign somebody has the "
            + "wrong id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNVERIFIED")]
    [InlineData("TODO: ask legal")]
    [InlineData("placeholder")]
    [InlineData("AB")]
    public void An_approval_that_names_nobody_is_refused(string reviewer)
    {
        var checks = Run(request: Request(reviewer: reviewer));

        Find(checks, "The reviewer is named").Passed.Should().BeFalse(
            "rule_pack_approval.sql rejects these in CI, and an approval nobody owns is not an "
            + "approval");
    }

    [Fact]
    public void The_approved_version_may_not_reuse_the_drafts_own_version()
    {
        var checks = Run(request: Request(version: "0.1.0-draft"));

        Find(checks, "The approved version is a new version").Passed.Should().BeFalse();
    }

    [Fact]
    public void A_source_nobody_archived_cannot_be_approved_against()
    {
        var checks = Run(draft: Draft(sourceLocation: null));

        Find(checks, "The source is archived").Passed.Should().BeFalse(
            "approving against a URL is not the same as reading the law");
    }

    [Fact]
    public void A_rule_with_no_test_cases_is_refused()
    {
        var checks = Run(tests: []);

        Find(checks, "The test corpus proves something").Passed.Should().BeFalse();
    }

    [Fact]
    public void A_rule_with_only_a_firing_case_is_refused()
    {
        var checks = Run(tests:
        [
            new("A retail deal needs the thing", "block", """{"deal.type":"retail"}"""),
        ]);

        Find(checks, "The test corpus proves something").Passed.Should().BeFalse(
            "a rule with only a firing case has been shown to be a constant, not a rule");
    }

    [Fact]
    public void A_rule_with_only_a_non_firing_case_is_refused()
    {
        var checks = Run(tests:
        [
            new("A wholesale deal is out of scope", "pass", """{"deal.type":"wholesale"}"""),
        ]);

        Find(checks, "The test corpus proves something").Passed.Should().BeFalse(
            "this proves the rule is quiet, not that it works");
    }

    [Fact]
    public void A_case_that_disagrees_with_the_rule_blocks_the_approval()
    {
        var checks = Run(tests:
        [
            // The rule requires A_THING on a retail deal, so this case is simply
            // wrong about what the rule does. Approving it would activate a rule
            // whose documented behaviour is not its behaviour.
            new("A retail deal is fine as it is", "pass", """{"deal.type":"retail"}"""),
            new("A wholesale deal is out of scope", "pass", """{"deal.type":"wholesale"}"""),
            new("A retail deal needs the thing", "block", """{"deal.type":"retail"}"""),
        ]);

        var check = Find(checks, "Every stored case agrees");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("A retail deal is fine as it is");
    }

    [Fact]
    public void A_business_day_deadline_needs_a_calendar_for_every_year_it_governs()
    {
        var checks = Run(
            draft: Draft(WithBusinessDayDeadline),
            holidayYears: new HashSet<int>());

        Find(checks, "Business-day deadlines have a calendar").Passed.Should().BeFalse(
            "the engine refuses to compute a business-day deadline without a calendar, so this "
            + "would approve a rule that cannot run");
    }

    [Fact]
    public void A_business_day_deadline_spanning_an_uncovered_year_is_refused()
    {
        var checks = Run(
            request: Request(from: new DateOnly(2027, 1, 1), to: new DateOnly(2029, 1, 1)),
            draft: Draft(WithBusinessDayDeadline),
            holidayYears: new HashSet<int> { 2026, 2027 });

        var check = Find(checks, "Business-day deadlines have a calendar");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("2028");
    }

    [Fact]
    public void A_rule_without_a_business_day_deadline_needs_no_calendar()
    {
        var checks = Run(holidayYears: new HashSet<int>());

        Find(checks, "Business-day deadlines have a calendar").Passed.Should().BeTrue(
            "a calendar is only a precondition for the rules that count in business days");
    }

    [Fact]
    public void Two_approved_versions_may_not_cover_the_same_day()
    {
        var checks = Run(
            request: Request(from: new DateOnly(2026, 6, 1)),
            approved: [(new DateOnly(2024, 9, 1), null, "1.0.0")]);

        var check = Find(checks, "No approved version covers these dates");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("1.0.0");
    }

    [Fact]
    public void A_version_that_starts_where_the_last_one_ends_does_not_overlap()
    {
        var checks = Run(
            request: Request(from: new DateOnly(2027, 1, 1)),
            approved: [(new DateOnly(2024, 9, 1), new DateOnly(2027, 1, 1), "1.0.0")]);

        Find(checks, "No approved version covers these dates").Passed.Should().BeTrue(
            "the range is half-open, so effective_to is the first day the old version stops "
            + "governing");
    }

    [Fact]
    public void An_effective_period_that_ends_before_it_starts_is_refused()
    {
        var checks = Run(request: Request(
            from: new DateOnly(2027, 1, 1),
            to: new DateOnly(2026, 1, 1)));

        Find(checks, "The effective period is a period").Passed.Should().BeFalse();
    }

    [Fact]
    public void A_definition_the_evaluator_cannot_read_is_refused()
    {
        var checks = Run(draft: Draft("""{ "conditions": { "nonsense": true } }"""));

        Find(checks, "The definition parses").Passed.Should().BeFalse(
            "approving a rule the evaluator cannot read would activate something that never "
            + "fires, which looks exactly like compliance");
    }
}
