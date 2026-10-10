using System.IO;
using System.Text.Json;
using System.Windows;

namespace ShowroomBilling.Desktop.Services;

public static class UiDensityManager
{
    public const string Compact = "Compact";
    public const string Comfortable = "Comfortable";

    // Token values per density. The Compact column matches the defaults in
    // DesignTokens.xaml. Views read these keys with DynamicResource, so a
    // change made in Settings applies to every open view at once.
    internal static readonly IReadOnlyList<(string Key, double Compact, double Comfortable)> Tokens =
    [
        ("RowHeight", 28, 34),
        ("InputHeight", 28, 34),
        ("ButtonHeight", 28, 34),
        ("FontSizeUi", 12.5, 13.5),
        ("FontSizeLabel", 11.5, 12),
        ("FontSizeSm", 11, 12),
        ("FontSizeXs", 10.5, 11),
        ("FontSizeSectionTitle", 10.5, 11.5),
        ("FontSizeMono", 12, 13),
    ];

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
            foreach (var (key, compact, comfortable) in Tokens)
            {
                resources[key] = normalized == Comfortable ? comfortable : compact;
            }
        }

        if (persist)
        {
            SaveDensity(normalized);
        }
    }

    private static void SaveDensity(string density)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new UiPreferences(density)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A preference that cannot be saved must not break the Settings
            // screen; the density still applies for this session.
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
