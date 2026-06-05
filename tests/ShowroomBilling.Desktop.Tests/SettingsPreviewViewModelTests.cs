using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Printing;
using Xunit;

namespace ShowroomBilling.Desktop.Tests;

public class SettingsPreviewViewModelTests
{
    private static readonly TimeSpan FastDebounce = TimeSpan.FromMilliseconds(15);
    private const int DebounceSettleMs = 120; // enough headroom for timer callback + async hops

    [Fact]
    public async Task Debounce_coalesces_burst_of_changes_into_single_render()
    {
        var (vm, dispatcher, _, settings) = BuildVm();
        vm.SetActive(true);

        // Fire many property changes within the debounce window — only one render
        // should actually kick off.
        for (int i = 0; i < 20; i++)
        {
            settings.Draft.PrintCompanyName = $"Acme {i}";
        }

        await dispatcher.WaitForRenderCountAsync(1);
        await Task.Delay(DebounceSettleMs);

        Assert.Equal(1, dispatcher.RenderCount);
    }

    [Fact]
    public async Task Stale_render_success_does_not_overwrite_newer()
    {
        var (vm, dispatcher, _, settings) = BuildVm();

        // Gate must be installed before activation so render 1 is captured by it.
        var release1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen = 0;
        dispatcher.Gate = _ => Interlocked.Increment(ref gen) == 1 ? release1.Task : release2.Task;

        vm.SetActive(true);
        await dispatcher.WaitForRenderCountAsync(1); // render 1 starts, stuck on release1

        settings.Draft.PrintCompanyName = "second";
        await dispatcher.WaitForRenderCountAsync(2); // render 2 starts, stuck on release2

        // Newer render (2) completes first; older (1) is now stale.
        release2.SetResult(true);
        await dispatcher.WaitForCompleteCountAsync(1);
        release1.SetResult(true);
        await dispatcher.WaitForCompleteCountAsync(2);

        // Stale success must not flip any state — StatusMessage stays as the newer
        // render left it (empty on success).
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public async Task Stale_render_failure_does_not_set_failure_status()
    {
        var (vm, dispatcher, _, settings) = BuildVm();

        var release1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gen = 0;
        dispatcher.Gate = _ =>
        {
            var n = Interlocked.Increment(ref gen);
            return n == 1
                ? release1.Task.ContinueWith<bool>(_ => throw new InvalidOperationException("stale boom"),
                    TaskContinuationOptions.ExecuteSynchronously)
                : Task.FromResult(true);
        };

        vm.SetActive(true);
        await dispatcher.WaitForRenderCountAsync(1);

        settings.Draft.PrintCompanyName = "second";
        await dispatcher.WaitForRenderCountAsync(2);
        await dispatcher.WaitForCompleteCountAsync(1);

        // Now let the stale render fail. Failure must be suppressed.
        release1.SetResult(true);
        await dispatcher.WaitForCompleteCountAsync(2);

        Assert.DoesNotContain("Preview failed", vm.StatusMessage);
    }

    [Fact]
    public async Task Bulk_update_suppresses_intermediate_renders_and_fires_one_on_completion()
    {
        var (vm, dispatcher, _, settings) = BuildVm();
        vm.SetActive(true);

        // Enter bulk — mimic what SaveAsync does on its IsSaving flips.
        settings.IsSaving = true;
        settings.Draft.PrintCompanyName = "a";
        settings.Draft.PrintCompanyName = "b";
        settings.Draft.PrintCompanyName = "c";

        await Task.Delay(DebounceSettleMs);
        Assert.Equal(0, dispatcher.RenderCount);

        // Trailing edge.
        settings.IsSaving = false;
        await dispatcher.WaitForRenderCountAsync(1);

        Assert.Equal(1, dispatcher.RenderCount);
    }

    [Fact]
    public async Task Asset_download_happens_once_per_asset_id_change()
    {
        var (vm, dispatcher, assets, settings) = BuildVm();
        vm.SetActive(true);

        var logoId = Guid.NewGuid();
        settings.PrintLayout.LogoAssetId = logoId;
        await Task.Delay(DebounceSettleMs);

        // Same id fired multiple times — no additional downloads.
        settings.PrintLayout.LogoAssetId = logoId;
        settings.PrintLayout.LogoAssetId = logoId;
        await Task.Delay(DebounceSettleMs);

        Assert.Equal(1, assets.DownloadCount);

        // Different id triggers one more.
        settings.PrintLayout.LogoAssetId = Guid.NewGuid();
        await Task.Delay(DebounceSettleMs);

        Assert.Equal(2, assets.DownloadCount);
    }

    [Fact]
    public async Task Activation_downloads_existing_logo_for_first_render()
    {
        var (vm, dispatcher, assets, settings) = BuildVm();
        settings.PrintLayout.LogoAssetId = Guid.NewGuid();

        vm.SetActive(true);
        await Task.Delay(DebounceSettleMs);

        Assert.Equal(1, assets.DownloadCount);
        Assert.NotNull(dispatcher.LastOptions?.Layout.LogoBytes);
    }

    private static (SettingsPreviewViewModel vm, FakePrintDispatcher dispatcher, FakeAssetsApi assets, SettingsViewModel settings)
        BuildVm()
    {
        var dispatcher = new FakePrintDispatcher();
        var assets = new FakeAssetsApi();

        // Use the internal factory ctor so the embedded Preview IS the one under test.
        // Previously the test created a second Preview over the same draft/layout and
        // both were subscribed — harmless in production but noisy in tests.
        var settings = new SettingsViewModel(
            settingsApi: null,
            mastersApi: null,
            printAssetApi: assets,
            previewFactory: (draft, layout, host) =>
                new SettingsPreviewViewModel(draft, layout, host, dispatcher, assets, null, FastDebounce));

        return (settings.Preview, dispatcher, assets, settings);
    }

    private sealed class FakePrintDispatcher : IPrintDispatcher
    {
        private int _renderCount;
        private int _completedCount;
        private readonly SemaphoreSlim _completed = new(0);
        public int RenderCount => Volatile.Read(ref _renderCount);
        public int CompletedCount => Volatile.Read(ref _completedCount);
        public PrintDocumentOptions? LastOptions { get; private set; }

        /// <summary>If set, each render awaits the returned task before returning.</summary>
        public Func<int, Task<bool>>? Gate { get; set; }

        public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150)
        {
            var n = Interlocked.Increment(ref _renderCount);
            LastOptions = options;
            try
            {
                if (Gate is { } g) g(n).GetAwaiter().GetResult();
                // Return zero pages — lets us exercise the render pipeline (counters,
                // generation gating, status/ bulk transitions) without dragging in
                // WPF BitmapImage decoding from fake bytes.
                return Array.Empty<byte[]>();
            }
            finally
            {
                Interlocked.Increment(ref _completedCount);
                _completed.Release();
            }
        }

        public byte[] GeneratePdf(PrintDocumentOptions options) => Array.Empty<byte>();
        public IReadOnlyList<string> AvailablePrinters() => Array.Empty<string>();
        public string? DefaultPrinter() => null;
        public bool PrintToPrinter(PrintDocumentOptions options, string printerName) => false;
        public string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileName) => string.Empty;

        public async Task WaitForRenderCountAsync(int target, int timeoutMs = 3000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (RenderCount < target)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {target} renders to start; got {RenderCount}");
                await Task.Delay(10);
            }
        }

        public async Task WaitForCompleteCountAsync(int target, int timeoutMs = 2000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (CompletedCount < target)
            {
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException($"Expected {target} renders to complete; got {CompletedCount}");
                await Task.Delay(10);
            }
        }
    }

    private sealed class FakeAssetsApi : IPrintAssetApiClient
    {
        private readonly ConcurrentBag<Guid> _downloaded = new();
        public int DownloadCount => _downloaded.Count;

        public Task<byte[]?> DownloadAsync(Guid id, CancellationToken cancellationToken = default)
        {
            _downloaded.Add(id);
            return Task.FromResult<byte[]?>(new byte[] { 1, 2, 3 });
        }

        public Task<PrintAssetListResponse> ListAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PrintAssetListResponse(Array.Empty<PrintAssetResponse>()));

        public Task<PrintAssetResponse> UploadAsync(PrintAssetUploadRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }
}
