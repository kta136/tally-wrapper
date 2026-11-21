using ShowroomBilling.Contracts;
using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Application.Bills;

/// <summary>
/// The minutes a batch may date a bill at: those of its times that fall inside the shop hours
/// (<see cref="BatchHoursSettings"/>, owner, 21 November 2026). The planner dates bills at these
/// minutes and no others, and the Batch Data Scheduler's dialog counts them to say how many bills
/// its times have room for, so both go through this class.
///
/// The hours are India's: a minute is open when its time of day there is from the opening to the
/// closing time, both included, and its weekday there is not the closed day. It is worked out a
/// day at a time, never a minute at a time.
/// </summary>
public static class BatchOpenMinutes
{
    private const long MinutesInDay = 24 * 60;

    private static readonly long IndiaOffsetMinutes = (long)IndiaTime.Offset.TotalMinutes;

    /// <summary>
    /// The number of whole minutes a batch may date a bill at: minutes m with
    /// ceil-to-minute(max(start, earliest)) &lt;= m &lt;= floor-to-minute(end), whose time of day in India
    /// is from hours.Opens to hours.Closes inclusive (any time when either is null) and whose weekday
    /// in India is not hours.ClosedOn. 0 when start &gt;= end.
    /// </summary>
    public static int Count(DateTimeOffset start, DateTimeOffset end, DateTimeOffset? earliest, BatchHoursSettings? hours)
    {
        if (start >= end) return 0;

        var from = earliest is { } first && first > start ? first : start;
        var total = 0L;
        foreach (var run in Runs(from, end, hours))
            total += run.Length;
        return (int)Math.Min(total, int.MaxValue);
    }

    /// <summary>
    /// How many bills fit in openMinutes minutes when two bills are at least gapMinutes apart:
    /// 0 for none, else (openMinutes - 1) / max(1, gapMinutes) + 1.
    /// </summary>
    public static int RoomAtGap(int openMinutes, int gapMinutes)
        => openMinutes <= 0 ? 0 : (openMinutes - 1) / Math.Max(1, gapMinutes) + 1;

    /// <summary>
    /// Open minutes that follow one another: the first, as a minute of India's clock counted from
    /// the year 1, and how many there are.
    /// </summary>
    internal readonly record struct Run(long FirstMinute, long Length);

    /// <summary>
    /// The open minutes from ceil-to-minute(<paramref name="from"/>) to floor-to-minute(<paramref name="to"/>),
    /// in order, as runs: one for each day that has any, or a single run when no hours are set.
    /// </summary>
    internal static IEnumerable<Run> Runs(DateTimeOffset from, DateTimeOffset to, BatchHoursSettings? hours)
    {
        var first = (from.UtcTicks + TimeSpan.TicksPerMinute - 1) / TimeSpan.TicksPerMinute + IndiaOffsetMinutes;
        var last = to.UtcTicks / TimeSpan.TicksPerMinute + IndiaOffsetMinutes;
        if (last < first) yield break;

        // Hours are an opening and a closing time together; one without the other is no hours.
        // A time counts by its hour and minute: seconds are not stored, and are not looked at.
        var (opens, closes) = hours is { Opens: { } o, Closes: { } c }
            ? (o.Hour * 60L + o.Minute, c.Hour * 60L + c.Minute)
            : (0L, MinutesInDay - 1);
        var closedOn = hours?.ClosedOn;

        if (opens == 0 && closes == MinutesInDay - 1 && closedOn is null)
        {
            yield return new Run(first, last - first + 1);
            yield break;
        }

        for (var day = first / MinutesInDay; day <= last / MinutesInDay; day++)
        {
            if (closedOn is { } closed && WeekdayOf(day) == closed) continue;

            var dayFirst = Math.Max(first, day * MinutesInDay + opens);
            var dayLast = Math.Min(last, day * MinutesInDay + closes);
            if (dayLast >= dayFirst)
                yield return new Run(dayFirst, dayLast - dayFirst + 1);
        }
    }

    /// <summary>The instant, in UTC, of a minute of India's clock as <see cref="Run"/> counts them.</summary>
    internal static DateTimeOffset InstantOf(long minute)
        => new((minute - IndiaOffsetMinutes) * TimeSpan.TicksPerMinute, TimeSpan.Zero);

    /// <summary>The weekday of a day counted from 1 January of the year 1, which was a Monday.</summary>
    private static DayOfWeek WeekdayOf(long day) => (DayOfWeek)((day + 1) % 7);
}
