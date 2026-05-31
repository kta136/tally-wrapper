using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Printing;

/// <summary>
/// Substitutes the per-line <see cref="BillLineItemDto.ItemName"/> with the Tally
/// stock-item name for print. Primary source is <see cref="BillLineItemDto.StockName"/>,
/// which <c>InvoicePayloadMapper.ResolveStockName</c> set at save time from the karat /
/// item master (same field <c>TallyXmlVoucherBuilder</c> uses to talk to Tally). If a
/// line is missing <c>StockName</c> (older saved bills), fall back to the karat-master
/// snapshot lookup — V1 behaviour. Lines with neither source keep their original name.
/// </summary>
public static class PrintLineSubstitution
{
    public static IReadOnlyList<BillLineItemDto> WithTallyMappedItemNames(
        IReadOnlyList<BillLineItemDto> lines,
        IReadOnlyList<KaratMasterEntry>? karatMappings)
    {
        if (lines.Count == 0) return lines;

        var hasMappings = karatMappings is { Count: > 0 };
        var result = new List<BillLineItemDto>(lines.Count);
        foreach (var line in lines)
        {
            var mapped = !string.IsNullOrWhiteSpace(line.StockName)
                ? line.StockName!.Trim()
                : hasMappings
                    ? BillDocumentText.MapTallyItemName(line, karatMappings)
                    : null;

            result.Add(string.IsNullOrWhiteSpace(mapped) ? line : line with { ItemName = mapped! });
        }
        return result;
    }
}
