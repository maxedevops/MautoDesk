using FluentAssertions;
using MautoDesk.Sales.TaxKeeper;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// The preconditions for approving a tax rule set.
/// </summary>
/// <remarks>
/// <para>
/// None of these is a tax judgement. Each is a mistake that is easy to make at a
/// keyboard and expensive to find on a signed retail contract: approving a rule
/// set whose citation is still a placeholder, whose trade-in policy is still
/// UNVERIFIED, whose bands do not cover the top of the range, or which computes
/// no tax at all.
/// </para>
/// <para>
/// The last of those is the one worth having. A rule set can satisfy every
/// structural check and still produce $0.00, and nothing downstream questions a
/// confident zero — it is the dealer who makes up the difference to the state.
/// </para>
/// </remarks>
public sealed class TaxApprovalChecksTests
{
    /// <summary>Oklahoma as V0016 drafts it: banded, dollar-rounded, two fields settled.</summary>
    private const string OklahomaRules = """
        {
          "taxBasis": "actual_sales_price",
          "tradeInCreditPolicy": "none",
          "bands": [
            { "upTo": "1500.00", "flat": "20.00", "rate": "0" },
            { "upTo": null, "flat": null, "rate": "3.25" }
          ],
          "taxRounding": "dollars",
          "rebateTreatment": "taxable",
          "fees": [],
          "rounding": "AwayFromZero",
          "roundingScale": 2
        }
        """;

    private static TaxDraft Draft(
        string rulesJson = OklahomaRules,
        bool approved = false,
        int version = 2,
        string citation = "68 O.S. § 2103 (excise tax and rounding) and § 2104 (value), archived by V0015") =>
        new()
        {
            Id = Guid.Parse("0d000000-0000-4000-8000-000000000001"),
            JurisdictionId = Guid.Parse("0f000000-0000-4000-8000-00000000cc01"),
            JurisdictionName = "Oklahoma",
            SchemaVersion = 2,
            Version = version,
            EffectiveFrom = new DateOnly(2025, 7, 1),
            IsApproved = approved,
            SourceCitation = citation,
            RulesJson = rulesJson,
            Notes = "DRAFT, UNAPPROVED. Drafted by repository tooling from archived primary text.",
        };

    private static TaxApprovalRequest Request(
        int version = 3,
        string reviewer = "Dale Cooper CPA",
        DateOnly? from = null,
        DateOnly? to = null) =>
        new()
        {
            DraftId = Guid.Parse("0d000000-0000-4000-8000-000000000001"),
            Version = version,
            ReviewedBy = reviewer,
            OperatorName = "an operator",
            EffectiveFrom = from ?? new DateOnly(2026, 11, 15),
            EffectiveTo = to,
            Note = new string('x', 80),
            NotePath = "/tmp/tax-sign-off.txt",
            Commit = false,
        };

    private static IReadOnlyList<Check> Run(
        TaxApprovalRequest? request = null,
        TaxDraft? draft = null,
        IReadOnlyList<(int Version, DateOnly From, DateOnly? To)>? approved = null) =>
        TaxApprovalChecks.Run(request ?? Request(), draft ?? Draft(), approved ?? []);

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

    /// <summary>The headline number, computed through the real engine.</summary>
    [Fact]
    public void The_oklahoma_draft_prices_a_twenty_thousand_dollar_deal()
    {
        var check = Find(Run(), "It prices a real deal");

        check.Passed.Should().BeTrue();
        check.Detail.Should().Contain(
            "621",
            "20.00 + 3.25% of 18,500 is 621.25, which 68 O.S. 2103 rounds to the dollar");
    }

    [Fact]
    public void An_already_approved_rule_set_cannot_be_approved_again()
    {
        Find(Run(draft: Draft(approved: true)), "The draft is a draft")
            .Passed.Should().BeFalse(
                "an approval is a new row, so approving an approved one means somebody has the "
                + "wrong id");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("UNVERIFIED")]
    [InlineData("TODO: find a CPA")]
    [InlineData("placeholder")]
    [InlineData("AB")]
    public void An_approval_that_names_nobody_is_refused(string reviewer) =>
        Find(Run(request: Request(reviewer: reviewer)), "The reviewer is named")
            .Passed.Should().BeFalse("approved_by is the whole deliverable");

    [Fact]
    public void The_approved_version_may_not_reuse_the_drafts_own_number() =>
        Find(Run(request: Request(version: 2)), "The approved version is a new version")
            .Passed.Should().BeFalse();

    [Fact]
    public void A_placeholder_citation_cannot_be_approved()
    {
        var checks = Run(draft: Draft(
            citation: "PLACEHOLDER — must cite the governing statute or DMV publication"));

        Find(checks, "The source is cited").Passed.Should().BeFalse(
            "a tax figure with no statute behind it is the thing this system refuses to ship");
    }

    /// <summary>
    /// The field that can be worth thousands of dollars on one deal.
    /// </summary>
    /// <remarks>
    /// Oklahoma's two archived sections do not say whether a trade-in reduces the
    /// taxable amount, so the drafted row leaves it UNVERIFIED. Approving it in
    /// that state would price every deal with a trade on a silent default.
    /// </remarks>
    [Fact]
    public void A_rule_set_with_an_unverified_trade_in_policy_is_refused()
    {
        var checks = Run(draft: Draft(
            OklahomaRules.Replace(
                "\"tradeInCreditPolicy\": \"none\"",
                "\"tradeInCreditPolicy\": \"UNVERIFIED\"",
                StringComparison.Ordinal)));

        var check = Find(checks, "The figures the engine reads are settled");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("tradeInCreditPolicy");
    }

    [Fact]
    public void A_rule_set_with_an_unverified_basis_is_refused()
    {
        var checks = Run(draft: Draft(
            OklahomaRules.Replace(
                "\"taxBasis\": \"actual_sales_price\"",
                "\"taxBasis\": \"UNVERIFIED\"",
                StringComparison.Ordinal)));

        Find(checks, "The figures the engine reads are settled").Passed.Should().BeFalse();
    }

    /// <summary>
    /// The quiet one: the rule set is structurally perfect and the tax is zero.
    /// </summary>
    /// <remarks>
    /// Bands that ascend, a top band that is unbounded, a real citation, nothing
    /// UNVERIFIED — and every rate in it is zero, so a $20,000 car is taxed
    /// nothing. Every structural check passes. Without an end-to-end pricing
    /// check, this is the rule set that gets approved and puts $0.00 of tax on
    /// retail contracts until somebody at the state notices.
    /// </remarks>
    [Fact]
    public void A_rule_set_whose_rates_are_all_zero_is_refused()
    {
        var checks = Run(draft: Draft("""
            {
              "taxBasis": "actual_sales_price",
              "tradeInCreditPolicy": "none",
              "bands": [
                { "upTo": "1500.00", "flat": null, "rate": "0" },
                { "upTo": null, "flat": null, "rate": "0" }
              ],
              "taxRounding": "dollars",
              "rebateTreatment": "taxable",
              "fees": []
            }
            """));

        Find(checks, "The bands are coherent").Passed.Should().BeTrue(
            "structurally there is nothing wrong with it, which is the problem");

        var check = Find(checks, "It prices a real deal");

        check.Passed.Should().BeFalse();
        check.Detail.Should().Contain("no tax at all");
    }

    /// <summary>A rule set with no rate at all does not even deserialize.</summary>
    [Fact]
    public void A_rule_set_with_neither_bands_nor_a_rate_is_refused()
    {
        var checks = Run(draft: Draft("""
            {
              "taxBasis": "actual_sales_price",
              "tradeInCreditPolicy": "none",
              "rebateTreatment": "taxable",
              "fees": []
            }
            """));

        var check = Find(checks, "The rules deserialize as approved");

        check.Passed.Should().BeFalse(
            "the serializer is strict on an approved row precisely so a missing rate cannot "
            + "default to zero");
        check.Detail.Should().Contain("stateRate");
    }

    [Fact]
    public void A_bounded_top_band_is_refused()
    {
        var checks = Run(draft: Draft("""
            {
              "taxBasis": "actual_sales_price",
              "tradeInCreditPolicy": "none",
              "bands": [
                { "upTo": "1500.00", "flat": "20.00", "rate": "0" },
                { "upTo": "50000.00", "flat": null, "rate": "3.25" }
              ],
              "taxRounding": "dollars",
              "rebateTreatment": "taxable",
              "fees": []
            }
            """));

        var check = Find(checks, "The bands are coherent");

        check.Passed.Should().BeFalse(
            "a $70,000 truck would be taxed on $50,000 of its value and nothing would say so");
        check.Detail.Should().Contain("unbounded");
    }

    [Fact]
    public void Bands_alongside_a_flat_rate_are_refused()
    {
        var checks = Run(draft: Draft(
            OklahomaRules.Replace(
                "\"taxRounding\": \"dollars\"",
                "\"taxRounding\": \"dollars\", \"stateRate\": \"4.5\"",
                StringComparison.Ordinal)));

        Find(checks, "The bands are coherent").Passed.Should().BeFalse(
            "that is two taxes, and picking one would be a guess about money on a contract");
    }

    [Fact]
    public void An_effective_period_that_ends_before_it_starts_is_refused() =>
        Find(
            Run(request: Request(
                from: new DateOnly(2027, 1, 1), to: new DateOnly(2026, 1, 1))),
            "The effective period is a period")
            .Passed.Should().BeFalse();

    [Fact]
    public void Two_approved_rule_sets_may_not_cover_the_same_day()
    {
        var checks = Run(
            request: Request(from: new DateOnly(2026, 6, 1)),
            approved: [(1, new DateOnly(2024, 1, 1), null)]);

        var check = Find(checks, "No approved rule set covers these dates");

        check.Passed.Should().BeFalse("two rule sets covering one day is two prices for one deal");
        check.Detail.Should().Contain("Version 1");
    }

    [Fact]
    public void A_rule_set_that_starts_where_the_last_one_ends_does_not_overlap() =>
        Find(
            Run(
                request: Request(from: new DateOnly(2027, 1, 1)),
                approved: [(1, new DateOnly(2024, 1, 1), new DateOnly(2027, 1, 1))]),
            "No approved rule set covers these dates")
            .Passed.Should().BeTrue(
                "the range is half-open, so effective_to is the first day the old set stops "
                + "governing");

    [Fact]
    public void Malformed_json_fails_rather_than_defaulting()
    {
        var checks = Run(draft: Draft("{ this is not json"));

        Find(checks, "The rules deserialize as approved").Passed.Should().BeFalse();
        Find(checks, "It prices a real deal").Passed.Should().BeFalse(
            "a rule set that cannot be read must not be reported as pricing anything");
    }

    /// <summary>A flat-rate jurisdiction is unaffected by the band machinery.</summary>
    [Fact]
    public void A_flat_rate_jurisdiction_passes_without_bands()
    {
        var checks = Run(draft: Draft("""
            {
              "taxBasis": "actual_sales_price",
              "tradeInCreditPolicy": "full",
              "stateRate": "6.25",
              "rebateTreatment": "taxable",
              "fees": []
            }
            """));

        checks.Where(check => !check.Passed).Should().BeEmpty();
        Find(checks, "The bands are coherent").Detail.Should().Contain("Not a banded jurisdiction");
    }
}
