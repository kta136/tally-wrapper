using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class TitleBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string appName = "Tally Wrapper";

    // <Version> in the project file is the one place the number is written.
    [ObservableProperty]
    private string version = typeof(TitleBarViewModel).Assembly.GetName().Version is { } assemblyVersion
        ? $"v{assemblyVersion.Major}.{assemblyVersion.Minor}"
        : string.Empty;

    [ObservableProperty]
    private string company = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OperatorInitials))]
    private string operatorName = "—";

    /// <summary>The one or two capital letters in the operator's avatar.</summary>
    public string OperatorInitials => InitialsOf(OperatorName);

    internal static string InitialsOf(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "?";

        var parts = name.Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var letters = parts
            .Select(part => part.FirstOrDefault(char.IsLetterOrDigit))
            .Where(letter => letter != default)
            .ToArray();
        if (letters.Length >= 2) return new string([letters[0], letters[1]]).ToUpperInvariant();
        if (letters.Length == 0) return "?";

        // One word: its first two characters ("kk" is KK, "Meera" is ME).
        var word = new string(parts[0].Where(char.IsLetterOrDigit).Take(2).ToArray());
        return word.ToUpperInvariant();
    }
}
