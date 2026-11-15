using FluentAssertions;
using MautoDesk.Sales.Domain;
using MautoDesk.SharedKernel;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// A graduated tax, and the one jurisdiction that forced the engine to grow one.
/// </summary>
/// <remarks>
/// <para>
/// Oklahoma's used-vehicle excise tax is not a percentage. 68 O.S. § 2103,
/// archived and hashed by <c>V0015</c>, levies "Twenty Dollars ($20.00) on the
/// first One Thousand Five Hundred Dollars ($1,500.00) or less of value of such
/// vehicle, and three and one-fourth percent (3 1/4%) of the remaining value of
/// such vehicle", and says the result "shall be rounded to the nearest dollar".
/// </para>
/// <para>
/// A single <see cref="Percentage"/> cannot express that, so before this the
/// engine could not have priced an Oklahoma deal even with an approved rule set.
/// The expectations below are computed by hand from that sentence, not from the
/// engine.
/// </para>
/// </remarks>
public sealed class TaxBandTests
{
    /// <summary>The Oklahoma used-vehicle shape, as <c>V0016</c> drafts it.</summary>
    private static readonly TaxBand[] OklahomaUsed =
    [
        new(Money.FromDecimal(1_500m), Percentage.Zero, Money.FromDecimal(20m),
            "Twenty Dollars ($20.00) on the first One Thousand Five Hundred Dollars"),
        new(null, Percentage.FromPercent(3.25m), null,
            "and three and one-fourth percent (3 1/4%) of the remaining value"),
    ];

    // --------------------------------------------------------- the statute ----

    /// <summary>
    /// The worked example: $20 + 3.25% of $18,500 = $621.25, rounded to $621.
    /// </summary>
    [Fact]
    public void A_twenty_thousand_dollar_car_owes_six_hundred_and_twenty_one()
    {
        var tax = Tax(20_000m);

        // 20.00 + (20,000 - 1,500) * 0.0325 = 20.00 + 601.25 = 621.25 -> 621
        tax.Should().Be(Money.FromDecimal(621m));
    }

    /// <summary>
    /// "or less" means a cheap car still owes the whole twenty dollars.
    /// </summary>
    /// <remarks>
    /// The easy misreading is to prorate the flat amount across the first band, so
    /// a $900 car owes $12. The statute does not say that, and a tax that is too
    /// low is as wrong as one that is too high — it is the dealer who makes up the
    /// difference to the state.
    /// </remarks>
    [Theory]
    [InlineData(1, 20)]
    [InlineData(900, 20)]
    [InlineData(1_499, 20)]
    [InlineData(1_500, 20)]
    public void Anything_in_the_first_band_owes_the_flat_twenty(decimal value, decimal expected) =>
        Tax(value).Should().Be(Money.FromDecimal(expected));

    /// <summary>The second band applies only to the part above $1,500.</summary>
    [Fact]
    public void The_percentage_applies_only_to_the_remainder()
    {
        // 20.00 + (2,500 - 1,500) * 0.0325 = 20.00 + 32.50 = 52.50 -> 53
        // (AwayFromZero, so a half rounds up.)
        Tax(2_500m).Should().Be(Money.FromDecimal(53m));
    }

    /// <summary>A vehicle with no value owes nothing, flat band or not.</summary>
    /// <remarks>
    /// The flat amount is charged "the moment the base reaches the band", and a
    /// base of zero does not reach it. Without this, a trade that cancelled the
    /// price would produce twenty dollars of tax on a sale of nothing.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-500)]
    public void A_base_of_nothing_owes_nothing(decimal value) =>
        Tax(value).Should().Be(Money.Zero);

    /// <summary>Rounding to the dollar is the statute's instruction, not a preference.</summary>
    [Fact]
    public void Dollar_rounding_is_applied_to_the_tax_line_only()
    {
        var toTheDollar = Tax(20_000m, TaxRounding.Dollars);
        var toTheCent = Tax(20_000m, TaxRounding.Cents);

        toTheDollar.Should().Be(Money.FromDecimal(621m));
        toTheCent.Should().Be(
            Money.FromDecimal(621.25m),
            "the rest of the deal is still to the cent; only this line rounds");
    }

    // ------------------------------------------------------- malformed bands --

    /// <summary>A rule set cannot carry both a flat rate and bands.</summary>
    [Fact]
    public void A_flat_rate_and_bands_together_are_refused()
    {
        var rules = RuleSet(OklahomaUsed) with { StateRate = Percentage.FromPercent(3.25m) };

        rules.BandError.Should().NotBeNull()
            .And.Contain("does not describe one tax");
    }

    /// <summary>An unbounded band that is not last makes the bands above unreachable.</summary>
    [Fact]
    public void An_unbounded_band_must_be_the_last_one()
    {
        var rules = RuleSet(
        [
            new TaxBand(null, Percentage.FromPercent(1m)),
            new TaxBand(Money.FromDecimal(5_000m), Percentage.FromPercent(2m)),
        ]);

        rules.BandError.Should().NotBeNull().And.Contain("never be reached");
    }

    /// <summary>A bounded top band would stop taxing an expensive car.</summary>
    /// <remarks>
    /// This is the quiet one. A rule set whose top band ends at $50,000 taxes a
    /// $70,000 truck on $50,000 of it and reports a confident figure, and nothing
    /// about the output says that twenty thousand dollars went untaxed.
    /// </remarks>
    [Fact]
    public void The_top_band_must_be_unbounded()
    {
        var rules = RuleSet(
        [
            new TaxBand(Money.FromDecimal(1_500m), Percentage.Zero, Money.FromDecimal(20m)),
            new TaxBand(Money.FromDecimal(50_000m), Percentage.FromPercent(3.25m)),
        ]);

        rules.BandError.Should().NotBeNull().And.Contain("must be unbounded");
    }

    /// <summary>Bands have to ascend, or the arithmetic is nonsense.</summary>
    [Fact]
    public void Bands_must_ascend()
    {
        var rules = RuleSet(
        [
            new TaxBand(Money.FromDecimal(5_000m), Percentage.FromPercent(1m)),
            new TaxBand(Money.FromDecimal(1_500m), Percentage.FromPercent(2m)),
            new TaxBand(null, Percentage.FromPercent(3m)),
        ]);

        rules.BandError.Should().NotBeNull().And.Contain("must ascend");
    }

    /// <summary>The well-formed Oklahoma set has nothing wrong with it.</summary>
    [Fact]
    public void The_oklahoma_band_set_is_valid() =>
        RuleSet(OklahomaUsed).BandError.Should().BeNull();

    /// <summary>A flat-rate jurisdiction is unaffected by any of this.</summary>
    [Fact]
    public void A_jurisdiction_with_no_bands_has_no_band_error()
    {
        var rules = RuleSet([]) with { StateRate = Percentage.FromPercent(6.25m) };

        rules.BandError.Should().BeNull();
        rules.IsBanded.Should().BeFalse();
        rules.CombinedRate.Should().Be(Percentage.FromPercent(6.25m));
    }

    // ----------------------------------------------------------- the fixture --

    private static Money Tax(decimal taxableAmount, TaxRounding rounding = TaxRounding.Dollars)
    {
        // Driven through the engine rather than a helper, so what is asserted is
        // what a deal would actually be priced at.
        var rules = RuleSet(OklahomaUsed) with { Rounding = rounding, IsApproved = true };

        var input = new DealInput
        {
            DealId = Guid.Parse("0e000000-0000-4000-8000-00000000bb01"),
            DealDate = new DateOnly(2026, 11, 15),
            VehiclePrice = Money.FromDecimal(Math.Max(taxableAmount, 0m)),
        };

        // A negative base cannot be expressed as a vehicle price, so it arrives the
        // way it actually would: a trade allowance larger than the price, with the
        // jurisdiction crediting the trade in full.
        if (taxableAmount < 0m)
        {
            input = input with
            {
                VehiclePrice = Money.FromDecimal(1_000m),
                Trade = new TradeIn(
                    Money.FromDecimal(1_000m - taxableAmount),
                    Money.Zero),
            };

            rules = rules with { TradeInCredit = TradeInCreditPolicy.Full };
        }

        var result = DealEngine.Calculate(input, rules);

        result.IsSuccess.Should().BeTrue("the rule set should price this deal");

        return result.Value.SalesTax;
    }

    private static TaxRuleSet RuleSet(IReadOnlyList<TaxBand> bands) => new()
    {
        RuleSetId = Guid.Parse("0d000000-0000-4000-8000-000000000001"),
        Version = 2,
        JurisdictionId = Guid.Parse("0f000000-0000-4000-8000-00000000cc01"),
        EffectiveFrom = new DateOnly(2025, 7, 1),
        IsApproved = false,
        SourceCitation = "68 O.S. § 2103, archived by V0015",
        Bands = bands,
        Rounding = TaxRounding.Dollars,
        TradeInCredit = TradeInCreditPolicy.None,
    };
}
