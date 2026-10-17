namespace ShowroomBilling.Desktop.Shell;

/// <summary>One line of the shortcut list: what the keys do, and the keys.</summary>
/// <param name="Action">What happens, in sentence case.</param>
/// <param name="Keys">One chip per entry; a pair such as Tab and Enter shows two.</param>
/// <param name="AdminOnly">Only works with an admin session; shown with a lock.</param>
public sealed record ShortcutRow(string Action, IReadOnlyList<string> Keys, bool AdminOnly = false);

/// <summary>
/// The shortcuts the Shortcuts dialog lists, three columns of them. The keys themselves are bound
/// where they work (<c>MainWindow.xaml</c>, <c>InvoiceView</c>, <c>BillsView</c>); this is only what the
/// operator reads, so a key that is added or changed there is added or changed here.
/// </summary>
public static class ShortcutCatalog
{
    private static ShortcutRow Row(string action, params string[] keys) => new(action, keys);

    public static IReadOnlyList<ShortcutRow> MoveAround { get; } =
    [
        Row("Go to Invoice", "Ctrl+1"),
        Row("Go to Bills", "Ctrl+2"),
        Row("Go to Settings", "Ctrl+3"),
        Row("Open this list", "?"),
        Row("Close a dialog, or cancel", "Esc"),
        Row("Unlock admin", "~"),
    ];

    public static IReadOnlyList<ShortcutRow> InvoiceEntry { get; } =
    [
        Row("24K rate", "F2"),
        Row("Quick add", "F3"),
        Row("Party", "F4"),
        Row("Next field", "Tab", "Enter"),
        Row("Previous field", "Shift+Tab"),
        Row("New row", "Ctrl+N"),
        Row("Remove row", "Ctrl+Del"),
        Row("Print estimate", "F9"),
        Row("Save bill", "Ctrl+S"),
        Row("New bill", "Ctrl+Shift+N"),
    ];

    public static IReadOnlyList<ShortcutRow> Bills { get; } =
    [
        Row("Open details", "Enter"),
        Row("Retry push", "Ctrl+R"),
        Row("Repost to Tally", "Ctrl+Shift+R"),
        Row("Edit", "Ctrl+Shift+E"),
        Row("Print", "Ctrl+P"),
        Row("Refresh", "F5"),
        Row("Next or previous page", "PgDn", "PgUp"),
        new ShortcutRow("Delete selected, admin", ["Shift+Del"], AdminOnly: true),
    ];
}
