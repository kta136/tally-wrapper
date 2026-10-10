using System.Text.Json;
using System.Text.Json.Serialization;

namespace ShowroomBilling.Contracts.Settings;

/// <summary>
/// Reads the item-master and karat-master JSON kept in cloud settings. The
/// server and the Desktop both parse these blobs, so they share one reader.
/// </summary>
public static class MasterDataJson
{
    // The entry records name every JSON property themselves; these options
    // only make the reader tolerant of casing and of numbers saved as text.
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Parses item masters. Blank text is an empty list; malformed JSON throws <see cref="JsonException"/>.</summary>
    public static IReadOnlyList<ItemMasterEntry> ParseItems(string? json) => Parse<ItemMasterEntry>(json);

    /// <summary>Parses karat masters. Blank text is an empty list; malformed JSON throws <see cref="JsonException"/>.</summary>
    public static IReadOnlyList<KaratMasterEntry> ParseKarats(string? json) => Parse<KaratMasterEntry>(json);

    /// <summary>As <see cref="ParseItems"/>, but malformed JSON is treated as no items.</summary>
    public static IReadOnlyList<ItemMasterEntry> ParseItemsOrEmpty(string? json) => ParseOrEmpty<ItemMasterEntry>(json);

    /// <summary>As <see cref="ParseKarats"/>, but malformed JSON is treated as no karats.</summary>
    public static IReadOnlyList<KaratMasterEntry> ParseKaratsOrEmpty(string? json) => ParseOrEmpty<KaratMasterEntry>(json);

    private static IReadOnlyList<T> Parse<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(json, ReadOptions) ?? [];
    }

    private static IReadOnlyList<T> ParseOrEmpty<T>(string? json)
    {
        try
        {
            return Parse<T>(json);
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
