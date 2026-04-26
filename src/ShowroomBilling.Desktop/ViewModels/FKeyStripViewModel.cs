using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels;

public sealed record FKeyItem(string Key, string Label);

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

    private static readonly IReadOnlyList<FKeyItem> InvoiceKeys =
    [
        new("F2", "Edit Rate"),
        new("F3", "Item Picker"),
        new("F4", "Party"),
        new("F9", "Est. Print"),
        new("Ctrl+S", "Save & Post"),
        new("Ctrl+N", "New Row"),
        new("Ctrl+Del", "Remove Row"),
        new("Esc", "Cancel"),
        new("?", "Shortcuts"),
    ];

    private static readonly IReadOnlyList<FKeyItem> BillsKeys =
    [
        new("Enter", "Details"),
        new("P", "Push Selected"),
        new("Ctrl+P", "Print Selected"),
        new("Ctrl+R", "Retry Selected"),
        new("F5", "Refresh"),
        new("PgDn", "Next Page"),
        new("PgUp", "Prev Page"),
        new("Ctrl+S", "Push (details)"),
        new("Ctrl+⇧+R", "Repost (details)"),
        new("Esc", "Close Dialog"),
    ];

    private static readonly IReadOnlyList<FKeyItem> SettingsKeys =
    [
        new("Ctrl+S", "Save"),
        new("Esc", "Cancel"),
        new("Tab", "Next Field"),
        new("?", "Shortcuts"),
    ];
}
