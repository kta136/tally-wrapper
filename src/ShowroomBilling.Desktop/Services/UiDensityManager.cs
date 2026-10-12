using System.IO;
using System.Text.Json;
using System.Windows;
using ShowroomBilling.Contracts;

namespace ShowroomBilling.Desktop.Services;

public static class UiDensityManager
{
    public const string Compact = "Compact";
    public const string Standard = "Standard";
    public const string Comfortable = "Comfortable";

    /// <summary>The three densities, in the order Settings lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [Compact, Standard, Comfortable];

    // Token values per density. The Standard column matches the defaults in
    // DesignTokens.xaml. Views read these keys with DynamicResource, so a
    // change made in Settings applies to every open view at once.
    internal static readonly IReadOnlyList<(string Key, double Compact, double Standard, double Comfortable)> Tokens =
    [
        ("RowHeight", 32, 38, 44),
        ("ListRowHeight", 36, 42, 48),
        ("InputHeight", 30, 34, 40),
        ("ButtonHeight", 30, 34, 40),
        ("FontSizeUi", 12.5, 13.5, 14.5),
        ("FontSizeLabel", 11.5, 12.5, 13.5),
        ("FontSizeSm", 11.5, 12.5, 13.5),
        ("FontSizeXs", 10.5, 11, 12),
        ("FontSizeSectionTitle", 11.5, 12.5, 13.5),
        ("FontSizeMono", 12, 12.5, 13.5),
        ("CellPaddingX", 8, 10, 12),
    ];

    private const int CurrentFileVersion = 2;

    private static readonly string Folder = AppPaths.DataDirectory;

    private static readonly string FilePath = Path.Combine(Folder, "ui-preferences.json");

    public static string CurrentDensity { get; private set; } = Standard;

    public static void ApplyStoredDensity()
    {
        var density = LoadDensity();
        ApplyDensity(density, persist: false);
    }

    public static void ApplyDensity(string density, bool persist = true)
    {
        var normalized = Normalize(density);
        CurrentDensity = normalized;

        var resources = System.Windows.Application.Current?.Resources;
        if (resources is not null)
        {
            foreach (var (key, compact, standard, comfortable) in Tokens)
            {
                var value = normalized switch
                {
                    Compact => compact,
                    Comfortable => comfortable,
                    _ => standard,
                };
                resources[key] = value;
                if (key == "CellPaddingX")
                {
                    // A Thickness cannot be bound to a double, so the padding the table cells
                    // read is kept in step with it here.
                    resources["CellPadding"] = new Thickness(value, 0, value, 0);
                }
            }
        }

        if (persist)
        {
            SaveDensity(normalized);
        }
    }

    internal static string Normalize(string? density)
    {
        if (string.Equals(density, Compact, StringComparison.OrdinalIgnoreCase)) return Compact;
        if (string.Equals(density, Comfortable, StringComparison.OrdinalIgnoreCase)) return Comfortable;
        return Standard;
    }

    private static void SaveDensity(string density)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new UiPreferences(density, CurrentFileVersion)));
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
                return Standard;
            }

            var prefs = JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath));
            return Migrate(prefs);
        }
        catch
        {
            return Standard;
        }
    }

    /// <summary>
    /// A file written before the three-level scale holds the old two-level names: its
    /// "Compact" was the only size the app had, which is what "Standard" is now, so it must
    /// not turn into the tighter layout on upgrade.
    /// </summary>
    internal static string Migrate(UiPreferences? prefs)
    {
        if (prefs is null)
        {
            return Standard;
        }

        if (prefs.Version < CurrentFileVersion
            && string.Equals(prefs.Density, Compact, StringComparison.OrdinalIgnoreCase))
        {
            return Standard;
        }

        return Normalize(prefs.Density);
    }

    internal sealed record UiPreferences(string Density, int Version = 1);
}
