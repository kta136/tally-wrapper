namespace ShowroomBilling.Contracts;

/// <summary>
/// The showroom's calendar. Bill dates, the fiscal year and "today" are all
/// read on India Standard Time, whatever time zone the server or workstation
/// is set to.
/// </summary>
public static class IndiaTime
{
    /// <summary>India Standard Time, UTC+05:30. India has no daylight saving.</summary>
    public static readonly TimeSpan Offset = TimeSpan.FromMinutes(330);

    /// <summary>The calendar date in India at <paramref name="instant"/>.</summary>
    public static DateOnly DateOf(DateTimeOffset instant) =>
        DateOnly.FromDateTime(instant.ToOffset(Offset).DateTime);

    /// <summary>Today's calendar date in India.</summary>
    public static DateOnly Today(TimeProvider? timeProvider = null) =>
        DateOf((timeProvider ?? TimeProvider.System).GetUtcNow());
}
