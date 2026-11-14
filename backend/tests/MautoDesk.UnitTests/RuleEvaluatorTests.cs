using FluentAssertions;
using MautoDesk.Compliance.Domain;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The regulatory evaluator.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every rule here is fictional.</b> They are named <c>ZZ-TEST-*</c> against a
/// jurisdiction called <c>ZZ-TEST</c>, because a fixture that looks like a real
/// statute is a fixture somebody eventually cites. Real rules arrive from a
/// primary source with an archived source version and a named approver; none of
/// that is in this file.
/// </para>
/// <para>
/// What is tested is the machinery: which rules are selected, what firing means,
/// what satisfying a requirement means, and whether the same facts always produce
/// the same answer.
/// </para>
/// </remarks>
public sealed class RuleEvaluatorTests
{
    private const string Jurisdiction = "ZZ-TEST";
    private const string Trigger = "DEAL_SOLD";
    private const string Pack = "ZZ-TEST-0.1.0";

    private static readonly DateOnly Today = new(2026, 6, 15);
    private static readonly Guid SourceId = Guid.Parse("55555555-5555-4555-8555-000000000005");

    // ---------------------------------------------------------------------
    // Selection. A rule that should not have been considered is as dangerous
    // as one that was considered wrongly.
    // ---------------------------------------------------------------------

    /// <summary>An unapproved rule is invisible, whatever its dates say.</summary>
    /// <remarks>
    /// The gate that keeps a half-written draft from blocking a real deal — or,
    /// worse, from silently passing one.
    /// </remarks>
    [Fact]
    public void An_unapproved_rule_is_never_evaluated()
    {
        var rule = BlockingRequirement() with { IsApproved = false };

        var run = Evaluate(Facts(), rule);

        run.Results.Should().BeEmpty();
        run.OverallStatus.Should().Be(DealReadiness.Green);
    }

    /// <summary>A draft is invisible even when it is marked approved by mistake.</summary>
    [Theory]
    [InlineData(RuleStatus.Draft)]
    [InlineData(RuleStatus.Review)]
    [InlineData(RuleStatus.Retired)]
    public void Only_live_lifecycle_states_are_evaluated(RuleStatus status)
    {
        var rule = BlockingRequirement() with { Status = status };

        Evaluate(Facts(), rule).Results.Should().BeEmpty();
    }

    /// <summary>A rule that was not yet in force on the deal date does not apply.</summary>
    /// <remarks>
    /// Rules are resolved as of the day the deal was written. Without this, a rule
    /// introduced in July would retroactively make a compliant January deal
    /// non-compliant.
    /// </remarks>
    [Fact]
    public void A_rule_effective_after_the_deal_date_does_not_apply()
    {
        var rule = BlockingRequirement() with { EffectiveFrom = new DateOnly(2026, 7, 1) };

        Evaluate(Facts(), rule).Results.Should().BeEmpty();
    }

    /// <summary>A rule that had expired by the deal date does not apply either.</summary>
    [Fact]
    public void A_rule_that_had_already_ended_does_not_apply()
    {
        var rule = BlockingRequirement() with { EffectiveTo = new DateOnly(2026, 6, 1) };

        Evaluate(Facts(), rule).Results.Should().BeEmpty();
    }

    /// <summary>Another jurisdiction's rules are not applied to this deal.</summary>
    [Fact]
    public void Rules_from_another_jurisdiction_are_ignored()
    {
        var rule = BlockingRequirement() with { JurisdictionCode = "ZZ-OTHER" };

        Evaluate(Facts(), rule).Results.Should().BeEmpty();
    }

    /// <summary>A rule for a different lifecycle event is not applied.</summary>
    [Fact]
    public void Rules_for_another_trigger_are_ignored()
    {
        var rule = BlockingRequirement() with { TriggerEvent = "DEAL_FUNDED" };

        Evaluate(Facts(), rule).Results.Should().BeEmpty();
    }

    /// <summary>
    /// Two approved versions covering one day is a loud failure, not a coin toss.
    /// </summary>
    /// <remarks>
    /// The exclusion constraint on <c>compliance.regulatory_rule_version</c> is
    /// supposed to make this impossible. If it is ever dropped, silently evaluating
    /// both would let two identical deals be judged differently — so the engine
    /// refuses rather than choosing.
    /// </remarks>
    [Fact]
    public void Two_approved_versions_covering_the_same_day_are_refused()
    {
        var first = BlockingRequirement();
        var second = BlockingRequirement() with { Id = Guid.NewGuid(), Version = "2.0.0" };

        var act = () => Evaluate(Facts(), first, second);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*cannot choose between them*");
    }

    // ---------------------------------------------------------------------
    // Firing, satisfying, and the readiness light.
    // ---------------------------------------------------------------------

    /// <summary>A rule whose conditions do not hold passes, and says why.</summary>
    /// <remarks>
    /// Recorded rather than omitted. "We considered this rule and it did not apply"
    /// is a materially different claim from "we never looked", and an auditor is
    /// entitled to the difference.
    /// </remarks>
    [Fact]
    public void A_rule_that_does_not_apply_is_recorded_as_a_pass()
    {
        var facts = Facts(financed: false);

        var run = Evaluate(facts, BlockingRequirement());

        run.Results.Should().ContainSingle();
        run.Results[0].Status.Should().Be(ComplianceStatus.Pass);
        run.Results[0].Message.Should().Contain("Does not apply");
        run.OverallStatus.Should().Be(DealReadiness.Green);
    }

    /// <summary>A blocking requirement that is not on file turns the deal red.</summary>
    [Fact]
    public void An_unsatisfied_blocking_requirement_blocks_the_deal()
    {
        var run = Evaluate(Facts(), BlockingRequirement());

        run.Results.Should().ContainSingle();
        run.Results[0].Status.Should().Be(ComplianceStatus.Block);
        run.Results[0].RequirementCode.Should().Be("SECURITY_AGREEMENT");
        run.OverallStatus.Should().Be(DealReadiness.Red);
        run.BlockingCount.Should().Be(1);
        run.Blockers.Should().ContainSingle();
    }

    /// <summary>The same rule passes once the requirement is met.</summary>
    /// <remarks>
    /// The negative half of the pair above. A rule that only ever blocks has not
    /// been shown to be a rule — it has been shown to be a constant.
    /// </remarks>
    [Fact]
    public void A_satisfied_requirement_passes()
    {
        var facts = Facts(satisfied: "SECURITY_AGREEMENT");

        var run = Evaluate(facts, BlockingRequirement());

        run.Results[0].Status.Should().Be(ComplianceStatus.Pass);
        run.Results[0].Message.Should().Contain("on file");
        run.OverallStatus.Should().Be(DealReadiness.Green);
    }

    /// <summary>A warning is yellow, not red — the deal proceeds.</summary>
    [Fact]
    public void A_warning_severity_rule_does_not_block()
    {
        var rule = BlockingRequirement() with { Severity = RuleSeverity.Warning };

        var run = Evaluate(Facts(), rule);

        run.Results[0].Status.Should().Be(ComplianceStatus.Warning);
        run.OverallStatus.Should().Be(DealReadiness.Yellow);
        run.BlockingCount.Should().Be(0);
        run.WarningCount.Should().Be(1);
    }

    /// <summary>A review-severity rule puts the deal in front of a human.</summary>
    [Fact]
    public void A_review_severity_rule_is_yellow()
    {
        var rule = BlockingRequirement() with { Severity = RuleSeverity.Review };

        var run = Evaluate(Facts(), rule);

        run.Results[0].Status.Should().Be(ComplianceStatus.Review);
        run.OverallStatus.Should().Be(DealReadiness.Yellow);
    }

    /// <summary>
    /// A prohibition has nothing to satisfy: firing is the finding.
    /// </summary>
    /// <remarks>
    /// The shape used for "this fee is above its cap" or "this advertised price
    /// includes a conditional incentive". The fix is to restructure the deal, not
    /// to produce a document.
    /// </remarks>
    [Fact]
    public void A_prohibition_blocks_on_firing_alone()
    {
        var rule = Rule(
            "ZZ-TEST-FEE-001",
            new Comparison("deal.docFee", ConditionOperator.GreaterThan, "400"),
            RuleSeverity.Blocking,
            new ProhibitAction("The documentary fee is above the amount permitted here."));

        var facts = RegulatoryContext.Create().With("deal.docFee", 500m).Build();

        var run = Evaluate(facts, rule);

        run.Results[0].Status.Should().Be(ComplianceStatus.Block);
        run.Results[0].RequirementCode.Should().BeNull("there is nothing to produce");
        run.Results[0].Message.Should().Contain("above the amount permitted");
    }

    /// <summary>The same prohibition is silent when the deal is within the limit.</summary>
    [Fact]
    public void A_prohibition_that_does_not_fire_passes()
    {
        var rule = Rule(
            "ZZ-TEST-FEE-001",
            new Comparison("deal.docFee", ConditionOperator.GreaterThan, "400"),
            RuleSeverity.Blocking,
            new ProhibitAction("The documentary fee is above the amount permitted here."));

        var facts = RegulatoryContext.Create().With("deal.docFee", 400m).Build();

        Evaluate(facts, rule).Results[0].Status.Should().Be(ComplianceStatus.Pass);
    }

    /// <summary>One blocking rule among many passes is still red.</summary>
    [Fact]
    public void One_blocker_among_passes_is_still_red()
    {
        var passing = Rule(
            "ZZ-TEST-AAA-001",
            new Comparison("deal.isFinanced", ConditionOperator.Equals, "false"),
            RuleSeverity.Blocking,
            new RequireAction("NEVER_NEEDED", "Something else"));

        var run = Evaluate(Facts(), BlockingRequirement(), passing);

        run.Results.Should().HaveCount(2);
        run.OverallStatus.Should().Be(DealReadiness.Red);
    }

    // ---------------------------------------------------------------------
    // The condition vocabulary.
    // ---------------------------------------------------------------------

    /// <summary>A fact nobody supplied does not satisfy a comparison.</summary>
    /// <remarks>
    /// Unknown is not false. Treating it as a match is how a rule silently stops
    /// firing the day an upstream field is renamed — and nothing would fail.
    /// </remarks>
    [Fact]
    public void A_missing_fact_never_matches_a_comparison()
    {
        var rule = Rule(
            "ZZ-TEST-MISSING-001",
            new Comparison("title.status", ConditionOperator.Equals, "held"),
            RuleSeverity.Blocking,
            new RequireAction("X", "X"));

        var run = Evaluate(RegulatoryContext.Create().Build(), rule);

        run.Results[0].Status.Should().Be(ComplianceStatus.Pass, "the fact was never supplied");
    }

    /// <summary><c>Exists</c> and <c>NotExists</c> are how a rule asks about presence.</summary>
    [Fact]
    public void Existence_operators_test_presence_rather_than_value()
    {
        var exists = new Comparison("title.number", ConditionOperator.Exists);
        var notExists = new Comparison("title.number", ConditionOperator.NotExists);

        var withTitle = RegulatoryContext.Create().With("title.number", "T-1").Build();
        var without = RegulatoryContext.Create().Build();

        var trace = new List<string>();

        exists.Matches(withTitle, trace).Should().BeTrue();
        exists.Matches(without, trace).Should().BeFalse();
        notExists.Matches(without, trace).Should().BeTrue();
        notExists.Matches(withTitle, trace).Should().BeFalse();
    }

    /// <summary>Equality is numeric when both sides are numbers, so 10 and 10.00 agree.</summary>
    [Fact]
    public void Numeric_equality_ignores_trailing_zeroes()
    {
        var comparison = new Comparison("deal.fee", ConditionOperator.Equals, "10.00");
        var facts = RegulatoryContext.Create().With("deal.fee", 10m).Build();

        comparison.Matches(facts, []).Should().BeTrue();
    }

    /// <summary>String equality ignores case, because a status is not a password.</summary>
    [Fact]
    public void String_equality_is_case_insensitive()
    {
        var comparison = new Comparison("title.type", ConditionOperator.Equals, "ELECTRONIC");
        var facts = RegulatoryContext.Create().With("title.type", "electronic").Build();

        comparison.Matches(facts, []).Should().BeTrue();
    }

    /// <summary><c>In</c> and <c>NotIn</c> test membership.</summary>
    [Theory]
    [InlineData("held", true)]
    [InlineData("verified", false)]
    public void Membership_operators_work(string status, bool expected)
    {
        var inList = new Comparison(
            "title.status", ConditionOperator.In, Values: ["held", "pending", "unknown"]);

        var facts = RegulatoryContext.Create().With("title.status", status).Build();

        inList.Matches(facts, []).Should().Be(expected);

        var notIn = new Comparison(
            "title.status", ConditionOperator.NotIn, Values: ["held", "pending", "unknown"]);

        notIn.Matches(facts, []).Should().Be(!expected);
    }

    /// <summary>Numeric comparisons are decimal, not floating point.</summary>
    /// <remarks>
    /// These decide whether a deal is blocked, and several compare money. A
    /// comparison that answers differently because 0.1 cannot be represented is not
    /// something to discover on a contract.
    /// </remarks>
    [Theory]
    [InlineData(ConditionOperator.GreaterThan, "400", "400.01", true)]
    [InlineData(ConditionOperator.GreaterThan, "400", "400.00", false)]
    [InlineData(ConditionOperator.GreaterThanOrEqual, "400", "400.00", true)]
    [InlineData(ConditionOperator.LessThan, "0.3", "0.1", true)]
    [InlineData(ConditionOperator.LessThanOrEqual, "0.3", "0.30", true)]
    public void Numeric_comparisons_are_exact(
        ConditionOperator op, string threshold, string actual, bool expected)
    {
        var comparison = new Comparison("deal.amount", op, threshold);
        var facts = RegulatoryContext.Create().With("deal.amount", actual).Build();

        comparison.Matches(facts, []).Should().Be(expected);
    }

    /// <summary>An empty <c>all</c> matches, which is how a rule says "always applies".</summary>
    [Fact]
    public void An_empty_all_of_always_matches() =>
        new AllOf([]).Matches(Facts(), []).Should().BeTrue();

    /// <summary>An empty <c>any</c> does not match — there is nothing to satisfy it.</summary>
    [Fact]
    public void An_empty_any_of_never_matches() =>
        new AnyOf([]).Matches(Facts(), []).Should().BeFalse();

    /// <summary><c>all</c>, <c>any</c> and <c>not</c> compose.</summary>
    [Fact]
    public void Condition_groups_compose()
    {
        var condition = new AllOf(
        [
            new Comparison("deal.isFinanced", ConditionOperator.Equals, "true"),
            new AnyOf(
            [
                new Comparison("title.status", ConditionOperator.Equals, "held"),
                new Comparison("title.status", ConditionOperator.Equals, "pending"),
            ]),
            new Not(new Comparison("deal.isWholesale", ConditionOperator.Equals, "true")),
        ]);

        var matching = RegulatoryContext.Create()
            .With("deal.isFinanced", true)
            .With("title.status", "pending")
            .With("deal.isWholesale", false)
            .Build();

        condition.Matches(matching, []).Should().BeTrue();

        var wholesale = RegulatoryContext.Create()
            .With("deal.isFinanced", true)
            .With("title.status", "pending")
            .With("deal.isWholesale", true)
            .Build();

        condition.Matches(wholesale, []).Should().BeFalse();
    }

    /// <summary>The trace records what was compared, for the evaluation detail.</summary>
    [Fact]
    public void The_evaluation_detail_records_what_was_compared()
    {
        var run = Evaluate(Facts(), BlockingRequirement());

        run.Results[0].EvaluationDetail.Should().NotBeNullOrEmpty();
        run.Results[0].EvaluationDetail!.Should()
            .Contain(line => line.Contains("deal.isFinanced"));
    }

    // ---------------------------------------------------------------------
    // Deadlines.
    // ---------------------------------------------------------------------

    /// <summary>A deadline is computed from the rule, not from a constant.</summary>
    /// <remarks>
    /// Monday 15 June 2026 plus two business days is Wednesday the 17th.
    /// </remarks>
    [Fact]
    public void A_deadline_action_produces_a_date_from_the_rule()
    {
        var rule = Rule(
            "ZZ-TEST-DEADLINE-001",
            new AllOf([]),
            RuleSeverity.Blocking,
            new RequireAction("PRE_REGISTRATION", "Pre-registration submission"),
            new DeadlineAction("PRE_REGISTRATION", 2));

        var run = Evaluate(Facts(), calendar: Calendar(), rules: rule);

        run.Results[0].Deadline.Should().Be(new DateOnly(2026, 6, 17));
    }

    /// <summary>
    /// A deadline without a calendar is refused rather than guessed.
    /// </summary>
    /// <remarks>
    /// A business-day deadline computed against no holiday calendar is wrong on
    /// every holiday, and wrong quietly. A dealer would act on it.
    /// </remarks>
    [Fact]
    public void A_deadline_without_a_calendar_throws()
    {
        var rule = Rule(
            "ZZ-TEST-DEADLINE-001",
            new AllOf([]),
            RuleSeverity.Blocking,
            new RequireAction("PRE_REGISTRATION", "Pre-registration"),
            new DeadlineAction("PRE_REGISTRATION", 2));

        var act = () => Evaluate(Facts(), rule);

        act.Should().Throw<InvalidOperationException>().WithMessage("*calendar*");
    }

    /// <summary>A satisfied requirement carries no deadline — it is already done.</summary>
    [Fact]
    public void A_satisfied_requirement_has_no_outstanding_deadline()
    {
        var rule = Rule(
            "ZZ-TEST-DEADLINE-001",
            new AllOf([]),
            RuleSeverity.Blocking,
            new RequireAction("PRE_REGISTRATION", "Pre-registration"),
            new DeadlineAction("PRE_REGISTRATION", 2));

        var facts = Facts(satisfied: "PRE_REGISTRATION");

        var run = Evaluate(facts, calendar: Calendar(), rules: rule);

        run.Results[0].Status.Should().Be(ComplianceStatus.Pass);
        run.Results[0].Deadline.Should().BeNull();
    }

    // ---------------------------------------------------------------------
    // Reproducibility.
    // ---------------------------------------------------------------------

    /// <summary>The same facts and rules always produce the same hashes.</summary>
    /// <remarks>
    /// This is what makes "which rules applied to this deal on 19 August 2026" a
    /// question with a checkable answer.
    /// </remarks>
    [Fact]
    public void The_same_evaluation_reproduces_exactly()
    {
        var facts = Facts();

        var first = Evaluate(facts, BlockingRequirement());
        var second = Evaluate(facts, BlockingRequirement());

        second.InputHash.Should().Equal(first.InputHash);
        second.ResultHash.Should().Equal(first.ResultHash);
    }

    /// <summary>Different facts hash differently.</summary>
    [Fact]
    public void A_changed_fact_changes_the_input_hash()
    {
        var financed = Evaluate(Facts(), BlockingRequirement());
        var cash = Evaluate(Facts(financed: false), BlockingRequirement());

        cash.InputHash.Should().NotEqual(financed.InputHash);
    }

    /// <summary>
    /// Rewording a message does not change the result hash.
    /// </summary>
    /// <remarks>
    /// The hash covers what happens to the deal — rule, version, status,
    /// requirement, deadline — not the prose. Otherwise improving the wording of a
    /// message would look, to an auditor, exactly like changing a compliance
    /// outcome.
    /// </remarks>
    [Fact]
    public void Rewording_a_message_does_not_change_the_result_hash()
    {
        var original = BlockingRequirement();
        var reworded = original with { Message = "Completely different wording here." };

        var first = Evaluate(Facts(), original);
        var second = Evaluate(Facts(), reworded);

        second.ResultHash.Should().Equal(first.ResultHash);
    }

    /// <summary>Results come back in a stable order regardless of how rules were loaded.</summary>
    /// <remarks>
    /// Without this the result hash would depend on however the database happened
    /// to return the rows, and two identical evaluations would disagree about their
    /// own reproducibility.
    /// </remarks>
    [Fact]
    public void Results_are_ordered_independently_of_the_input_order()
    {
        var a = BlockingRequirement();
        var b = Rule(
            "ZZ-TEST-AAA-001",
            new AllOf([]),
            RuleSeverity.Warning,
            new RequireAction("OTHER", "Something else"));

        var forwards = Evaluate(Facts(), a, b);
        var backwards = Evaluate(Facts(), b, a);

        forwards.Results.Select(result => result.RuleCode)
            .Should().Equal(backwards.Results.Select(result => result.RuleCode));

        backwards.ResultHash.Should().Equal(forwards.ResultHash);
    }

    /// <summary>The run records the engine and pack versions that produced it.</summary>
    [Fact]
    public void The_run_pins_its_engine_and_pack_version()
    {
        var run = Evaluate(Facts(), BlockingRequirement());

        run.EngineVersion.Should().Be(RuleEvaluator.Version);
        run.RulePackVersion.Should().Be(Pack);
        run.EvaluatedOn.Should().Be(Today);
        run.JurisdictionCode.Should().Be(Jurisdiction);
    }

    /// <summary>Every verdict traces to a rule version and an archived source.</summary>
    /// <remarks>
    /// A compliance result with no source is somebody's recollection. The AI
    /// explanation layer is required to quote one of these rows, which only works
    /// if the citation is carried all the way through.
    /// </remarks>
    [Fact]
    public void Every_result_carries_its_rule_version_and_source()
    {
        var run = Evaluate(Facts(), BlockingRequirement());

        run.Results.Should().OnlyContain(result =>
            result.RuleVersionId != Guid.Empty && result.SourceVersionId != Guid.Empty);
    }

    // ---------------------------------------------------------------------
    // Fixtures. Fictional throughout.
    // ---------------------------------------------------------------------

    private static ComplianceRun Evaluate(
        RegulatoryContext facts,
        params RegulatoryRuleVersion[] rules) =>
        Evaluate(facts, null, rules);

    private static ComplianceRun Evaluate(
        RegulatoryContext facts,
        BusinessDayCalendar? calendar,
        params RegulatoryRuleVersion[] rules) =>
        RuleEvaluator.Evaluate(new EvaluationRequest
        {
            TriggerEvent = Trigger,
            JurisdictionCode = Jurisdiction,
            EvaluatedOn = Today,
            RulePackVersion = Pack,
            Context = facts,
            Rules = rules,
            Calendar = calendar,
        });

    private static RegulatoryContext Facts(bool financed = true, string? satisfied = null)
    {
        var builder = RegulatoryContext.Create()
            .With("deal.isFinanced", financed)
            .With("deal.isWholesale", false)
            .With("deal.type", "retail");

        if (satisfied is not null)
        {
            builder.Satisfying(satisfied);
        }

        return builder.Build();
    }

    /// <summary>A financed deal needs a security agreement. Fictional.</summary>
    private static RegulatoryRuleVersion BlockingRequirement() => Rule(
        "ZZ-TEST-FIN-001",
        new Comparison("deal.isFinanced", ConditionOperator.Equals, "true"),
        RuleSeverity.Blocking,
        new RequireAction("SECURITY_AGREEMENT", "Retail instalment and security agreement"));

    private static RegulatoryRuleVersion Rule(
        string code,
        ConditionNode conditions,
        RuleSeverity severity,
        params RuleAction[] actions) => new()
        {
            Id = Guid.Parse("44444444-4444-4444-8444-000000000004"),
            RuleCode = code,
            Version = "1.0.0",
            JurisdictionCode = Jurisdiction,
            TriggerEvent = Trigger,
            Severity = severity,
            Status = RuleStatus.Active,
            EffectiveFrom = new DateOnly(2026, 1, 1),
            IsApproved = true,
            SourceVersionId = SourceId,
            Conditions = conditions,
            Actions = actions,
            Message = "Fixture rule.",
        };

    /// <summary>A calendar with one fictional holiday, so the tests exercise a real skip.</summary>
    private static BusinessDayCalendar Calendar() =>
        BusinessDayCalendar.ForJurisdiction(Jurisdiction, [new DateOnly(2026, 7, 3)]);
}
