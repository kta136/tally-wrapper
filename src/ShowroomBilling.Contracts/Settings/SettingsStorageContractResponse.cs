namespace ShowroomBilling.Contracts.Settings;

public sealed record SettingsStorageRuleResponse(
    string Key,
    string Scope,
    string Description);

public sealed record SettingsStorageContractResponse(
    IReadOnlyList<SettingsStorageRuleResponse> LocalRules,
    IReadOnlyList<SettingsStorageRuleResponse> CloudRules,
    string Note);
