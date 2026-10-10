namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Names of the overlay dialogs hosted in <c>MainWindow.xaml</c>. The shell's
/// <c>ActiveDialog</c> holds one of these (or null); each host compares against
/// its own name through <c>DialogVisibilityConverter</c> with
/// <c>ConverterParameter={x:Static shell:ShellDialogs.…}</c>.
/// </summary>
public static class ShellDialogs
{
    public const string Shortcuts = "shortcuts";
    public const string Health = "health";
    public const string BillDetails = "billDetails";
    public const string PrintPreview = "printPreview";
    public const string Admin = "admin";
    public const string SetupWizard = "setupWizard";
    public const string SyntheticBatch = "syntheticBatch";
    public const string ChangeNumber = "changeNumber";
    public const string ReasonPrompt = "reasonPrompt";
}
