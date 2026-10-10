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
    private string operatorName = "—";
}
