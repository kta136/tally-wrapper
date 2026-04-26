using System.Text.Json;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public interface ICompanyProfileProvider
{
    CompanyProfile Current { get; }

    /// <summary>
    /// Karat master snapshot used by the print pipeline to resolve the Tally
    /// stock-item name for non-diamond lines. Empty if no mapping is configured.
    /// </summary>
    IReadOnlyList<KaratMasterEntry> KaratMappings { get; }

    /// <summary>Copy settings from <paramref name="print"/> into the current profile.</summary>
    void Apply(PrintSettingsDto print);

    /// <summary>Refresh the karat-master snapshot from cloud settings JSON.</summary>
    void ApplyMasters(MasterDataSettingsDto masters);
}

public sealed class CompanyProfileProvider : ICompanyProfileProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Safe placeholder used until InitializeAsync() pulls real settings from the API.
    // Empty strings render as "—" in the invoice and are obviously unset at a glance.
    public CompanyProfile Current { get; private set; } = CreateDefault();

    public IReadOnlyList<KaratMasterEntry> KaratMappings { get; private set; } = Array.Empty<KaratMasterEntry>();

    public void Apply(PrintSettingsDto print) => Current = PrintProfileMapping.ToCompanyProfile(print);

    public void ApplyMasters(MasterDataSettingsDto masters)
    {
        ArgumentNullException.ThrowIfNull(masters);
        KaratMappings = ParseKaratMappings(masters.KaratMappingDataJson);
    }

    private static IReadOnlyList<KaratMasterEntry> ParseKaratMappings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KaratMasterEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<KaratMasterEntry>>(json, JsonOptions)
                ?? (IReadOnlyList<KaratMasterEntry>)Array.Empty<KaratMasterEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<KaratMasterEntry>();
        }
    }

    private static CompanyProfile CreateDefault() => new(
        Name: "—",
        Address: null,
        Phone: null,
        State: null,
        Country: null,
        Gstin: string.Empty,
        Huid: null,
        BankName: null,
        BankAccount: null,
        BankIfsc: null,
        BankUpi: null,
        TermsAndConditions: null);
}
