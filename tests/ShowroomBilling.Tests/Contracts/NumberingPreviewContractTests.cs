using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Contracts.PrintAssets;

namespace ShowroomBilling.Tests.Contracts;

/// <summary>
/// The two answers that lost a field when the showroom id and the document type left the
/// database: the number preview and a print asset. A Desktop of an older build must still be
/// answered, and neither answer may name what no longer exists.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class NumberingPreviewContractTests(TestApiFactory factory) : IClassFixture<TestApiFactory>
{
    [Fact]
    public async Task The_preview_names_the_year_and_the_number_and_no_document_type()
    {
        var client = factory.CreateClient();

        var preview = await client.GetFromJsonAsync<JsonElement>("/api/numbering/preview?fiscalYear=2026-27");

        Assert.Equal(
            ["fiscalYear", "formattedNumber", "prefix", "previewValue", "suffix"],
            preview.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal("2026-27", preview.GetProperty("fiscalYear").GetString());
        Assert.Equal(1, preview.GetProperty("previewValue").GetInt64());
        Assert.EndsWith("2026-27/0001", preview.GetProperty("formattedNumber").GetString());
    }

    [Fact]
    public async Task An_older_Desktop_that_still_sends_a_document_type_is_answered_all_the_same()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/numbering/preview?documentType=sales_invoice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var preview = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, preview.GetProperty("previewValue").GetInt64());
    }

    [Fact]
    public async Task A_print_asset_is_listed_without_a_showroom_id()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, factory.GetDeviceToken());
        var upload = await client.PostAsJsonAsync(
            "/api/print-assets",
            new PrintAssetUploadRequest(
                PrintAssetKinds.Logo,
                "logo.png",
                Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A])));
        Assert.Equal(HttpStatusCode.Created, upload.StatusCode);

        var list = await client.GetFromJsonAsync<JsonElement>("/api/print-assets");

        var asset = Assert.Single(list.GetProperty("assets").EnumerateArray());
        Assert.Equal(
            ["assetKind", "byteLength", "contentType", "createdAtUtc", "fileName", "id"],
            asset.EnumerateObject().Select(property => property.Name).Order());
    }
}
