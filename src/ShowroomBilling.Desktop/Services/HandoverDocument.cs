using System.Text.Json.Serialization;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// The JSON a hand-over link carries, version 1 or 2 (docs/18_bill_handover.md); version 2 adds a
/// bill's final amount. The property names are the contract's. A <c>required</c> property must be in
/// the JSON; <see cref="HandoverLinkReader"/> reads it and checks every limit before anything is saved.
/// </summary>
public sealed record HandoverDocument
{
    [JsonPropertyName("v")] public required int V { get; init; }

    [JsonPropertyName("issuedAt")] public required DateTimeOffset IssuedAt { get; init; }

    [JsonPropertyName("bills")] public required IReadOnlyList<HandoverBill> Bills { get; init; }

    /// <summary>Save IDs an earlier hand-over of the same source made that this one no longer has.</summary>
    [JsonPropertyName("notSent")] public IReadOnlyList<Guid>? NotSent { get; init; }
}

/// <summary>One sale bill to save as an ordinary bill.</summary>
public sealed record HandoverBill
{
    /// <summary>Stays the same each time the same bill is handed over again; saved as the bill's create operation id.</summary>
    [JsonPropertyName("saveId")] public required Guid SaveId { get; init; }

    [JsonPropertyName("partyName")] public required string PartyName { get; init; }

    [JsonPropertyName("partyPhone")] public string? PartyPhone { get; init; }

    [JsonPropertyName("partyAddress")] public string? PartyAddress { get; init; }

    [JsonPropertyName("billDate")] public required DateOnly BillDate { get; init; }

    [JsonPropertyName("payment")] public required string Payment { get; init; }

    [JsonPropertyName("lines")] public required IReadOnlyList<HandoverLine> Lines { get; init; }

    /// <summary>
    /// Version 2: what the bill is to come to, in whole rupees, or null for the bill as its lines come.
    /// The rest comes off as the bill's discount, as the Invoice tab's Final amount box takes it off.
    /// </summary>
    [JsonPropertyName("finalAmount")] public decimal? FinalAmount { get; init; }
}

/// <summary>A sale line. Weights are grams; money is rupees for the whole line; <see cref="LineTotal"/> has GST in it and the discount off.</summary>
public sealed record HandoverLine
{
    [JsonPropertyName("itemName")] public required string ItemName { get; init; }

    /// <summary>A karat label, or null for a line with no stamp.</summary>
    [JsonPropertyName("karat")] public string? Karat { get; init; }

    [JsonPropertyName("pieces")] public required int Pieces { get; init; }

    [JsonPropertyName("grossWeight")] public required decimal GrossWeight { get; init; }

    [JsonPropertyName("lessWeight")] public required decimal LessWeight { get; init; }

    [JsonPropertyName("netWeight")] public required decimal NetWeight { get; init; }

    /// <summary>The line's wastage; it may be below zero for an item sold under its purity.</summary>
    [JsonPropertyName("wastagePercent")] public required decimal WastagePercent { get; init; }

    /// <summary>Rupees a gram of fine (a 24K rate), or null for a line priced by the piece or with no fine weight.</summary>
    [JsonPropertyName("rate")] public required decimal? Rate { get; init; }

    [JsonPropertyName("labourAmount")] public required decimal LabourAmount { get; init; }

    [JsonPropertyName("stoneValue")] public required decimal StoneValue { get; init; }

    [JsonPropertyName("hallmarkAmount")] public required decimal HallmarkAmount { get; init; }

    [JsonPropertyName("discountAmount")] public required decimal DiscountAmount { get; init; }

    [JsonPropertyName("lineTotal")] public required decimal LineTotal { get; init; }

    /// <summary>Carats of the line's diamonds; 0, or left out, for a line with none.</summary>
    [JsonPropertyName("diamondCarats")] public decimal DiamondCarats { get; init; }

    /// <summary>
    /// What the line's diamonds are worth, without setting them; 0, or left out, for none. It is part
    /// of <see cref="StoneValue"/> and <see cref="LineTotal"/>, and is billed as a line of its own.
    /// </summary>
    [JsonPropertyName("diamondValue")] public decimal DiamondValue { get; init; }

    /// <summary>
    /// How far the sender's own rounding can have moved <see cref="LineTotal"/>, in rupees; 0, or left
    /// out, keeps the line total exactly. Within it, the line is billed as if typed.
    /// </summary>
    [JsonPropertyName("roundingAllowance")] public decimal RoundingAllowance { get; init; }
}
