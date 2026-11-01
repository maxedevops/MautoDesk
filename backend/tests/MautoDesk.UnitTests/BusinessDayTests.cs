using FluentAssertions;
using MautoDesk.Compliance.Domain;
using Xunit;

namespace MautoDesk.UnitTests;

/// <summary>
/// Business-day arithmetic.
/// </summary>
/// <remarks>
/// "Two business days from the sale" is a real statutory obligation in more than
/// one launch state, and being a day out means a dealer misses a filing. The
/// dates below are fictional holidays on a fictional jurisdiction; the real
/// calendar is regulatory content and arrives from a cited source.
/// </remarks>
public sealed class BusinessDayTests
{
    private const string Jurisdiction = "ZZ-TEST";

    // Thursday 2 July and Friday 3 July 2026, invented for these tests.
    private static readonly DateOnly[] Holidays =
    [
        new(2026, 7, 2),
        new(2026, 7, 3),
    ];

    /// <summary>Two business days from a Monday is the Wednesday.</summary>
    [Fact]
    public void Business_days_advance_within_a_week()
    {
        var calendar = Calendar();

        // Monday 15 June 2026.
        calendar.AddBusinessDays(new DateOnly(2026, 6, 15), 2)
            .Should().Be(new DateOnly(2026, 6, 17));
    }

    /// <summary>Two business days from a Thursday skips the weekend to Monday.</summary>
    [Fact]
    public void Business_days_skip_the_weekend()
    {
        var calendar = Calendar();

        // Thursday 18 June 2026 → Friday 19 (1) → Monday 22 (2).
        calendar.AddBusinessDays(new DateOnly(2026, 6, 18), 2)
            .Should().Be(new DateOnly(2026, 6, 22));
    }

    /// <summary>
    /// Holidays are skipped as well as weekends.
    /// </summary>
    /// <remarks>
    /// From Wednesday 1 July 2026: Thursday the 2nd and Friday the 3rd are
    /// holidays in this fixture, the 4th and 5th are the weekend, so two business
    /// days lands on Tuesday the 7th. Without the calendar it would say Friday the
    /// 3rd — two working days early, and confidently.
    /// </remarks>
    [Fact]
    public void Holidays_are_skipped()
    {
        var calendar = Calendar();

        calendar.AddBusinessDays(new DateOnly(2026, 7, 1), 2)
            .Should().Be(new DateOnly(2026, 7, 7));
    }

    /// <summary>A sale on a Saturday starts its clock on the Monday.</summary>
    /// <remarks>
    /// The start date is never counted, whether or not it is a working day — so
    /// one business day from Saturday 20 June is Monday the 22nd.
    /// </remarks>
    [Fact]
    public void A_weekend_start_date_is_not_counted()
    {
        var calendar = Calendar();

        calendar.AddBusinessDays(new DateOnly(2026, 6, 20), 1)
            .Should().Be(new DateOnly(2026, 6, 22));
    }

    /// <summary>Zero business days is the start date itself.</summary>
    [Fact]
    public void Zero_business_days_is_the_same_day()
    {
        var start = new DateOnly(2026, 6, 15);

        Calendar().AddBusinessDays(start, 0).Should().Be(start);
    }

    /// <summary>A calendar-day deadline landing on a weekend rolls to the next working day.</summary>
    /// <remarks>
    /// An office that is shut cannot accept a filing, and putting a dealer in
    /// default for a closed door is not the reading to take.
    /// </remarks>
    [Fact]
    public void Calendar_days_roll_forward_off_a_closed_day()
    {
        var calendar = Calendar();

        // Monday 15 June plus 5 calendar days is Saturday the 20th → Monday the 22nd.
        calendar.AddCalendarDays(new DateOnly(2026, 6, 15), 5)
            .Should().Be(new DateOnly(2026, 6, 22));
    }

    /// <summary>Weekends and holidays are not business days; ordinary weekdays are.</summary>
    [Theory]
    [InlineData(2026, 6, 15, true)]   // Monday
    [InlineData(2026, 6, 19, true)]   // Friday
    [InlineData(2026, 6, 20, false)]  // Saturday
    [InlineData(2026, 6, 21, false)]  // Sunday
    [InlineData(2026, 7, 3, false)]   // fixture holiday
    public void Working_days_are_identified(int year, int month, int day, bool expected) =>
        Calendar().IsBusinessDay(new DateOnly(year, month, day)).Should().Be(expected);

    /// <summary>
    /// An empty calendar is refused, because it is never the truth.
    /// </summary>
    /// <remarks>
    /// <c>compliance.jurisdiction_holiday</c> is seeded empty on purpose, waiting
    /// on a cited source. Handing that emptiness to the calculator would produce a
    /// confident wrong deadline on every holiday, so the constructor refuses
    /// instead of guessing.
    /// </remarks>
    [Fact]
    public void An_unpopulated_calendar_is_refused()
    {
        var act = () => BusinessDayCalendar.ForJurisdiction(Jurisdiction, []);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*No holiday calendar has been loaded*");
    }

    /// <summary>A jurisdiction with genuinely no holidays must say so explicitly.</summary>
    /// <remarks>
    /// Named so the decision appears at the call site where a reviewer can see it,
    /// rather than arriving as an empty collection passed by accident.
    /// </remarks>
    [Fact]
    public void A_deliberately_empty_calendar_is_available_and_obvious()
    {
        var calendar = BusinessDayCalendar.WithNoHolidays(Jurisdiction);

        calendar.HolidayCount.Should().Be(0);
        calendar.IsBusinessDay(new DateOnly(2026, 7, 3)).Should().BeTrue();
    }

    /// <summary>The deadline action decides which arithmetic applies.</summary>
    [Fact]
    public void A_deadline_action_selects_business_or_calendar_days()
    {
        var calendar = Calendar();
        var start = new DateOnly(2026, 7, 1);

        calendar.Apply(new DeadlineAction("X", 2, BusinessDays: true), start)
            .Should().Be(new DateOnly(2026, 7, 7));

        // Two calendar days is Friday the 3rd, a fixture holiday, so it rolls to
        // Monday the 6th.
        calendar.Apply(new DeadlineAction("X", 2, BusinessDays: false), start)
            .Should().Be(new DateOnly(2026, 7, 6));
    }

    private static BusinessDayCalendar Calendar() =>
        BusinessDayCalendar.ForJurisdiction(Jurisdiction, Holidays);
}
