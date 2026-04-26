using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Printing;

/// <summary>
/// Substitutes the per-line <see cref="BillLineItemDto.ItemName"/> with the Tally
/// stock-item name resolved via the karat master, mirroring V1's print pipeline
/// (services/print_manager._mapped_tally_item_name). Diamond lines and lines with
/// no unique karat-label match keep their program-side name.
/// </summary>
public static class PrintLineSubstitution
{
    public static IReadOnlyList<BillLineItemDto> WithTallyMappedItemNames(
        IReadOnlyList<BillLineItemDto> lines,
        IReadOnlyList<KaratMasterEntry>? karatMappings)
    {
        if (lines.Count == 0 || karatMappings is null || karatMappings.Count == 0)
        {
            return lines;
        }

        var result = new List<BillLineItemDto>(lines.Count);
        foreach (var line in lines)
        {
            var mapped = BillDocumentText.MapTallyItemName(line, karatMappings);
            result.Add(string.IsNullOrWhiteSpace(mapped) ? line : line with { ItemName = mapped! });
        }
        return result;
    }
}
