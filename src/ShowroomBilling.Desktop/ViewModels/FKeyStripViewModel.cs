using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels;

/// <summary>One shortcut of the bottom bar: a key chip, optionally a second one, and what it does.</summary>
public sealed record FKeyItem(string Key, string Label, string? AltKey = null);

/// <summary>The shortcuts the bottom bar lists for the open screen.</summary>
public partial class FKeyStripViewModel : ObservableObject
{
    [ObservableProperty]
    private IReadOnlyList<FKeyItem> items = InvoiceKeys;

    public void SetTab(NavTab tab) => Items = tab switch
    {
        NavTab.Invoice => InvoiceKeys,
        NavTab.Bills => BillsKeys,
        NavTab.Settings => SettingsKeys,
        _ => InvoiceKeys
    };

    /// <summary>
    /// The keys for what is on screen: those of the tab, or, while the working-without-the-server
    /// page stands in for the Invoice and Bills tabs, the two that still do something there.
    /// </summary>
    public void SetScreen(NavTab tab, bool limited)
    {
        if (limited && tab != NavTab.Settings)
        {
            Items = LimitedKeys;
            return;
        }

        SetTab(tab);
    }

    private static readonly IReadOnlyList<FKeyItem> LimitedKeys =
    [
        new("Ctrl+3", "Settings"),
        new("?", "Shortcuts"),
    ];

    private static readonly IReadOnlyList<FKeyItem> InvoiceKeys =
    [
        new("Tab", "Next field"),
        new("Shift+Enter", "Back"),
        new("F3", "Quick add"),
        new("Ctrl+N", "New row"),
        new("Ctrl+Del", "Remove row"),
        new("F9", "Print estimate"),
        new("Ctrl+S", "Save"),
    ];

    private static readonly IReadOnlyList<FKeyItem> BillsKeys =
    [
        new("Enter", "Open details"),
        new("Ctrl+R", "Retry push"),
        new("Ctrl+Shift+R", "Repost"),
        new("Ctrl+Shift+E", "Edit"),
        new("Ctrl+P", "Print"),
        new("PgUp", "Page", "PgDn"),
    ];

    private static readonly IReadOnlyList<FKeyItem> SettingsKeys =
    [
        new("Ctrl+S", "Save"),
        new("Esc", "Cancel"),
        new("Tab", "Next field"),
        new("?", "Shortcuts"),
    ];
}
