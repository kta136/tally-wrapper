namespace ShowroomBilling.Application.Settings;

public enum SettingStorageScope
{
    LocalBootstrap,
    LocalRuntime,
    LocalSecret,
    LocalPreference,
    CloudShared
}

public sealed record SettingStorageRule(
    string Key,
    SettingStorageScope Scope,
    string Description);

public interface ISettingsStorageContract
{
    IReadOnlyList<SettingStorageRule> Rules { get; }
    IReadOnlyList<SettingStorageRule> LocalRules { get; }
    IReadOnlyList<SettingStorageRule> CloudRules { get; }
    bool IsCloudOwned(string key);
}

public sealed class SettingsStorageContract : ISettingsStorageContract
{
    private static readonly IReadOnlyList<SettingStorageRule> KnownRules =
    [
        new("desktop.bootstrap.api_base_url", SettingStorageScope.LocalBootstrap, "Desktop API bootstrap endpoint."),
        new("desktop.bootstrap.device_id", SettingStorageScope.LocalBootstrap, "Desktop workstation identity."),
        new("desktop.runtime.counter_name", SettingStorageScope.LocalRuntime, "Workstation-local counter label."),
        new("desktop.preference.preferred_printer_name", SettingStorageScope.LocalPreference, "Printer choice stays local to the workstation."),
        new("desktop.preference.last_pdf_directory", SettingStorageScope.LocalPreference, "Recent PDF export path stays local."),
        new("connection.host", SettingStorageScope.CloudShared, "Tally connection host is shared operational behavior."),
        new("connection.port", SettingStorageScope.CloudShared, "Tally connection port is shared operational behavior."),
        new("connection.timeout_seconds", SettingStorageScope.CloudShared, "Tally timeout affects shared behavior."),
        new("connection.company_name", SettingStorageScope.CloudShared, "Active Tally company must stay consistent across counters."),
        new("numbering.*", SettingStorageScope.CloudShared, "Numbering is server-owned and cannot live locally."),
        new("print.*", SettingStorageScope.CloudShared, "Shared invoice layout and printing rules are backend-owned."),
        new("ledgers.*", SettingStorageScope.CloudShared, "Ledger mappings affect shared business posting behavior."),
        new("voucher_types.*", SettingStorageScope.CloudShared, "Voucher type mappings affect shared business posting behavior."),
        new("item_master.*", SettingStorageScope.CloudShared, "Item master data is shared business data."),
        new("karat_mapping.*", SettingStorageScope.CloudShared, "Karat/stock mappings are shared business data."),
        new("admin.*", SettingStorageScope.CloudShared, "Admin-operational settings are backend-owned.")
    ];

    public IReadOnlyList<SettingStorageRule> Rules => KnownRules;

    public IReadOnlyList<SettingStorageRule> LocalRules =>
        KnownRules.Where(rule => rule.Scope != SettingStorageScope.CloudShared).ToArray();

    public IReadOnlyList<SettingStorageRule> CloudRules =>
        KnownRules.Where(rule => rule.Scope == SettingStorageScope.CloudShared).ToArray();

    public bool IsCloudOwned(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        return CloudRules.Any(rule => Matches(rule.Key, key));
    }

    private static bool Matches(string pattern, string candidate)
    {
        if (!pattern.Contains('*', StringComparison.Ordinal))
        {
            return string.Equals(pattern, candidate, StringComparison.OrdinalIgnoreCase);
        }

        var prefix = pattern[..pattern.IndexOf('*')];
        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
