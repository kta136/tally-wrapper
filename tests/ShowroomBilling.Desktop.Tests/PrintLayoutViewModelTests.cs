using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;
using System.Net;
using Xunit;

namespace ShowroomBilling.Desktop.Tests;

public sealed class PrintLayoutViewModelTests
{
    [Fact]
    public void Mandatory_visibility_is_locked_while_optional_visibility_is_editable()
    {
        var vm = new PrintLayoutViewModel();
        var items = vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.ItemsTable);
        var notes = vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.Notes);

        items.IsVisible = false;
        notes.IsVisible = false;

        Assert.True(items.IsVisible);
        Assert.False(items.CanHide);
        Assert.False(notes.IsVisible);
        Assert.True(notes.CanHide);
    }

    [Fact]
    public void Move_and_pin_commands_keep_pinned_group_contiguous_from_boundary()
    {
        var vm = new PrintLayoutViewModel();
        var terms = vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.Terms);
        vm.PinBottomFromHereCommand.Execute(terms);

        vm.MoveSection(PrintLayoutSectionKeys.Terms, PrintLayoutSectionKeys.Logo);

        var boundary = vm.SectionLayouts
            .Select((row, index) => (row, index))
            .Single(pair => pair.row.SectionKey == PrintLayoutSectionKeys.Terms).index;
        Assert.All(vm.SectionLayouts.Take(boundary), row => Assert.False(row.IsBottomPinned));
        Assert.All(vm.SectionLayouts.Skip(boundary), row => Assert.True(row.IsBottomPinned));

        vm.MoveSectionDownCommand.Execute(terms);
        var movedBoundary = vm.SectionLayouts.IndexOf(terms);
        Assert.All(vm.SectionLayouts.Take(movedBoundary), row => Assert.False(row.IsBottomPinned));
        Assert.All(vm.SectionLayouts.Skip(movedBoundary), row => Assert.True(row.IsBottomPinned));
    }

    [Fact]
    public void Reset_defaults_restores_order_visibility_density_border_and_gst_pin()
    {
        var vm = new PrintLayoutViewModel
        {
            PageDensity = PrintPageDensity.Comfortable,
            InvoiceBorderThicknessPt = 4
        };
        vm.MoveSectionToEnd(PrintLayoutSectionKeys.CopyLabel);
        vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.Logo).IsVisible = false;
        vm.ClearBottomPinCommand.Execute(null);

        vm.ResetPageLayoutCommand.Execute(null);

        Assert.Equal(PrintLayoutSectionKeys.All, vm.SectionLayouts.Select(row => row.SectionKey).ToArray());
        Assert.All(vm.SectionLayouts, row => Assert.True(row.IsVisible));
        Assert.Equal(PrintPageDensity.Standard, vm.PageDensity);
        Assert.Equal(1, vm.InvoiceBorderThicknessPt);
        Assert.Equal(PrintLayoutSectionKeys.GstBreakup, vm.BottomPinnedFromSectionKey);
    }

    [Fact]
    public async Task Watermark_upload_sets_pending_bytes_and_delete_clears_asset()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x33];
        var assets = new FakePrintAssetApi();
        var picker = new FakeImagePicker(new PrintLayoutImageSelection("brand.png", bytes));
        var vm = new PrintLayoutViewModel(new FakeSettingsApi(), assets, picker);

        await vm.UploadWatermarkCommand.ExecuteAsync(null);

        Assert.NotNull(vm.WatermarkAssetId);
        Assert.Same(bytes, vm.PendingWatermarkBytes);
        Assert.Equal("brand.png", vm.WatermarkFileName);
        Assert.Equal(PrintLayoutDefaults.WatermarkOffsetXCm, vm.WatermarkOffsetXCm);
        Assert.Equal(PrintLayoutDefaults.WatermarkOffsetYCm, vm.WatermarkOffsetYCm);
        Assert.Equal(PrintLayoutDefaults.WatermarkOpacityPercent, vm.WatermarkOpacityPercent);
        Assert.Equal(PrintAssetKinds.Watermark, assets.LastUpload!.AssetKind);

        await vm.DeleteWatermarkCommand.ExecuteAsync(null);

        Assert.Null(vm.WatermarkAssetId);
        Assert.Null(vm.PendingWatermarkBytes);
        Assert.Equal("—", vm.WatermarkFileName);
        Assert.Single(assets.DeletedIds);
    }

    [Fact]
    public async Task Rejected_watermark_upload_stays_available_as_local_preview_until_cleared()
    {
        byte[] bytes = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x33];
        var assets = new FakePrintAssetApi
        {
            UploadException = new ApiException(
                HttpStatusCode.BadRequest,
                "Bad Request",
                "Unsupported print asset kind.",
                null)
        };
        var picker = new FakeImagePicker(new PrintLayoutImageSelection(
            @"C:\Users\operator\Desktop\watermark.png",
            bytes));
        var settings = new FakeSettingsApi();
        var vm = new PrintLayoutViewModel(settings, assets, picker);

        await vm.UploadWatermarkCommand.ExecuteAsync(null);

        Assert.Null(vm.WatermarkAssetId);
        Assert.Same(bytes, vm.PendingWatermarkBytes);
        Assert.Equal("watermark.png", vm.WatermarkFileName);
        Assert.True(vm.HasWatermark);
        Assert.True(vm.DeleteWatermarkCommand.CanExecute(null));
        Assert.Contains("connected server", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("local preview", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Null(settings.LastUpdate);
        Assert.Contains("browse the file again", vm.StatusMessage, StringComparison.OrdinalIgnoreCase);

        await vm.DeleteWatermarkCommand.ExecuteAsync(null);

        Assert.Null(vm.PendingWatermarkBytes);
        Assert.False(vm.HasWatermark);
        Assert.Equal("—", vm.WatermarkFileName);
        Assert.Empty(assets.DeletedIds);
    }

    [Fact]
    public async Task Save_and_reload_round_trip_designer_values()
    {
        var watermarkId = Guid.NewGuid();
        var initial = new PrintLayoutSettings(
            1, 1, 1, 1,
            null,
            null,
            PrintLayoutDefaults.CreateWatermark(watermarkId),
            PrintLayoutDefaults.CreatePageLayout());
        var settingsApi = new FakeSettingsApi { Layout = initial };
        var assetsApi = new FakePrintAssetApi();
        assetsApi.Add(new PrintAssetResponse(
            watermarkId,
            Guid.NewGuid(),
            PrintAssetKinds.Watermark,
            "wm.png",
            "image/png",
            9,
            DateTimeOffset.UtcNow));
        var vm = new PrintLayoutViewModel(settingsApi, assetsApi, new FakeImagePicker(null));
        await vm.LoadAsync();

        vm.PageDensity = PrintPageDensity.Compact;
        vm.InvoiceBorderThicknessPt = 0;
        vm.BottomPinnedFromSectionKey = PrintLayoutSectionKeys.Terms;
        vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.BankDetails).IsVisible = false;
        vm.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.Terms).SpacingBeforeMm = 5;
        vm.MoveSection(PrintLayoutSectionKeys.Terms, PrintLayoutSectionKeys.Logo);
        vm.WatermarkOpacityPercent = 27;

        await vm.SaveCommand.ExecuteAsync(null);

        var saved = settingsApi.LastUpdate!.Layout;
        Assert.Equal(PrintPageDensity.Compact, saved.PageLayout!.Density);
        Assert.Equal(0, saved.PageLayout.InvoiceBorderThicknessPt);
        Assert.Equal(PrintLayoutSectionKeys.Terms, saved.PageLayout.BottomPinnedFromSectionKey);
        Assert.Equal(PrintLayoutSectionKeys.Terms, saved.PageLayout.Sections[1].SectionKey);
        Assert.False(saved.PageLayout.Sections.Single(row => row.SectionKey == PrintLayoutSectionKeys.BankDetails).IsVisible);
        Assert.Equal(5, saved.PageLayout.Sections.Single(row => row.SectionKey == PrintLayoutSectionKeys.Terms).SpacingBeforeMm);
        Assert.Equal(27, saved.Watermark!.OpacityPercent);

        settingsApi.Layout = saved;
        var reloaded = new PrintLayoutViewModel(settingsApi, assetsApi, new FakeImagePicker(null));
        await reloaded.LoadAsync();
        Assert.Equal(PrintPageDensity.Compact, reloaded.PageDensity);
        Assert.Equal(PrintLayoutSectionKeys.Terms, reloaded.SectionLayouts[1].SectionKey);
        Assert.False(reloaded.SectionLayouts.Single(row => row.SectionKey == PrintLayoutSectionKeys.BankDetails).IsVisible);
        Assert.Equal(27, reloaded.WatermarkOpacityPercent);
    }

    private sealed class FakeImagePicker(PrintLayoutImageSelection? selection) : IPrintLayoutImagePicker
    {
        public Task<PrintLayoutImageSelection?> PickAsync(string kind) => Task.FromResult(selection);
    }

    private sealed class FakePrintAssetApi : IPrintAssetApiClient
    {
        private readonly List<PrintAssetResponse> _assets = [];

        public PrintAssetUploadRequest? LastUpload { get; private set; }
        public List<Guid> DeletedIds { get; } = [];
        public Exception? UploadException { get; init; }

        public void Add(PrintAssetResponse asset) => _assets.Add(asset);

        public Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrintAssetListResponse(_assets.ToArray()));

        public Task<PrintAssetResponse> UploadAsync(
            PrintAssetUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpload = request;
            if (UploadException is not null)
            {
                return Task.FromException<PrintAssetResponse>(UploadException);
            }
            var response = new PrintAssetResponse(
                Guid.NewGuid(),
                Guid.NewGuid(),
                request.AssetKind,
                request.FileName,
                request.ContentType,
                Convert.FromBase64String(request.Base64Content).LongLength,
                DateTimeOffset.UtcNow);
            _assets.Add(response);
            return Task.FromResult(response);
        }

        public Task<byte[]?> DownloadAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<byte[]?>([1, 2, 3]);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeletedIds.Add(id);
            _assets.RemoveAll(asset => asset.Id == id);
            return Task.FromResult(true);
        }
    }

    private sealed class FakeSettingsApi : ISettingsApiClient
    {
        public PrintLayoutSettings Layout { get; set; } =
            new(1, 1, 1, 1, null, null, null, PrintLayoutDefaults.CreatePageLayout());

        public UpdatePrintLayoutRequest? LastUpdate { get; private set; }

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new PrintLayoutResponse(Layout, DateTimeOffset.UtcNow));

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default)
        {
            LastUpdate = request;
            Layout = request.Layout;
            return Task.FromResult(new PrintLayoutResponse(request.Layout, DateTimeOffset.UtcNow));
        }

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
