using System.IO;
using System.Text.Json;
using System.Windows;

namespace ShowroomBilling.Desktop.Services;

public static class UiDensityManager
{
    public const string Compact = "Compact";
    public const string Comfortable = "Comfortable";

    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ShowroomBilling");

    private static readonly string FilePath = Path.Combine(Folder, "ui-preferences.json");

    public static string CurrentDensity { get; private set; } = Compact;

    public static void ApplyStoredDensity()
    {
        var density = LoadDensity();
        ApplyDensity(density, persist: false);
    }

    public static void ApplyDensity(string density, bool persist = true)
    {
        var normalized = string.Equals(density, Comfortable, StringComparison.OrdinalIgnoreCase)
            ? Comfortable
            : Compact;
        CurrentDensity = normalized;

        var resources = System.Windows.Application.Current?.Resources;
        if (resources is not null)
        {
            if (normalized == Comfortable)
            {
                resources["RowHeight"] = 34d;
                resources["InputHeight"] = 34d;
                resources["ButtonHeight"] = 34d;
                resources["CellPadding"] = 12d;
                resources["FontSizeUi"] = 13.5d;
                resources["FontSizeLabel"] = 12d;
                resources["FontSizeSm"] = 12d;
                resources["FontSizeXs"] = 11d;
                resources["FontSizeSectionTitle"] = 11.5d;
                resources["FontSizeMono"] = 13d;
            }
            else
            {
                resources["RowHeight"] = 28d;
                resources["InputHeight"] = 28d;
                resources["ButtonHeight"] = 28d;
                resources["CellPadding"] = 10d;
                resources["FontSizeUi"] = 12.5d;
                resources["FontSizeLabel"] = 11.5d;
                resources["FontSizeSm"] = 11d;
                resources["FontSizeXs"] = 10.5d;
                resources["FontSizeSectionTitle"] = 10.5d;
                resources["FontSizeMono"] = 12d;
            }
        }

        if (persist)
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new UiPreferences(normalized)));
        }
    }

    private static string LoadDensity()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return Compact;
            }

            var prefs = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath));
            return string.Equals(prefs?.Density, Comfortable, StringComparison.OrdinalIgnoreCase)
                ? Comfortable
                : Compact;
        }
        catch
        {
            return Compact;
        }
    }

    private sealed record UiPreferences(string Density);
}
