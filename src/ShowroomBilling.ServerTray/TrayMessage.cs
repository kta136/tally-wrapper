using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

internal sealed record TrayMessage(string Title, string? Details, Tone Tone)
{
    internal void Show(IWin32Window? owner) => TrayDialog.ShowMessage(owner, Tone, Title, Details);
}
