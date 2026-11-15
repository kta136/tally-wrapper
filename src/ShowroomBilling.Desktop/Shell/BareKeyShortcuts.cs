using System.Windows.Input;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// The two shortcuts that are a plain character: <c>?</c> for the shortcuts list and the
/// backtick for admin unlock. They must not be <c>KeyBinding</c>s. A key binding marks the key
/// handled whether or not its command may run, and a handled key types nothing, so neither
/// character could be typed into any text box: not in a party name, a note or a reason.
/// The window calls this from its key handler instead, and takes the key only when it ran.
/// </summary>
public static class BareKeyShortcuts
{
    /// <summary>
    /// Runs the shortcut <paramref name="key"/> stands for, if it may run now. False means the
    /// key is not a shortcut at this moment (a text box has the cursor, or a dialog is open):
    /// leave it unhandled, so the character is typed.
    /// </summary>
    public static bool TryRun(Key key, ModifierKeys modifiers, ICommand shortcuts, ICommand adminUnlock)
    {
        var command = (key, modifiers) switch
        {
            (Key.OemQuestion, ModifierKeys.Shift) => shortcuts,
            (Key.OemTilde, ModifierKeys.None) => adminUnlock,
            _ => null,
        };
        if (command is null || !command.CanExecute(null))
        {
            return false;
        }

        command.Execute(null);
        return true;
    }
}
