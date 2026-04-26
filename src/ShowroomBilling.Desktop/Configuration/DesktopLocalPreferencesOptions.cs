namespace ShowroomBilling.Desktop.Configuration;

public sealed class DesktopLocalPreferencesOptions
{
    public const string SectionName = "DesktopLocalPreferences";

    public string PreferredPrinterName { get; set; } = string.Empty;

    public string LastPdfDirectory { get; set; } = "output\\pdf";

    public string KeyboardNavigationProfile { get; set; } = "BillingDefault";
}
