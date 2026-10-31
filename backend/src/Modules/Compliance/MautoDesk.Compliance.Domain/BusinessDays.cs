namespace MautoDesk.Compliance.Domain;

/// <summary>
/// Business-day arithmetic against a jurisdiction's holiday calendar.
/// </summary>
/// <remarks>
/// <para>
/// A submission deadline of "two business days from the sale" is a real statutory
/// obligation in more than one launch state, and getting it wrong by a day means a
/// dealer misses a filing. So the calendar is an input, not an assumption.
/// </para>
/// <para>
/// <b>An empty calendar is not the same as no holidays.</b> Handing this class an
/// empty set says "this jurisdiction observes no holidays", which is never true.
/// <see cref="ForJurisdiction"/> therefore refuses to guess: a caller that has not
/// loaded a calendar gets an exception rather than a plausible deadline computed
/// against a five-day week that ignores public holidays.
/// <c>compliance.jurisdiction_holiday</c> is seeded empty on purpose, waiting on a
/// cited source.
/// </para>
/// </remarks>
public sealed class BusinessDayCalendar
{
    private readonly IReadOnlySet<DateOnly> _holidays;

    private BusinessDayCalendar(string jurisdictionCode, IReadOnlySet<DateOnly> holidays)
    {
        JurisdictionCode = jurisdictionCode;
        _holidays = holidays;
    }

    /// <summary>The jurisdiction this calendar belongs to.</summary>
    public string JurisdictionCode { get; }

    /// <summary>How many holidays are loaded.</summary>
    public int HolidayCount => _holidays.Count;

    /// <summary>
    /// Builds a calendar from a jurisdiction's loaded holidays.
    /// </summary>
    /// <param name="jurisdictionCode">The jurisdiction, e.g. <c>US-OK</c>.</param>
    /// <param name="holidays">Every observed holiday covering the dates in question.</param>
    /// <exception cref="ArgumentException">
    /// No holidays were supplied. Computing a statutory deadline against a calendar
    /// nobody populated would produce a confident wrong answer, and a dealer would
    /// act on it.
    /// </exception>
    public static BusinessDayCalendar ForJurisdiction(
        string jurisdictionCode,
        IEnumerable<DateOnly> holidays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jurisdictionCode);
        ArgumentNullException.ThrowIfNull(holidays);

        var set = holidays.ToHashSet();

        if (set.Count == 0)
        {
            throw new ArgumentException(
                $"No holiday calendar has been loaded for {jurisdictionCode}. A business-day " +
                "deadline computed against an empty calendar is wrong on every holiday, and " +
                "silently. Populate compliance.jurisdiction_holiday from a cited source first.",
                nameof(holidays));
        }

        return new BusinessDayCalendar(jurisdictionCode, set);
    }

    /// <summary>
    /// A calendar for a jurisdiction that genuinely observes no holidays.
    /// </summary>
    /// <remarks>
    /// Exists so tests and any such jurisdiction can be explicit about it. Naming
    /// it this way means the decision appears at the call site, where a reviewer
    /// can see it, instead of being an empty collection passed by accident.
    /// </remarks>
    public static BusinessDayCalendar WithNoHolidays(string jurisdictionCode) =>
        new(jurisdictionCode, new HashSet<DateOnly>());

    /// <summary>True when the date is a working day.</summary>
    public bool IsBusinessDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(date);

    /// <summary>
    /// Adds business days to a date.
    /// </summary>
    /// <remarks>
    /// The count is of <em>working days elapsed</em>, so two business days from a
    /// Friday is the following Tuesday, and from a Friday before a Monday holiday
    /// is the Wednesday. The start date itself is never counted, whether or not it
    /// is a working day — a sale made on a Saturday starts its clock on Monday.
    /// </remarks>
    public DateOnly AddBusinessDays(DateOnly start, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);

        var date = start;
        var remaining = days;

        while (remaining > 0)
        {
            date = date.AddDays(1);

            if (IsBusinessDay(date))
            {
                remaining--;
            }
        }

        return date;
    }

    /// <summary>Adds calendar days, rolling forward off a non-working day.</summary>
    /// <remarks>
    /// A calendar-day deadline that lands on a Sunday is, in practice, the next
    /// working day — an office that is shut cannot accept a filing. Rolling forward
    /// is the reading that does not put a dealer in default for a closed door.
    /// </remarks>
    public DateOnly AddCalendarDays(DateOnly start, int days)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(days);

        var date = start.AddDays(days);

        while (!IsBusinessDay(date))
        {
            date = date.AddDays(1);
        }

        return date;
    }

    /// <summary>Applies a rule's deadline action to a trigger date.</summary>
    public DateOnly Apply(DeadlineAction deadline, DateOnly triggerDate)
    {
        ArgumentNullException.ThrowIfNull(deadline);

        return deadline.BusinessDays
            ? AddBusinessDays(triggerDate, deadline.Days)
            : AddCalendarDays(triggerDate, deadline.Days);
    }
}
