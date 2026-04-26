using System.Text.Json.Serialization;

namespace ShowroomBilling.Contracts.Settings;

public sealed record KaratMasterEntry(
    [property: JsonPropertyName("label")] string Label,
    [property: JsonPropertyName("purity_percent")] decimal PurityPercent,
    [property: JsonPropertyName("tally_item")] string TallyItem);
