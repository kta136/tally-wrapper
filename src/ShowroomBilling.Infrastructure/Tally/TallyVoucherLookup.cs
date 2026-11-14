using System.Globalization;
using System.Xml.Linq;
using ShowroomBilling.Application.Settings;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// Asks Tally which sales vouchers carry a given voucher number. It is the
/// way to the voucher of a bill that no push from here ever created: one an
/// admin marked as pushed after entering or checking it in Tally by hand.
/// A read, so it goes through the retrying side of <see cref="ITallyXmlClient"/>;
/// a read that fails throws, and the caller decides how to report it.
/// </summary>
public interface ITallyVoucherLookup
{
    /// <summary>
    /// The Tally master ids of the vouchers of the configured sales voucher
    /// type numbered <paramref name="voucherNumber"/> and dated inside
    /// <paramref name="fiscalYear"/> (<c>2026-27</c>). Empty when there is none.
    /// </summary>
    Task<IReadOnlyList<string>> FindMasterIdsAsync(
        string voucherNumber,
        string fiscalYear,
        CancellationToken cancellationToken = default);
}

public sealed class TallyVoucherLookup(
    ITallyXmlClient xmlClient,
    ICloudSettingsService cloudSettings) : ITallyVoucherLookup
{
    private const string CollectionName = "TW_VouchersByNumber";
    private const string FilterName = "TW_VoucherNumberIs";

    public async Task<IReadOnlyList<string>> FindMasterIdsAsync(
        string voucherNumber,
        string fiscalYear,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voucherNumber);
        var number = voucherNumber.Trim();

        var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
        var company = settings.Settings.Connection.ActiveCompanyName;
        // The same default the voucher builder falls back to.
        var voucherType = string.IsNullOrWhiteSpace(settings.Settings.Ledgers.SalesVoucherType)
            ? "Sales"
            : settings.Settings.Ledgers.SalesVoucherType.Trim();

        var request = BuildRequest(number, voucherType, company, fiscalYear);
        var response = await xmlClient.SendAsync(request, cancellationToken);
        return ReadMasterIds(response, number, voucherType);
    }

    private static XElement BuildRequest(string voucherNumber, string voucherType, string? company, string fiscalYear)
    {
        // The number and the type go into a TDL formula between double quotes,
        // which has no way to carry a double quote of its own.
        if (voucherNumber.Contains('"') || voucherType.Contains('"'))
        {
            throw new InvalidOperationException(
                "A voucher number or voucher type that contains a double quote cannot be looked up in Tally.");
        }

        var (from, to) = PeriodOf(fiscalYear);
        var staticVariables = new XElement("STATICVARIABLES",
            new XElement("SVEXPORTFORMAT", "$$SysName:XML"),
            // A voucher collection covers only the period it is asked for.
            new XElement("SVFROMDATE", from.ToString("yyyyMMdd", CultureInfo.InvariantCulture)),
            new XElement("SVTODATE", to.ToString("yyyyMMdd", CultureInfo.InvariantCulture)));
        if (!string.IsNullOrWhiteSpace(company))
        {
            staticVariables.Add(new XElement("SVCURRENTCOMPANY", company));
        }

        return new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("VERSION", 1),
                new XElement("TALLYREQUEST", "Export"),
                new XElement("TYPE", "Collection"),
                new XElement("ID", CollectionName)),
            new XElement("BODY",
                new XElement("DESC",
                    staticVariables,
                    new XElement("TDL",
                        new XElement("TDLMESSAGE",
                            new XElement("COLLECTION",
                                new XAttribute("NAME", CollectionName),
                                new XElement("TYPE", "Voucher"),
                                new XElement("FETCH", "MasterID, VoucherNumber, VoucherTypeName, Date"),
                                new XElement("FILTER", FilterName)),
                            new XElement("SYSTEM",
                                new XAttribute("TYPE", "Formulae"),
                                new XAttribute("NAME", FilterName),
                                $"$VoucherNumber = \"{voucherNumber}\" AND $VoucherTypeName = \"{voucherType}\""))))));
    }

    /// <summary>
    /// Reads the answer without trusting that Tally applied the filter: a
    /// voucher counts only when its own number is the one asked for and its
    /// type, when the answer gives one, is the sales voucher type. A wrong or
    /// ignored filter then finds nothing rather than pointing an Alter at
    /// somebody else's voucher.
    /// </summary>
    private static IReadOnlyList<string> ReadMasterIds(XElement response, string voucherNumber, string voucherType)
    {
        var ids = new List<string>();
        foreach (var voucher in response.DescendantsAndSelf()
            .Where(x => string.Equals(x.Name.LocalName, "VOUCHER", StringComparison.OrdinalIgnoreCase)))
        {
            var number = ReadChild(voucher, "VOUCHERNUMBER");
            if (!string.Equals(number, voucherNumber, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var type = ReadChild(voucher, "VOUCHERTYPENAME") ?? voucher.Attribute("VCHTYPE")?.Value.Trim();
            if (!string.IsNullOrEmpty(type) && !string.Equals(type, voucherType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var masterId = TallyIds.NormalizePositiveInteger(ReadChild(voucher, "MASTERID"))
                ?? throw new InvalidOperationException(
                    $"Tally listed a voucher numbered {voucherNumber} without a usable master id.");
            if (!ids.Contains(masterId))
            {
                ids.Add(masterId);
            }
        }

        return ids;
    }

    /// <summary>1 April to 31 March of an Indian fiscal-year label such as <c>2026-27</c>.</summary>
    private static (DateOnly From, DateOnly To) PeriodOf(string fiscalYear)
    {
        var label = fiscalYear?.Trim() ?? string.Empty;
        if (label.Length < 4
            || !int.TryParse(label.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var startYear)
            || startYear is < 2000 or > 9998)
        {
            throw new InvalidOperationException($"'{fiscalYear}' is not a fiscal year such as 2026-27.");
        }

        return (new DateOnly(startYear, 4, 1), new DateOnly(startYear + 1, 3, 31));
    }

    private static string? ReadChild(XElement element, string localName)
    {
        var value = element.Elements()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
            ?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
