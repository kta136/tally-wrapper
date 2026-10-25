using System.Buffers.Text;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests.TestSupport;

/// <summary>
/// The hand-over contract's test vector, copied as it is, and helpers that sign other documents
/// with its key. The values are made up.
/// </summary>
internal static class HandoverTestLinks
{
    /// <summary>The bytes 0 to 31, as base64url.</summary>
    public const string VectorKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";

    /// <summary>The base64url of the vector's JSON, exactly as the contract gives it.</summary>
    public const string VectorData =
        "eyJ2IjoxLCJpc3N1ZWRBdCI6IjIwMjYtMTAtMjVUMTM6MzA6MDBaIiwiYmlsbHMiOlt7InNhdmVJZCI6IjBmOGI2YzFlLTVhMmQtNGM3ZS05YjFhLTNkNGU1ZjYwNzE4YSIsInBhcnR5TmFtZSI6IkJ1eWVyIEEiLCJwYXJ0eVBob25lIjoiOTAwMDAwMDAwMCIsInBhcnR5QWRkcmVzcyI6bnVsbCwiYmlsbERhdGUiOiIyMDI2LTEwLTI1IiwicGF5bWVudCI6IkNhc2giLCJsaW5lcyI6W3siaXRlbU5hbWUiOiJHb2xkIE9ybmFtZW50cyIsImthcmF0IjoiMjJLIiwicGllY2VzIjoxLCJncm9zc1dlaWdodCI6MTAuNSwibGVzc1dlaWdodCI6MC4yLCJuZXRXZWlnaHQiOjEwLjMsIndhc3RhZ2VQZXJjZW50IjoyLjUsInJhdGUiOjcwMDAsImxhYm91ckFtb3VudCI6MzA5MCwic3RvbmVWYWx1ZSI6MTUwMCwiaGFsbG1hcmtBbW91bnQiOjQ1LCJkaXNjb3VudEFtb3VudCI6MCwibGluZVRvdGFsIjo3MjU2OS4zfV19XSwibm90U2VudCI6W119";

    public const string VectorSignature = "8JdxWixNvInzgYmd5LzVDIkMrJxTNB3Dpj3oKOYwrxI";

    /// <summary>The JSON <see cref="VectorData"/> decodes to.</summary>
    public const string VectorJson =
        """{"v":1,"issuedAt":"2026-10-25T13:30:00Z","bills":[{"saveId":"0f8b6c1e-5a2d-4c7e-9b1a-3d4e5f60718a","partyName":"Buyer A","partyPhone":"9000000000","partyAddress":null,"billDate":"2026-10-25","payment":"Cash","lines":[{"itemName":"Gold Ornaments","karat":"22K","pieces":1,"grossWeight":10.5,"lessWeight":0.2,"netWeight":10.3,"wastagePercent":2.5,"rate":7000,"labourAmount":3090,"stoneValue":1500,"hallmarkAmount":45,"discountAmount":0,"lineTotal":72569.3}]}],"notSent":[]}""";

    public static readonly DateTimeOffset VectorIssuedAt = new(2026, 10, 25, 13, 30, 0, TimeSpan.Zero);

    public static byte[] VectorKeyBytes => Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();

    public static string Link(string data, string signature) => $"tallywrapper:handover?d={data}&s={signature}";

    /// <summary>A link carrying <paramref name="json"/>, signed with <paramref name="key"/> (the vector's when null).</summary>
    public static string Signed(string json, byte[]? key = null)
    {
        var data = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));
        return Link(data, HandoverLinkReader.Sign(data, key ?? VectorKeyBytes));
    }

    /// <summary>The vector's JSON as a tree to change, with <c>issuedAt</c> set to <paramref name="issuedAt"/> when given.</summary>
    public static JsonObject VectorDocument(DateTimeOffset? issuedAt = null)
    {
        var document = JsonNode.Parse(VectorJson)!.AsObject();
        if (issuedAt is { } at)
        {
            document["issuedAt"] = at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        }

        return document;
    }

    public static string Signed(JsonObject document, byte[]? key = null) => Signed(document.ToJsonString(), key);

    /// <summary>A bill of the hand-over JSON, for documents built in a test.</summary>
    public static JsonObject Bill(Guid saveId, string party, params JsonObject[] lines) => new()
    {
        ["saveId"] = saveId.ToString(),
        ["partyName"] = party,
        ["partyPhone"] = null,
        ["partyAddress"] = null,
        ["billDate"] = DateOnly.FromDateTime(DateTime.Today).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        ["payment"] = "Cash",
        ["lines"] = new JsonArray(lines.Select(line => (JsonNode)line).ToArray()),
    };

    public static JsonObject Line(string item = "Gold Ornaments", string? karat = "22K", decimal net = 10m, decimal lineTotal = 70_000m, decimal? rate = 7_000m) => new()
    {
        ["itemName"] = item,
        ["karat"] = karat,
        ["pieces"] = 1,
        ["grossWeight"] = net,
        ["lessWeight"] = 0,
        ["netWeight"] = net,
        ["wastagePercent"] = 2,
        ["rate"] = rate,
        ["labourAmount"] = 1_000,
        ["stoneValue"] = 0,
        ["hallmarkAmount"] = 0,
        ["discountAmount"] = 0,
        ["lineTotal"] = lineTotal,
    };

    public static JsonObject Document(DateTimeOffset issuedAt, IEnumerable<JsonObject> bills, IEnumerable<Guid>? notSent = null) => new()
    {
        ["v"] = 1,
        ["issuedAt"] = issuedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
        ["bills"] = new JsonArray(bills.Select(bill => (JsonNode)bill).ToArray()),
        ["notSent"] = new JsonArray((notSent ?? []).Select(id => (JsonNode)id.ToString()).ToArray()),
    };

    /// <summary>Reads a document the reader accepted, for tests that start after the checks.</summary>
    public static HandoverDocument Read(JsonObject document)
        => JsonSerializer.Deserialize<HandoverDocument>(document.ToJsonString())!;
}

/// <summary>The item and karat masters the hand-over tests match against.</summary>
internal static class HandoverTestMasters
{
    public static readonly ItemMasterEntry[] Items =
    [
        new("Gold Ornaments", ItemUnits.Gram, 8m, ItemCategories.GoldBased, PricingModes.Both, 320m, "Gold 22K"),
        new("Gold Chain", ItemUnits.Gram, 6m, ItemCategories.GoldBased, PricingModes.Wastage, 0m, "Chain 22K"),
        new("Gold Coin", ItemUnits.Gram, 0m, ItemCategories.GoldBased, PricingModes.Labour, 150m, "Coin 24K"),
        new("Diamond Ring", ItemUnits.Carat, 0m, ItemCategories.Diamond, PricingModes.Wastage, 0m, "Diamond Ring"),
        new("Diamond Pendant", ItemUnits.Gram, 0m, ItemCategories.Diamond, PricingModes.Wastage, 0m, "Diamond Pendant"),
    ];

    public static readonly KaratMasterEntry[] Karats =
    [
        new("24K", 100m, "Gold 24K"),
        new("22K", 91.6m, "Gold 22K"),
        new("18K", 75m, "Gold 18K"),
    ];

    public static MasterDataSettingsDto Dto => new(JsonSerializer.Serialize(Items), JsonSerializer.Serialize(Karats));

    public static HandoverMasters Masters => HandoverMasters.From(Dto);

    /// <summary>Settings as the server would send them, with these masters, for tests that need a whole settings view model.</summary>
    public static SettingsViewModel Settings(HandoverKeyStore? keys = null)
    {
        var settings = new SettingsViewModel(handoverKeys: keys ?? new HandoverKeyStore(Path.Combine(TestAppData.Root, "keys", Guid.NewGuid().ToString("N"), HandoverKeyStore.FileName)));
        settings.ApplyEffectiveSettings(new EffectiveSettingsResponse(
            "cloud",
            "Settings loaded from the server",
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("localhost", 9000, 30, "Sample Company"),
                new NumberingSettingsDto("SR/", string.Empty, 4),
                new PrintSettingsDto("Sample Company", null, null, null, "State", "India", null, null, null, null, null, true, false, false, 10, 8),
                new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                Dto),
            [],
            [],
            DateTimeOffset.UtcNow));
        return settings;
    }
}
