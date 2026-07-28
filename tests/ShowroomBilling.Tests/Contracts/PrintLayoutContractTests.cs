using System.Net;
using System.Net.Http.Json;
using ShowroomBilling.Contracts.Device;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests.Contracts;

public sealed class PrintLayoutContractTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public PrintLayoutContractTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Print_layout_put_and_get_round_trip_designer_contract()
    {
        var client = CreateAuthenticatedClient();
        var watermarkId = Guid.NewGuid();
        var pageLayout = PrintLayoutDefaults.CreatePageLayout() with
        {
            Density = PrintPageDensity.Compact,
            InvoiceBorderThicknessPt = 0,
            BottomPinnedFromSectionKey = PrintLayoutSectionKeys.Terms,
            Sections = PrintLayoutDefaults.CreatePageLayout().Sections
                .Select(section => section.SectionKey == PrintLayoutSectionKeys.BankDetails
                    ? section with { IsVisible = false, SpacingBeforeMm = 2, SpacingAfterMm = 3 }
                    : section)
                .Reverse()
                .ToArray()
        };
        var request = new UpdatePrintLayoutRequest(new PrintLayoutSettings(
            0.3, 0.4, 0.5, 0.6,
            null,
            null,
            new PrintLayoutWatermarkPlacement(watermarkId, 4.5, 8.85, 12, 12, 15),
            pageLayout));

        var put = await client.PutAsJsonAsync("/api/settings/print-layout", request);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var get = await client.GetFromJsonAsync<PrintLayoutResponse>("/api/settings/print-layout");
        Assert.NotNull(get);
        Assert.Equal(watermarkId, get!.Layout.Watermark!.AssetId);
        Assert.Equal(PrintPageDensity.Compact, get.Layout.PageLayout!.Density);
        Assert.Equal(0, get.Layout.PageLayout.InvoiceBorderThicknessPt);
        Assert.Equal(PrintLayoutSectionKeys.Terms, get.Layout.PageLayout.BottomPinnedFromSectionKey);
        Assert.Equal(PrintLayoutSectionKeys.Signature, get.Layout.PageLayout.Sections[0].SectionKey);
        var bank = get.Layout.PageLayout.Sections.Single(row => row.SectionKey == PrintLayoutSectionKeys.BankDetails);
        Assert.False(bank.IsVisible);
        Assert.Equal(2, bank.SpacingBeforeMm);
        Assert.Equal(3, bank.SpacingAfterMm);
    }

    [Fact]
    public async Task Watermark_upload_returns_created_and_downloads_original_bytes()
    {
        var client = CreateAuthenticatedClient();
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x2A];

        var response = await client.PostAsJsonAsync(
            "/api/print-assets",
            new PrintAssetUploadRequest(
                PrintAssetKinds.Watermark,
                "watermark.png",
                "image/png",
                Convert.ToBase64String(bytes)));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var asset = await response.Content.ReadFromJsonAsync<PrintAssetResponse>();
        Assert.NotNull(asset);
        Assert.Equal(PrintAssetKinds.Watermark, asset!.AssetKind);
        Assert.Equal("image/png", asset.ContentType);

        var downloaded = await client.GetByteArrayAsync($"/api/print-assets/{asset.Id}");
        Assert.Equal(bytes, downloaded);
    }

    [Fact]
    public async Task Mutating_print_layout_and_asset_endpoints_require_device_token()
    {
        var client = _factory.CreateClient();

        var put = await client.PutAsJsonAsync(
            "/api/settings/print-layout",
            new UpdatePrintLayoutRequest(DefaultLayout()));
        var upload = await client.PostAsJsonAsync(
            "/api/print-assets",
            new PrintAssetUploadRequest(
                PrintAssetKinds.Watermark,
                "watermark.png",
                "image/png",
                Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])));

        Assert.Equal(HttpStatusCode.Unauthorized, put.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, upload.StatusCode);
    }

    [Fact]
    public async Task Invalid_designer_payload_returns_bad_request_problem_details()
    {
        var client = CreateAuthenticatedClient();
        var invalid = DefaultLayout() with
        {
            PageLayout = PrintLayoutDefaults.CreatePageLayout() with
            {
                Sections = PrintLayoutDefaults.CreatePageLayout().Sections.Skip(1).ToArray()
            }
        };

        var response = await client.PutAsJsonAsync(
            "/api/settings/print-layout",
            new UpdatePrintLayoutRequest(invalid));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Missing PageLayout section key", body);
    }

    private HttpClient CreateAuthenticatedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenConstants.HeaderName, _factory.GetDeviceToken());
        return client;
    }

    private static PrintLayoutSettings DefaultLayout() =>
        new(
            1, 1, 1, 1,
            null,
            null,
            null,
            PrintLayoutDefaults.CreatePageLayout());
}
