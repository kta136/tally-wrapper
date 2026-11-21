using System;
using System.Globalization;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

/// <summary>
/// A time of day as the Batch Data Scheduler shows and reads it: 24-hour, hours and minutes. The
/// app has no time picker, so a time is typed; "10:43", "9:05", "1043", "10.43" and "10" are all read.
/// </summary>
public static class BatchClock
{
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    public static bool TryParse(string? text, out TimeOnly time)
    {
        time = default;
        var typed = (text ?? string.Empty).Trim().Replace('.', ':');
        if (typed.Length == 0) return false;

        // "1043" and "943" are a time without its colon; "10" and "9" are a whole hour.
        if (!typed.Contains(':'))
        {
            typed = typed.Length <= 2 ? typed + ":00" : typed[..^2] + ":" + typed[^2..];
        }

        var parts = typed.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hour)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minute)
            || hour > 23
            || minute > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }
}
