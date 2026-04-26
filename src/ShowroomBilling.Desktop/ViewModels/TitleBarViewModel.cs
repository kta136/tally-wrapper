using CommunityToolkit.Mvvm.ComponentModel;

namespace ShowroomBilling.Desktop.ViewModels;

public partial class TitleBarViewModel : ObservableObject
{
    [ObservableProperty]
    private string appName = "Showroom Billing";

    [ObservableProperty]
    private string version = "v1.0";

    [ObservableProperty]
    private string company = "—";

    [ObservableProperty]
    private string operatorName = "—";
}
