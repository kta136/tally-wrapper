using System.Globalization;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Invoice;

/// <summary>
/// Today's rate per gram at 24K, 22K and 18K: the line the Invoice screen shows next to the
/// line items so the operator can check the 24K rate they typed against the usual ones.
/// </summary>
public sealed record RateLadder(string Rate24Kt, string Rate22Kt, string Rate18Kt)
{
    // Standard Indian jewellery purities, used when the karat master has no matching row
    // (before Settings has loaded, or when a label is not a number of karats).
    private const decimal Default22KtPurity = 91.6m;
    private const decimal Default18KtPurity = 75.0m;

    /// <summary>The ladder for a typed 24K rate, or null while no rate has been typed.</summary>
    public static RateLadder? For(decimal? rate24Kt, IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        if (rate24Kt is not { } rate24 || rate24 <= 0m)
        {
            return null;
        }

        var (purity22, purity18) = ResolvePurities(karatMasters);
        return new RateLadder(
            Format(rate24),
            Format(rate24 * purity22 / 100m),
            Format(rate24 * purity18 / 100m));
    }

    private static string Format(decimal value)
        => Math.Round(value, 0, MidpointRounding.AwayFromZero).ToString("N0", UiCulture.Culture);

    private static (decimal Purity22, decimal Purity18) ResolvePurities(IEnumerable<KaratMasterRowVm>? karatMasters)
    {
        var purity22 = Default22KtPurity;
        var purity18 = Default18KtPurity;
        if (karatMasters is null)
        {
            return (purity22, purity18);
        }

        foreach (var row in karatMasters)
        {
            if (!decimal.TryParse(row.PurityPercent, NumberStyles.Number, CultureInfo.InvariantCulture, out var purity)
                || purity is <= 0m or > 100m)
            {
                continue;
            }

            if (LabelMatchesKarat(row.Label, 22)) purity22 = purity;
            else if (LabelMatchesKarat(row.Label, 18)) purity18 = purity;
        }

        return (purity22, purity18);
    }

    // Accepts "22", "22KT", "22kt", "22 K" and "22-karat": the leading digits are the karat.
    private static bool LabelMatchesKarat(string? label, int karat)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;

        var digits = 0;
        var consumed = 0;
        foreach (var ch in label.AsSpan().Trim())
        {
            if (!char.IsDigit(ch)) break;
            digits = digits * 10 + (ch - '0');
            consumed++;
        }

        return consumed > 0 && digits == karat;
    }
}
