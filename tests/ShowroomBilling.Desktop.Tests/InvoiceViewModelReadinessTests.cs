using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// Contract tests for the readiness-aware overload of
/// <see cref="InvoiceViewModel.RefreshNextNumberAsync(bool, CancellationToken)"/>.
/// On a cold launch the Invoice tab can activate before the API child has
/// bound its port; without the wait the existing fire-and-forget call
/// silently swallows the failure and the operator pays the full cold-path
/// cost on first SaveDraft. These tests pin the contract:
///
///   1. waitForApi: true does NOT call GetPreviewAsync until the signal fires.
///   2. After MarkReady, the call goes through and the InvoiceNumber updates.
///   3. waitForApi: false (the legacy default) is unaffected by the signal.
///   4. External cancellation aborts the wait without exception.
/// </summary>
public sealed class InvoiceViewModelReadinessTests
{
    [Fact]
    public async Task WaitForApi_DoesNotIssueRequest_BeforeReady()
    {
        var signal = new ApiReadinessSignal();
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: signal);

        var refreshTask = vm.RefreshNextNumberAsync(waitForApi: true);

        // Give the wait a moment to park itself on the readiness TCS.
        await Task.Delay(50);

        Assert.False(refreshTask.IsCompleted, "Refresh should still be parked on readiness signal.");
        Assert.Equal(0, numbering.PreviewCallCount);
    }

    [Fact]
    public async Task WaitForApi_IssuesRequest_AfterMarkReady()
    {
        var signal = new ApiReadinessSignal();
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: signal);

        var refreshTask = vm.RefreshNextNumberAsync(waitForApi: true);
        signal.MarkReady();
        await refreshTask;

        Assert.Equal(1, numbering.PreviewCallCount);
        Assert.Equal("SR/26-27/0001", vm.InvoiceNumber);
    }

    [Fact]
    public async Task WaitForApi_IsNoOp_WhenSignalAlreadyReady()
    {
        var signal = new ApiReadinessSignal();
        signal.MarkReady(); // Pre-flagged — second/third Invoice activation in a session.
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: signal);

        await vm.RefreshNextNumberAsync(waitForApi: true);

        Assert.Equal(1, numbering.PreviewCallCount);
        Assert.Equal("SR/26-27/0001", vm.InvoiceNumber);
    }

    [Fact]
    public async Task LegacyOverload_IgnoresSignal_AndIssuesRequestImmediately()
    {
        var signal = new ApiReadinessSignal(); // never marked ready
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: signal);

        // Legacy callsite (e.g. ClearInvoice after an existing session) must
        // not regress — by then the API is definitionally up.
        await vm.RefreshNextNumberAsync(CancellationToken.None);

        Assert.Equal(1, numbering.PreviewCallCount);
    }

    [Fact]
    public async Task ExternalCancellation_AbortsReadinessWait_WithoutException()
    {
        var signal = new ApiReadinessSignal(); // never marked ready
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: signal);

        using var cts = new CancellationTokenSource();
        var refreshTask = vm.RefreshNextNumberAsync(waitForApi: true, cts.Token);
        cts.Cancel();

        // Caller cancelled — the VM must absorb the OperationCanceledException
        // and bail without issuing a request.
        await refreshTask;

        Assert.Equal(0, numbering.PreviewCallCount);
    }

    [Fact]
    public async Task NullReadinessSignal_FallsThroughImmediately()
    {
        // Older test/design-time construction paths still use the
        // parameterless ctor; the wait must degrade gracefully.
        var numbering = new RecordingNumberingApiClient();
        var vm = new InvoiceViewModel(
            billsApi: null,
            numberingApi: numbering,
            settings: null,
            apiReadiness: null);

        await vm.RefreshNextNumberAsync(waitForApi: true);

        Assert.Equal(1, numbering.PreviewCallCount);
    }

    private sealed class RecordingNumberingApiClient : INumberingApiClient
    {
        public int PreviewCallCount { get; private set; }
        public int ReserveCallCount { get; private set; }

        public Task<NumberingPreviewResponse> GetPreviewAsync(
            string? documentType = null,
            string? fiscalYear = null,
            CancellationToken cancellationToken = default)
        {
            PreviewCallCount++;
            return Task.FromResult(new NumberingPreviewResponse(
                ShowroomId: Guid.Empty,
                FiscalYear: "26-27",
                DocumentType: documentType ?? "sales_invoice",
                PreviewValue: 1,
                FormattedNumber: "SR/26-27/0001",
                Prefix: "SR/",
                Suffix: null));
        }

        public Task<ReserveNumberResponse> ReserveAsync(
            ReserveNumberRequest request,
            CancellationToken cancellationToken = default)
        {
            ReserveCallCount++;
            throw new InvalidOperationException("Reserve should not be called from preview path.");
        }
    }
}
