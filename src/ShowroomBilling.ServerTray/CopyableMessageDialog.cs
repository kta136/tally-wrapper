using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.ServerTray;

/// <summary>
/// Every message the server shows: what happened as the title, the rest in a
/// box that can be copied, so an error can be sent on as text.
/// </summary>
internal static class CopyableMessageDialog
{
    /// <param name="owner">The window it belongs to; null centres it on the screen.</param>
    /// <param name="message">Its first line becomes the title, what follows the details.</param>
    /// <param name="icon">Error, Warning, or anything else for something that went well.</param>
    public static void ShowMessage(IWin32Window? owner, string message, MessageBoxIcon icon)
    {
        var (title, details) = Split(message);
        TrayDialog.ShowMessage(owner, ToneOf(icon), title, details);
    }

    internal static (string Title, string? Details) Split(string message)
    {
        var text = message.ReplaceLineEndings("\n").Trim();
        var firstBreak = text.IndexOf('\n');
        if (firstBreak < 0)
        {
            return (text, null);
        }

        var details = text[(firstBreak + 1)..].Trim();
        return (text[..firstBreak].Trim(), details.Length == 0 ? null : details);
    }

    private static Tone ToneOf(MessageBoxIcon icon) => icon switch
    {
        MessageBoxIcon.Error => Tone.Err,
        MessageBoxIcon.Warning => Tone.Warn,
        _ => Tone.Ok,
    };
}
