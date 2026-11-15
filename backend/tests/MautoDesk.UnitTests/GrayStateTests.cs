using FluentAssertions;
using MautoDesk.Compliance.Domain;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The GRAY state: a rule that could not be decided is not a rule that passed.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist because of a structural defect, not a bug in a particular
/// rule. Every operator except <c>exists</c> and <c>not_exists</c> used to return
/// false when a fact was absent, so a condition over a field nobody captured made
/// the rule <em>not fire</em>, which produced no finding, which came back GREEN.
/// A deal missing half its data was indistinguishable from a lawful one.
/// </para>
/// <para>
/// The fix is three-valued conditions, and the thing worth testing is not the
/// truth table in the abstract but the two directions it can go wrong in: an
/// absent fact must not be mistaken for a pass, and a fact that is genuinely
/// irrelevant must not drag an otherwise decided deal into GRAY.
/// </para>
/// </remarks>
public sealed class GrayStateTests
{
    private static readonly Guid RuleId = Guid.Parse("0b000000-0000-4000-8000-00000000aa01");
    private static readonly Guid SourceId = Guid.Parse("05100000-0000-4000-8000-00000000aa01");

    // ---------------------------------------------------------------- the trap --

    /// <summary>The defect this whole mechanism exists for.</summary>
    [Fact]
    public void A_rule_over_a_fact_nobody_captured_does_not_read_as_compliant()
    {
        // The rule blocks a deal whose odometer exceeds 100,000 miles. The deal
        // does not say what the odometer reads.
        var rule = Rule(new Comparison(
            "deal.odometer", ConditionOperator.GreaterThan, "100000"));

        var run = Evaluate(rule, RegulatoryContext.Create().With("deal.type", "retail").Build());

        run.OverallStatus.Should().Be(
            DealReadiness.Gray,
            "the engine has no idea whether this rule applies, and saying GREEN would be "
            + "asserting that it does not");
        run.Results.Should().ContainSingle()
            .Which.Status.Should().Be(ComplianceStatus.Unknown);
    }

    /// <summary>A GRAY verdict has to say what to go and find out.</summary>
    [Fact]
    public void An_undecided_rule_names_the_facts_it_was_missing()
    {
        var rule = Rule(new AllOf(
        [
            new Comparison("deal.type", ConditionOperator.Equals, "retail"),
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "100000"),
            new Comparison("customer.state", ConditionOperator.Equals, "OK"),
        ]));

        var run = Evaluate(rule, RegulatoryContext.Create().With("deal.type", "retail").Build());

        run.MissingFacts.Should().Equal(
            "customer.state", "deal.odometer");
        run.Results[0].Message.Should().Contain("customer.state")
            .And.Contain("deal.odometer");
    }

    /// <summary>Several undecided rules collapse into one list of things to do.</summary>
    [Fact]
    public void The_run_collects_every_missing_fact_once_and_in_order()
    {
        var first = Rule(
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "100000"),
            code: "RULE-A");
        var second = Rule(
            new AllOf(
            [
                new Comparison("deal.odometer", ConditionOperator.LessThan, "500"),
                new Comparison("customer.age", ConditionOperator.GreaterThanOrEqual, "18"),
            ]),
            code: "RULE-B");

        var run = Evaluate([first, second], RegulatoryContext.Create().Build());

        run.UnknownCount.Should().Be(2);
        run.MissingFacts.Should().Equal(
            "customer.age", "deal.odometer");
    }

    // ------------------------------------------------- the opposite failure ----

    /// <summary>
    /// A rule that definitely does not apply stays a pass, even with facts absent.
    /// </summary>
    /// <remarks>
    /// The failure mode on this side is just as real: if a missing fact anywhere in
    /// an <c>all</c> produced GRAY, then every retail-only rule would go gray on
    /// every wholesale deal, every deal would be gray, and GRAY would mean nothing.
    /// </remarks>
    [Fact]
    public void A_rule_scoped_out_by_a_known_fact_is_still_a_pass()
    {
        var rule = Rule(new AllOf(
        [
            new Comparison("deal.type", ConditionOperator.Equals, "retail"),
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "100000"),
        ]));

        // We know this is a wholesale deal, so the rule does not apply whatever the
        // odometer says. A decided No outranks an Unknown.
        var run = Evaluate(rule, RegulatoryContext.Create().With("deal.type", "wholesale").Build());

        run.OverallStatus.Should().Be(DealReadiness.Green);
        run.Results[0].Status.Should().Be(ComplianceStatus.Pass);
    }

    /// <summary>One child that definitely holds satisfies an <c>any</c>.</summary>
    [Fact]
    public void A_satisfied_any_of_does_not_care_what_else_is_missing()
    {
        var rule = Rule(new AnyOf(
        [
            new Comparison("title.status", ConditionOperator.Equals, "held"),
            new Comparison("lien.status", ConditionOperator.Equals, "active"),
        ]));

        var run = Evaluate(rule, RegulatoryContext.Create().With("title.status", "held").Build());

        run.Results[0].Status.Should().Be(
            ComplianceStatus.Block,
            "the first branch decided the disjunction, so the absent lien status cannot change it");
    }

    /// <summary>A complete deal still reaches a clean green.</summary>
    [Fact]
    public void A_deal_with_every_fact_present_is_decided_either_way()
    {
        var rule = Rule(new Comparison(
            "deal.odometer", ConditionOperator.GreaterThan, "100000"));

        var under = Evaluate(rule, RegulatoryContext.Create().With("deal.odometer", 42_000m).Build());
        var over = Evaluate(rule, RegulatoryContext.Create().With("deal.odometer", 142_000m).Build());

        under.OverallStatus.Should().Be(DealReadiness.Green);
        over.OverallStatus.Should().Be(DealReadiness.Red);
        under.MissingFacts.Should().BeEmpty();
    }

    // ------------------------------------------------------- the truth table ---

    /// <summary>Kleene conjunction: a decided No beats an Unknown.</summary>
    [Theory]
    [InlineData("retail", Match.Unknown)]   // scope holds, the rest is unknown
    [InlineData("wholesale", Match.No)]     // scope fails, so the whole thing fails
    public void All_of_lets_a_decided_no_win(string dealType, Match expected)
    {
        var condition = new AllOf(
        [
            new Comparison("deal.type", ConditionOperator.Equals, "retail"),
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "100000"),
        ]);

        var facts = RegulatoryContext.Create().With("deal.type", dealType).Build();

        condition.Matches(facts, []).Should().Be(expected);
    }

    /// <summary>Kleene disjunction: a decided Yes beats an Unknown.</summary>
    [Theory]
    [InlineData("held", Match.Yes)]
    [InlineData("clear", Match.Unknown)]
    public void Any_of_lets_a_decided_yes_win(string titleStatus, Match expected)
    {
        var condition = new AnyOf(
        [
            new Comparison("title.status", ConditionOperator.Equals, "held"),
            new Comparison("lien.status", ConditionOperator.Equals, "active"),
        ]);

        var facts = RegulatoryContext.Create().With("title.status", titleStatus).Build();

        condition.Matches(facts, []).Should().Be(expected);
    }

    /// <summary>Negating an unknown leaves it unknown.</summary>
    [Fact]
    public void Not_preserves_unknown() =>
        new Not(new Comparison("deal.odometer", ConditionOperator.Equals, "0"))
            .Matches(RegulatoryContext.Create().Build(), [])
            .Should().Be(
                Match.Unknown,
                "'we do not know whether X' negates to 'we do not know whether not-X'");

    /// <summary>
    /// <c>exists</c> is the one operator for which absence is the answer.
    /// </summary>
    /// <remarks>
    /// Without this exemption the operators whose entire job is to ask about
    /// absence would themselves become undecidable, and a rule could never ask
    /// "is the title number missing?" at all.
    /// </remarks>
    [Fact]
    public void Existence_operators_stay_decidable_on_an_absent_fact()
    {
        var empty = RegulatoryContext.Create().Build();

        new Comparison("title.number", ConditionOperator.Exists)
            .Matches(empty, []).Should().Be(Match.No);
        new Comparison("title.number", ConditionOperator.NotExists)
            .Matches(empty, []).Should().Be(Match.Yes);
    }

    /// <summary>A present-but-unparseable number is unknown, not false.</summary>
    /// <remarks>
    /// Something upstream is putting "n/a" where a figure belongs. Answering "no"
    /// to that is the same silent failure as answering "no" to an absent fact.
    /// </remarks>
    [Fact]
    public void A_fact_that_is_not_a_number_is_unknown_rather_than_false()
    {
        var facts = RegulatoryContext.Create().With("deal.odometer", "n/a").Build();

        new Comparison("deal.odometer", ConditionOperator.GreaterThan, "100000")
            .Matches(facts, []).Should().Be(Match.Unknown);
    }

    // --------------------------------------------------------- the ranking ----

    /// <summary>A known blocker outranks an unknown, because it is certain.</summary>
    [Fact]
    public void Red_outranks_gray()
    {
        var blocking = Rule(
            new Comparison("deal.type", ConditionOperator.Equals, "retail"),
            code: "RULE-BLOCK");
        var undecided = Rule(
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "1"),
            code: "RULE-GRAY");

        var run = Evaluate(
            [blocking, undecided],
            RegulatoryContext.Create().With("deal.type", "retail").Build());

        run.OverallStatus.Should().Be(DealReadiness.Red);
        run.UnknownCount.Should().Be(1, "the unknown is still recorded, it is just outranked");
    }

    /// <summary>An unknown outranks a warning, because it may itself be a blocker.</summary>
    [Fact]
    public void Gray_outranks_yellow()
    {
        var warning = Rule(
            new Comparison("deal.type", ConditionOperator.Equals, "retail"),
            code: "RULE-WARN",
            severity: RuleSeverity.Warning);
        var undecided = Rule(
            new Comparison("deal.odometer", ConditionOperator.GreaterThan, "1"),
            code: "RULE-GRAY");

        var run = Evaluate(
            [warning, undecided],
            RegulatoryContext.Create().With("deal.type", "retail").Build());

        run.OverallStatus.Should().Be(DealReadiness.Gray);
    }

    /// <summary>
    /// The engine version is part of the evidence, so the change must be visible.
    /// </summary>
    /// <remarks>
    /// A run stores the version that produced it. Without a bump, a GREEN run from
    /// before this change and a GREEN run from after would be indistinguishable,
    /// though they are different claims.
    /// </remarks>
    [Fact]
    public void The_engine_version_records_that_verdicts_changed() =>
        RuleEvaluator.Version.Should().Be("2.0.0");

    // ----------------------------------------------------------- the fixture --

    private static RegulatoryRuleVersion Rule(
        ConditionNode conditions,
        string code = "RULE-UNDER-TEST",
        RuleSeverity severity = RuleSeverity.Blocking) =>
        new()
        {
            Id = RuleId,
            RuleCode = code,
            JurisdictionCode = "US-OK",
            Version = "1.0.0",
            TriggerEvent = "DEAL_SOLD",
            Severity = severity,
            Status = RuleStatus.Active,
            IsApproved = true,
            EffectiveFrom = new DateOnly(2020, 1, 1),
            SourceVersionId = SourceId,
            Conditions = conditions,
            Actions = [],
            Message = "The rule under test applies.",
        };

    private static ComplianceRun Evaluate(RegulatoryRuleVersion rule, RegulatoryContext facts) =>
        Evaluate([rule], facts);

    private static ComplianceRun Evaluate(
        IReadOnlyList<RegulatoryRuleVersion> rules, RegulatoryContext facts) =>
        RuleEvaluator.Evaluate(new EvaluationRequest
        {
            TriggerEvent = "DEAL_SOLD",
            JurisdictionCode = "US-OK",
            EvaluatedOn = new DateOnly(2026, 11, 15),
            RulePackVersion = "test",
            Context = facts,
            Rules = rules,
        });
}
