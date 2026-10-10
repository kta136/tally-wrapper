namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// The one zoom range for the print preview and the settings preview, which
/// share a saved zoom value. With separate ranges, a zoom the view accepted
/// was saved as a different number and snapped the view back.
/// </summary>
public static class PrintPreviewZoom
{
    public const double DefaultPercent = 100;
    public const double MinPercent = 25;
    public const double MaxPercent = 200;

    /// <summary>Rounds to a whole percent inside the range; nonsense values become the default.</summary>
    public static double Normalize(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
        {
            return DefaultPercent;
        }

        return Math.Clamp(Math.Round(value), MinPercent, MaxPercent);
    }
}
