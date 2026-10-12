using System;
using System.Globalization;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

/// <summary>Who a settings section's values belong to, which is also how the sidebar groups them.</summary>
public enum SettingsSectionScope
{
    /// <summary>Stored on the server and the same at every counter.</summary>
    Shared,

    /// <summary>Stored on this PC only.</summary>
    ThisPc,

    /// <summary>Visible only while an admin session is unlocked.</summary>
    AdminOnly,
}

/// <summary>
/// What the Settings screen says about a section: its scope, its title, and the heading of the group
/// it sits under in the sidebar. The scope follows docs/14_settings_storage_contract.md.
/// </summary>
public static class SettingsSections
{
    public static SettingsSectionScope ScopeOf(SettingsSectionKey section) => section switch
    {
        SettingsSectionKey.Server or SettingsSectionKey.Advanced => SettingsSectionScope.ThisPc,
        SettingsSectionKey.Admin => SettingsSectionScope.AdminOnly,
        _ => SettingsSectionScope.Shared,
    };

    public static string TitleOf(SettingsSectionKey section) => section switch
    {
        SettingsSectionKey.PrintLayout => "Print layout",
        _ => section.ToString(),
    };

    /// <summary>The words on the chip beside a section's title.</summary>
    public static string ChipTextOf(SettingsSectionScope scope) => scope switch
    {
        SettingsSectionScope.Shared => "Shared by every counter",
        SettingsSectionScope.ThisPc => "This PC only",
        _ => "Admin only",
    };

    /// <summary>The small heading over a group of sidebar items.</summary>
    public static string GroupHeadingOf(SettingsSectionScope scope) => scope switch
    {
        SettingsSectionScope.Shared => "Shared by every counter",
        SettingsSectionScope.ThisPc => "This PC only",
        _ => "Admin, unlocked",
    };

    /// <summary>True for the sections that are edited as one draft and saved together: they get Refresh, Edit, Discard and Save all.</summary>
    public static bool IsEditedAsDraft(SettingsSectionKey section) =>
        section is SettingsSectionKey.Connection or SettingsSectionKey.Invoice
            or SettingsSectionKey.Ledgers or SettingsSectionKey.Masters;
}

/// <summary>Groups the sidebar's sections by scope; the group's name is the heading shown over it.</summary>
public sealed class SettingsSectionGroupConverter : IValueConverter
{
    public static SettingsSectionGroupConverter Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SettingsSectionKey section
            ? SettingsSections.GroupHeadingOf(SettingsSections.ScopeOf(section))
            : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
