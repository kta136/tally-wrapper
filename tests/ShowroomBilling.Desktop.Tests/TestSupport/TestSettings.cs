using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

internal static class TestSettings
{
    public static EffectiveSettingsResponse Create(
        string printCompanyName,
        string itemsJson = "[]",
        string karatsJson = "[]",
        bool requiresInitialSetup = false) =>
        new(
            "cloud",
            "settings",
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Development Company"),
                new NumberingSettingsDto("DEV-", string.Empty, 4),
                new PrintSettingsDto(printCompanyName, null, null, null, null, "India", null, null, null, null, null, true, false, false, 11, 9),
                new LedgerMappingsDto("Sales", "Cash", "Card / UPI", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                new MasterDataSettingsDto(itemsJson, karatsJson)),
            [],
            [],
            DateTimeOffset.UtcNow,
            requiresInitialSetup);
}
