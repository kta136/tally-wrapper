using System.Globalization;

namespace ShowroomBilling.Infrastructure.Tally;

internal static class TallyIds
{
    /// <summary>
    /// Tally voucher and master ids are positive integers. Returns the trimmed
    /// text when it is one, otherwise null (Tally sends 0 or blank for "none").
    /// </summary>
    internal static string? NormalizePositiveInteger(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? trimmed
            : null;
    }
}
