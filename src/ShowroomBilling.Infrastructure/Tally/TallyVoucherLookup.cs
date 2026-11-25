using System.Globalization;
using System.Xml.Linq;
using ShowroomBilling.Application.Settings;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// A sales voucher Tally holds: its master id, the date it carries now, its
/// total (the party amount, without its sign) when Tally gave one, and the
/// company it was asked for in.
/// </summary>
public sealed record TallyVoucherRef(string MasterId, DateOnly Date, decimal? Amount = null, string? Company = null);

/// <summary>
/// Where a voucher read is asked, and so where an Alter built on it has to go:
/// the company and the sales voucher type Settings name.
/// </summary>
public sealed record TallyVoucherDestination(string? Company, string VoucherType);

/// <summary>
/// Asks Tally about the voucher an edited bill's push has to change. An Alter
/// names its voucher by master id and by the date the voucher has in Tally
/// now, and only Tally can say that date, so the voucher is read before every
/// Alter: by the master id a push from here recorded, or by number for a bill
/// no push from here ever created (one an admin marked as pushed after
/// entering or checking it in Tally by hand).
/// Reads go through the retrying side of <see cref="ITallyXmlClient"/>; a read
/// that fails throws, and the caller decides how to report it.
/// </summary>
public interface ITallyVoucherLookup
{
    /// <summary>
    /// The vouchers of the configured sales voucher type numbered
    /// <paramref name="voucherNumber"/> and dated inside
    /// <paramref name="fiscalYear"/> (<c>2026-27</c>). Empty when there is none.
    /// </summary>
    Task<IReadOnlyList<TallyVoucherRef>> FindByNumberAsync(
        string voucherNumber,
        string fiscalYear,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The voucher of the configured sales voucher type with master id
    /// <paramref name="masterId"/>, dated inside <paramref name="fiscalYear"/>,
    /// or null when Tally has no such voucher (deleted, or never there).
    /// </summary>
    Task<TallyVoucherRef?> FindByMasterIdAsync(
        string masterId,
        string fiscalYear,
        CancellationToken cancellationToken = default);

    /// <summary>The company and voucher type the reads above are asked in (Settings, not Tally).</summary>
    Task<TallyVoucherDestination> DestinationAsync(CancellationToken cancellationToken = default);
}

public sealed class TallyVoucherLookup(
    ITallyXmlClient xmlClient,
    ICloudSettingsService cloudSettings) : ITallyVoucherLookup
{
    private const string ByNumberCollection = "TW_VouchersByNumber";
    private const string ByNumberFilter = "TW_VoucherNumberIs";
    private const string ByIdCollection = "TW_VoucherById";
    private const string ByIdFilter = "TW_VoucherIdIs";

    public async Task<IReadOnlyList<TallyVoucherRef>> FindByNumberAsync(
        string voucherNumber,
        string fiscalYear,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(voucherNumber);
        var number = voucherNumber.Trim();
        var (company, voucherType) = await ReadSettingsAsync(cancellationToken);

        // The number and the type go into a TDL formula between double quotes,
        // which has no way to carry a double quote of its own.
        if (number.Contains('"') || voucherType.Contains('"'))
        {
            throw new InvalidOperationException(
                "A voucher number or voucher type that contains a double quote cannot be looked up in Tally.");
        }

        var period = PeriodOf(fiscalYear);
        var request = BuildRequest(
            ByNumberCollection,
            ByNumberFilter,
            $"$VoucherNumber = \"{number}\" AND $VoucherTypeName = \"{voucherType}\"",
            company,
            period);
        var response = await xmlClient.SendAsync(request, cancellationToken);
        return ReadVouchers(
            response,
            voucherType,
            period,
            company,
            voucher => string.Equals(ReadChild(voucher, "VOUCHERNUMBER"), number, StringComparison.OrdinalIgnoreCase),
            $"numbered {number}");
    }

    public async Task<TallyVoucherRef?> FindByMasterIdAsync(
        string masterId,
        string fiscalYear,
        CancellationToken cancellationToken = default)
    {
        var id = TallyIds.NormalizePositiveInteger(masterId)
            ?? throw new InvalidOperationException($"'{masterId}' is not a Tally master id.");
        var (company, voucherType) = await ReadSettingsAsync(cancellationToken);

        // A master id names one voucher within the company, so the filter is
        // the id alone; the type is checked on this side, which also lets a
        // type name that a TDL formula cannot carry be looked up.
        var period = PeriodOf(fiscalYear);
        var request = BuildRequest(ByIdCollection, ByIdFilter, $"$MasterID = {id}", company, period);
        var response = await xmlClient.SendAsync(request, cancellationToken);
        var found = ReadVouchers(
            response,
            voucherType,
            period,
            company,
            voucher => TallyIds.NormalizePositiveInteger(ReadChild(voucher, "MASTERID")) == id,
            $"with master id {id}");
        return found.Count == 0 ? null : found[0];
    }

    public async Task<TallyVoucherDestination> DestinationAsync(CancellationToken cancellationToken = default)
    {
        var (company, voucherType) = await ReadSettingsAsync(cancellationToken);
        return new TallyVoucherDestination(string.IsNullOrWhiteSpace(company) ? null : company.Trim(), voucherType);
    }

    private async Task<(string? Company, string VoucherType)> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
        var company = settings.Settings.Connection.ActiveCompanyName;
        return (company, TallyXmlVoucherBuilder.SalesVoucherTypeOf(settings.Settings.Ledgers));
    }

    private static XElement BuildRequest(
        string collectionName,
        string filterName,
        string filterFormula,
        string? company,
        (DateOnly From, DateOnly To) period)
    {
        var (from, to) = period;
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
                new XElement("ID", collectionName)),
            new XElement("BODY",
                new XElement("DESC",
                    staticVariables,
                    new XElement("TDL",
                        new XElement("TDLMESSAGE",
                            new XElement("COLLECTION",
                                new XAttribute("NAME", collectionName),
                                new XElement("TYPE", "Voucher"),
                                new XElement("FETCH", "MasterID, VoucherNumber, VoucherTypeName, Date, Amount"),
                                new XElement("FILTER", filterName)),
                            new XElement("SYSTEM",
                                new XAttribute("TYPE", "Formulae"),
                                new XAttribute("NAME", filterName),
                                filterFormula))))));
    }

    /// <summary>
    /// Reads the answer without trusting that Tally applied the filter or the
    /// period: a voucher counts only when <paramref name="isTheOneAskedFor"/>
    /// says so of its own fields, its type, when the answer gives one, is the
    /// sales voucher type, and its date is inside the period asked for. A
    /// wrong or ignored filter then finds nothing rather than pointing an
    /// Alter at somebody else's voucher. That check is also what passes over
    /// the <c>VOUCHER</c> element in the answer's <c>CMPINFO</c> block, which
    /// is a count and not a voucher. A voucher Tally keeps only as deleted
    /// (<c>ISDELETED</c>) is not one to change, so it does not count either.
    /// An answer that is not a collection at all (Tally refused or did not
    /// understand the request) is an error, never "no such voucher".
    /// </summary>
    private static IReadOnlyList<TallyVoucherRef> ReadVouchers(
        XElement response,
        string voucherType,
        (DateOnly From, DateOnly To) period,
        string? company,
        Func<XElement, bool> isTheOneAskedFor,
        string askedFor)
    {
        EnsureCollectionAnswer(response);

        var found = new List<TallyVoucherRef>();
        foreach (var voucher in response.DescendantsAndSelf()
            .Where(x => string.Equals(x.Name.LocalName, "VOUCHER", StringComparison.OrdinalIgnoreCase)))
        {
            if (!voucher.HasElements || !isTheOneAskedFor(voucher))
            {
                continue;
            }

            // The read by id does not filter on the type, so the type has to
            // be there to be checked; Tally always lists it.
            var type = ReadChild(voucher, "VOUCHERTYPENAME") ?? voucher.Attribute("VCHTYPE")?.Value.Trim();
            if (string.IsNullOrEmpty(type))
            {
                throw new InvalidOperationException(
                    $"Tally listed a voucher {askedFor} without its voucher type.");
            }

            if (!string.Equals(type, voucherType, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(ReadChild(voucher, "ISDELETED"), "Yes", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var masterId = TallyIds.NormalizePositiveInteger(ReadChild(voucher, "MASTERID"))
                ?? throw new InvalidOperationException(
                    $"Tally listed a voucher {askedFor} without a usable master id.");
            // The date is what an Alter has to name, so a voucher without one
            // cannot be changed from here.
            var dateText = ReadChild(voucher, "DATE");
            if (!DateOnly.TryParseExact(dateText, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                throw new InvalidOperationException(
                    $"Tally listed a voucher {askedFor} without a usable date ('{dateText}').");
            }

            if (date < period.From || date > period.To)
            {
                continue;
            }

            var amount = decimal.TryParse(
                ReadChild(voucher, "AMOUNT"),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var signed)
                ? Math.Abs(signed)
                : (decimal?)null;

            var same = found.Find(x => x.MasterId == masterId);
            if (same is null)
            {
                found.Add(new TallyVoucherRef(masterId, date, amount, string.IsNullOrWhiteSpace(company) ? null : company.Trim()));
            }
            else if (same.Date != date)
            {
                // Naming the wrong date makes Tally create a second voucher,
                // so one id with two dates cannot be acted on.
                throw new InvalidOperationException(
                    $"Tally listed the voucher with master id {masterId} twice, dated {same.Date:yyyy-MM-dd} and {date:yyyy-MM-dd}.");
            }
        }

        return found;
    }

    /// <summary>
    /// Tally answers a request it refused or did not understand with HTTP 200
    /// all the same (<c>STATUS</c> 0, or a bare <c>RESPONSE</c> such as
    /// "Unknown Request"). Such an answer has no voucher in it, and reading it
    /// as "no such voucher" would fail a push for a voucher Tally may well
    /// hold. TallyPrime's answer to a collection request carries the
    /// <c>COLLECTION</c> element even when nothing matched.
    /// </summary>
    private static void EnsureCollectionAnswer(XElement response)
    {
        var status = response.Elements()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, "HEADER", StringComparison.OrdinalIgnoreCase))
            ?.Elements()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, "STATUS", StringComparison.OrdinalIgnoreCase))
            ?.Value.Trim();
        var isCollection = response.DescendantsAndSelf()
            .Any(x => string.Equals(x.Name.LocalName, "COLLECTION", StringComparison.OrdinalIgnoreCase));
        if (status == "0" || !isCollection)
        {
            var text = response.Value.Trim();
            throw new InvalidOperationException(
                $"Tally did not answer the voucher read with a list of vouchers: '{(text.Length > 200 ? text[..200] : text)}'.");
        }
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
