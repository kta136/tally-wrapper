using System.Text.Json;
using System.Xml.Linq;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Masters;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// Fetches master snapshots (companies, ledgers, stock items, voucher types) from Tally
/// via XML queries. Called only when an operator clicks a "Refresh from Tally" button —
/// there is no background polling. A read that fails throws; callers decide how to
/// report a timeout, a refused connection or missing settings.
/// </summary>
public interface ITallyMasterSource
{
    Task<IReadOnlyList<CompanySnapshotItem>> FetchCompaniesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LedgerSnapshotItem>> FetchLedgersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StockItemSnapshotItem>> FetchStockItemsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<VoucherTypeSnapshotItem>> FetchVoucherTypesAsync(CancellationToken cancellationToken = default);
}

public sealed class TallyXmlMasterSource(
    ITallyXmlClient xmlClient,
    ICloudSettingsService cloudSettings) : ITallyMasterSource
{
    private const string CollectionCompany = "TW_Companies";
    private const string CollectionLedger = "TW_Ledgers";
    private const string CollectionStockItem = "TW_StockItems";
    private const string CollectionVoucherType = "TW_VoucherTypes";

    public Task<IReadOnlyList<CompanySnapshotItem>> FetchCompaniesAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(
            CollectionCompany, tallyType: "Company", fetches: ["Name", "IsInactive"],
            includeCompanyScope: false, "COMPANY", MapCompany, cancellationToken);

    public Task<IReadOnlyList<LedgerSnapshotItem>> FetchLedgersAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(
            CollectionLedger, tallyType: "Ledger", fetches: ["Name", "Parent", "PrimaryGroup", "IsRevenue", "PartyGSTIN"],
            includeCompanyScope: true, "LEDGER", MapLedger, cancellationToken);

    public Task<IReadOnlyList<StockItemSnapshotItem>> FetchStockItemsAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(
            CollectionStockItem, tallyType: "Stock Item", fetches: ["Name", "AliasName", "BaseUnits", "GSTHSNCode"],
            includeCompanyScope: true, "STOCKITEM", MapStockItem, cancellationToken);

    public Task<IReadOnlyList<VoucherTypeSnapshotItem>> FetchVoucherTypesAsync(CancellationToken cancellationToken = default) =>
        FetchAsync(
            CollectionVoucherType, tallyType: "Voucher Type", fetches: ["Name", "Parent", "IsDeemedPositive"],
            includeCompanyScope: true, "VOUCHERTYPE", MapVoucherType, cancellationToken);

    private async Task<XElement> BuildCollectionRequest(
        string collectionName,
        string tallyType,
        string[] fetches,
        bool includeCompanyScope,
        CancellationToken cancellationToken)
    {
        var staticVariables = new XElement("STATICVARIABLES",
            new XElement("SVEXPORTFORMAT", "$$SysName:XML"));
        if (includeCompanyScope)
        {
            var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
            var company = settings.Settings.Connection.ActiveCompanyName;
            if (!string.IsNullOrWhiteSpace(company))
            {
                staticVariables.Add(new XElement("SVCURRENTCOMPANY", company));
            }
        }

        return new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("VERSION", 1),
                new XElement("TALLYREQUEST", "Export"),
                new XElement("TYPE", "Collection"),
                new XElement("ID", collectionName)),
            new XElement("BODY",
                new XElement("DESC",
                    staticVariables,
                    new XElement("TDL",
                        new XElement("TDLMESSAGE",
                            new XElement("COLLECTION",
                                new XAttribute("NAME", collectionName),
                                new XElement("TYPE", tallyType),
                                new XElement("FETCH", string.Join(", ", fetches))))))));
    }

    private async Task<IReadOnlyList<T>> FetchAsync<T>(
        string collectionName,
        string tallyType,
        string[] fetches,
        bool includeCompanyScope,
        string elementName,
        Func<XElement, T?> map,
        CancellationToken cancellationToken)
    {
        var request = await BuildCollectionRequest(collectionName, tallyType, fetches, includeCompanyScope, cancellationToken);
        var response = await xmlClient.SendAsync(request, cancellationToken);
        return ParseCollection(response, elementName, map);
    }

    private static IReadOnlyList<T> ParseCollection<T>(XElement response, string elementName, Func<XElement, T?> map)
    {
        var items = new List<T>();
        foreach (var element in response.DescendantsAndSelf()
            .Where(x => string.Equals(x.Name.LocalName, elementName, StringComparison.OrdinalIgnoreCase)))
        {
            var item = map(element);
            if (item is not null) items.Add(item);
        }
        return items;
    }

    private static CompanySnapshotItem? MapCompany(XElement element)
    {
        var name = ReadNameFromAttrOrChild(element);
        if (string.IsNullOrWhiteSpace(name)) return null;
        var inactive = ReadBool(element, "ISINACTIVE", defaultValue: false);
        return new CompanySnapshotItem(Name: name!, IsActive: !inactive, RawJson: ToRawJson(element));
    }

    private static LedgerSnapshotItem? MapLedger(XElement element)
    {
        var name = ReadNameFromAttrOrChild(element);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new LedgerSnapshotItem(
            Name: name!,
            Parent: ReadChild(element, "PARENT"),
            PrimaryGroup: ReadChild(element, "PRIMARYGROUP"),
            IsRevenue: ReadBool(element, "ISREVENUE", defaultValue: false),
            Gstin: ReadChild(element, "PARTYGSTIN"),
            RawJson: ToRawJson(element));
    }

    private static StockItemSnapshotItem? MapStockItem(XElement element)
    {
        var name = ReadNameFromAttrOrChild(element);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new StockItemSnapshotItem(
            Name: name!,
            Alias: ReadChild(element, "ALIASNAME"),
            BaseUnit: ReadChild(element, "BASEUNITS"),
            HsnCode: SanitizeHsn(ReadChild(element, "GSTHSNCODE")) ?? SanitizeHsn(ReadChild(element, "HSNCODE")),
            Karat: null,
            RawJson: ToRawJson(element));
    }

    private static VoucherTypeSnapshotItem? MapVoucherType(XElement element)
    {
        var name = ReadNameFromAttrOrChild(element);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new VoucherTypeSnapshotItem(
            Name: name!,
            ParentType: ReadChild(element, "PARENT"),
            IsDeemedPositive: ReadBool(element, "ISDEEMEDPOSITIVE", defaultValue: false),
            RawJson: ToRawJson(element));
    }

    private static string? ReadNameFromAttrOrChild(XElement element)
    {
        var attr = element.Attribute("NAME")?.Value;
        if (!string.IsNullOrWhiteSpace(attr)) return attr.Trim();
        var child = ReadChild(element, "NAME");
        return string.IsNullOrWhiteSpace(child) ? null : child.Trim();
    }

    private static string? ReadChild(XElement element, string localName)
    {
        var match = element.Elements()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        var value = match?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string? SanitizeHsn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Equals("Not Found", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static bool ReadBool(XElement element, string localName, bool defaultValue)
    {
        var value = ReadChild(element, localName);
        if (string.IsNullOrWhiteSpace(value)) return defaultValue;
        return value!.Equals("Yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("True", StringComparison.OrdinalIgnoreCase)
            || value == "1";
    }

    private static string ToRawJson(XElement element) =>
        JsonSerializer.Serialize(new { xml = element.ToString(SaveOptions.DisableFormatting) });
}
