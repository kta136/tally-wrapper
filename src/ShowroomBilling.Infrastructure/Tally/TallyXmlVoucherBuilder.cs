using System.Globalization;
using System.Xml.Linq;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Infrastructure.Tally;

public static class TallyXmlVoucherBuilder
{
    public static XElement Build(
        TallyPostRequest request,
        LedgerMappingsDto ledgers,
        string companyName)
    {
        var bill = request.Payload;

        // V1 parity (tally/xml_builder.py): the Tally party ledger is resolved from the
        // payment mode via the operator-configured ledger map, not from the free-text
        // customer name. bill.PartyName is print-only; it flows into NARRATION below.
        if (string.IsNullOrWhiteSpace(bill.Payment))
        {
            throw new VoucherBuildException(
                "MISSING_PAYMENT_MODE",
                "Bill has no payment mode; cannot resolve a Tally party ledger.",
                terminal: true);
        }
        var paymentMode = PaymentMode.Normalize(bill.Payment);
        var partyLedger = paymentMode == PaymentMode.Cash ? ledgers.CashLedger : ledgers.CreditDebitLedger;
        if (string.IsNullOrWhiteSpace(partyLedger))
        {
            throw new VoucherBuildException(
                paymentMode == PaymentMode.Cash ? "CONFIG_MISSING_CASH_LEDGER" : "CONFIG_MISSING_CREDIT_DEBIT_LEDGER",
                $"No Tally ledger is mapped for payment mode '{paymentMode}' in Settings.",
                terminal: true);
        }
        if (bill.Lines.Count == 0)
        {
            throw new VoucherBuildException(
                "NO_LINES",
                "Bill has zero line items; cannot create a Tally voucher.",
                terminal: true);
        }
        if (string.IsNullOrWhiteSpace(ledgers.SalesLedger))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_SALES_LEDGER",
                "Sales ledger is not mapped in Settings.",
                terminal: true);
        }
        if (string.IsNullOrWhiteSpace(companyName))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_COMPANY",
                "No active Tally company configured in Settings.",
                terminal: true);
        }

        var taxTotal = bill.Totals.TaxTotal;
        var cgst = Math.Round(taxTotal / 2m, 2);
        var sgst = taxTotal - cgst;

        if (cgst != 0m && string.IsNullOrWhiteSpace(ledgers.CgstLedger))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_CGST_LEDGER",
                "Bill has CGST but no CGST ledger is mapped in Settings.",
                terminal: true);
        }
        if (sgst != 0m && string.IsNullOrWhiteSpace(ledgers.SgstLedger))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_SGST_LEDGER",
                "Bill has SGST but no SGST ledger is mapped in Settings.",
                terminal: true);
        }
        if (bill.Totals.RoundOff != 0m && string.IsNullOrWhiteSpace(ledgers.RoundOffLedger))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_ROUNDOFF_LEDGER",
                "Bill has a non-zero round-off but no round-off ledger is mapped in Settings.",
                terminal: true);
        }
        if (bill.Totals.DiscountTotal != 0m && string.IsNullOrWhiteSpace(ledgers.DiscountLedger))
        {
            throw new VoucherBuildException(
                "CONFIG_MISSING_DISCOUNT_LEDGER",
                "Bill has a non-zero discount but no discount ledger is mapped in Settings.",
                terminal: true);
        }

        var voucherType = string.IsNullOrWhiteSpace(ledgers.SalesVoucherType) ? "Sales" : ledgers.SalesVoucherType;

        var voucherAttributes = new List<XAttribute>();
        if (request.Operation == TallyPostOperation.Alter)
        {
            if (string.IsNullOrWhiteSpace(request.TargetTagName) || string.IsNullOrWhiteSpace(request.TargetTagValue))
            {
                throw new VoucherBuildException(
                    "TALLY_ALTER_TARGET_MISSING",
                    "Edited bill cannot alter Tally because the original voucher identifier is missing.",
                    terminal: true);
            }

            voucherAttributes.Add(new XAttribute("VCHTYPE", voucherType));
            voucherAttributes.Add(new XAttribute("ACTION", "Alter"));
            voucherAttributes.Add(new XAttribute("TAGNAME", request.TargetTagName.Trim()));
            voucherAttributes.Add(new XAttribute("TAGVALUE", request.TargetTagValue.Trim()));
        }
        else
        {
            voucherAttributes.Add(new XAttribute("REMOTEID", request.IdempotencyKey));
            voucherAttributes.Add(new XAttribute("VCHTYPE", voucherType));
            voucherAttributes.Add(new XAttribute("ACTION", "Create"));
        }

        var voucherElement = new XElement("VOUCHER",
            voucherAttributes,
            new XElement("DATE", FormatTallyDate(bill.BillDate)),
            new XElement("EFFECTIVEDATE", FormatTallyDate(bill.BillDate)),
            new XElement("VOUCHERTYPENAME", voucherType));

        if (!string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            voucherElement.Add(new XElement("VOUCHERNUMBER", request.InvoiceNumber));
        }
        voucherElement.Add(new XElement("PARTYLEDGERNAME", partyLedger));
        voucherElement.Add(new XElement("ISINVOICE", "Yes"));
        // V1 parity: NARRATION carries the free-text customer label and operator notes,
        // joined by " | ". The party-ledger element above already identifies the Tally
        // ledger; this is purely human-readable context.
        var narrationParts = new[] { bill.PartyName?.Trim(), bill.Notes?.Trim() }
            .Where(p => !string.IsNullOrEmpty(p))
            .ToArray();
        if (narrationParts.Length > 0)
        {
            voucherElement.Add(new XElement("NARRATION", string.Join(" | ", narrationParts)));
        }

        // Tally voucher-XML sign convention (Accounting Invoice view):
        //   Dr legs (party receiving, discount expense) -> AMOUNT is NEGATIVE
        //   Cr legs (sales, CGST, SGST, round-off income) -> AMOUNT is POSITIVE
        //   Round-off sign preserved as-is: positive => Cr, negative => Dr
        voucherElement.Add(BuildLedgerEntry(partyLedger, -bill.Totals.GrandTotal, deemedPositive: true));

        var grossSum = bill.Lines.Sum(l => l.LineTotal);
        var netLineAmounts = AllocateNetAmounts(bill.Lines, grossSum, bill.Totals.Subtotal);
        var salesEntry = BuildLedgerEntry(ledgers.SalesLedger, bill.Totals.Subtotal, deemedPositive: false);
        for (var i = 0; i < bill.Lines.Count; i++)
        {
            salesEntry.Add(BuildInventoryAllocation(bill.Lines[i], netLineAmounts[i]));
        }
        voucherElement.Add(salesEntry);

        if (cgst != 0m)
        {
            voucherElement.Add(BuildLedgerEntry(ledgers.CgstLedger!, cgst, deemedPositive: false));
        }
        if (sgst != 0m)
        {
            voucherElement.Add(BuildLedgerEntry(ledgers.SgstLedger!, sgst, deemedPositive: false));
        }
        if (bill.Totals.DiscountTotal != 0m)
        {
            voucherElement.Add(BuildLedgerEntry(ledgers.DiscountLedger!, -bill.Totals.DiscountTotal, deemedPositive: false));
        }
        if (bill.Totals.RoundOff != 0m)
        {
            voucherElement.Add(BuildLedgerEntry(ledgers.RoundOffLedger!, bill.Totals.RoundOff, deemedPositive: false));
        }

        return new XElement("ENVELOPE",
            new XElement("HEADER",
                new XElement("TALLYREQUEST", "Import Data")),
            new XElement("BODY",
                new XElement("IMPORTDATA",
                    new XElement("REQUESTDESC",
                        new XElement("REPORTNAME", "Vouchers"),
                        new XElement("STATICVARIABLES",
                            new XElement("SVCURRENTCOMPANY", companyName))),
                    new XElement("REQUESTDATA",
                        new XElement("TALLYMESSAGE",
                            new XAttribute(XNamespace.Xmlns + "UDF", "TallyUDF"),
                            voucherElement)))));
    }

    private static XElement BuildLedgerEntry(string ledgerName, decimal amount, bool deemedPositive) =>
        new("ALLLEDGERENTRIES.LIST",
            new XElement("LEDGERNAME", ledgerName),
            new XElement("ISDEEMEDPOSITIVE", deemedPositive ? "Yes" : "No"),
            new XElement("AMOUNT", FormatAmount(amount)));

    private static XElement BuildInventoryAllocation(BillLineItemDto line, decimal netAmount)
    {
        var qtyText = FormatQuantity(line.Quantity, line.Unit);
        var perUnitRate = line.Quantity == 0m ? 0m : netAmount / line.Quantity;
        var rateText = FormatRate(perUnitRate, line.Unit);
        var stockName = string.IsNullOrWhiteSpace(line.StockName) ? line.ItemName : line.StockName!;

        return new XElement("INVENTORYALLOCATIONS.LIST",
            new XElement("STOCKITEMNAME", stockName),
            new XElement("ISDEEMEDPOSITIVE", "No"),
            new XElement("RATE", rateText),
            new XElement("AMOUNT", FormatAmount(netAmount)),
            new XElement("ACTUALQTY", qtyText),
            new XElement("BILLEDQTY", qtyText));
    }

    private static decimal[] AllocateNetAmounts(IReadOnlyList<BillLineItemDto> lines, decimal grossSum, decimal netTotal)
    {
        var result = new decimal[lines.Count];
        if (lines.Count == 0) return result;
        if (grossSum == 0m)
        {
            result[^1] = netTotal;
            return result;
        }

        decimal running = 0m;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            var share = Math.Round(lines[i].LineTotal / grossSum * netTotal, 2, MidpointRounding.AwayFromZero);
            result[i] = share;
            running += share;
        }
        result[^1] = netTotal - running;
        return result;
    }

    private static string FormatTallyDate(DateOnly date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string FormatAmount(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatQuantity(decimal qty, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? qty.ToString("0.###", CultureInfo.InvariantCulture)
            : $"{qty.ToString("0.###", CultureInfo.InvariantCulture)} {unit.Trim()}";

    private static string FormatRate(decimal rate, string? unit) =>
        string.IsNullOrWhiteSpace(unit)
            ? rate.ToString("0.##", CultureInfo.InvariantCulture)
            : $"{rate.ToString("0.##", CultureInfo.InvariantCulture)}/{unit.Trim()}";
}

public sealed class VoucherBuildException(string errorCode, string message, bool terminal)
    : Exception(message)
{
    public string ErrorCode { get; } = errorCode;
    public bool Terminal { get; } = terminal;
}
