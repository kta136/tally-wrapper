using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>What the hand-over needs from the shell.</summary>
internal interface IHandoverHost
{
    string? ActiveDialog { get; set; }

    NavTab ActiveTab { get; set; }

    BillsViewModel Bills { get; }

    SettingsViewModel Settings { get; }

    AdminUnlockViewModel Admin { get; }

    AdminTokenStore AdminTokens { get; }

    /// <summary>Opens the admin unlock and returns when it closes, unlocked or not.</summary>
    Task RequestAdminUnlockAsync(CancellationToken cancellationToken);

    /// <summary>Prints a saved bill straight to the printer, as a direct print after save does. False when it did not print.</summary>
    Task<bool> PrintSavedBillAsync(BillResponse bill);

    /// <summary>Opens Print preview with saved bills, where the operator prints them by hand.</summary>
    void OpenSavedBillsPreview(IReadOnlyList<BillResponse> bills);
}

/// <summary>
/// Takes the hand-over links this app receives, one at a time and in order: waits until the app
/// has started, checks the link, saves its bills, prints them and tells the operator. A link that
/// arrives while another is being handled waits its turn. See docs/18_bill_handover.md.
/// </summary>
public sealed class HandoverCoordinator
{
    private readonly IBillsApiClient? _bills;
    private readonly IHandoverHost _host;
    private readonly TimeProvider _time;
    private readonly Queue<string> _links = new();
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource<HandoverResultChoice>? _resultChoice;
    private bool _running;

    internal HandoverCoordinator(IBillsApiClient? bills, IHandoverHost host, TimeProvider? time = null)
    {
        _bills = bills;
        _host = host;
        _time = time ?? TimeProvider.System;
        Result.Closed += OnResultClosed;
    }

    /// <summary>The result dialog's content, while it is shown.</summary>
    public HandoverResultViewModel Result { get; } = new();

    /// <summary>Brings the main window to the front; the window wires it. Unset in tests.</summary>
    public Action? BringToFront { get; set; }

    /// <summary>Shows the toast card; the app wires it to a window that does not take the focus. Unset in tests.</summary>
    public Action<HandoverToastViewModel>? ShowToast { get; set; }

    public ILogger? Logger { get; set; }

    /// <summary>The task of the hand-overs being handled now; complete when there are none.</summary>
    public Task Idle { get; private set; } = Task.CompletedTask;

    /// <summary>Called once the app has loaded its settings (or failed to): links received before then are handled from here on.</summary>
    public void MarkStarted() => _started.TrySetResult();

    /// <summary>Takes a link from the command line or from a second start of the app. Call on the UI thread.</summary>
    public void Receive(string link)
    {
        _links.Enqueue(link);
        if (!_running)
        {
            _running = true;
            Idle = DrainAsync();
        }
    }

    /// <summary>The shell's dialog changed: Esc, or another dialog over the result, closes the result as Close.</summary>
    internal void OnActiveDialogChanged(string? value)
    {
        if (value != ShellDialogs.HandoverResult && _resultChoice is { Task.IsCompleted: false })
        {
            Result.Dismiss();
        }
    }

    private async Task DrainAsync()
    {
        try
        {
            await _started.Task;
            while (_links.TryDequeue(out var link))
            {
                try
                {
                    await HandleAsync(link);
                }
                catch (Exception ex)
                {
                    Logger?.LogError(ex, "A hand-over could not be handled.");
                    await ShowResultAsync(HandoverReports.Refused("Something went wrong while taking this hand-over. Nothing more was saved."));
                }
            }
        }
        finally
        {
            _running = false;
        }
    }

    private async Task HandleAsync(string link)
    {
        var read = HandoverLinkReader.Read(link, _host.Settings.Handover.Store.Load(), _time.GetUtcNow());
        if (read.Document is not { } document)
        {
            Logger?.LogWarning("A hand-over was refused: {Problem}", read.Problem);
            await ShowResultAsync(HandoverReports.Refused(read.Problem!));
            return;
        }

        var workflow = new HandoverSaveWorkflow(_bills!, RequestAdminTokenAsync, _host.PrintSavedBillAsync);
        while (true)
        {
            var run = await RunAsync(workflow, document);
            Logger?.LogInformation(
                "Hand-over of {Bills} bill(s): {Saved} saved, {Updated} updated, {Unchanged} unchanged, {Problems} not saved or updated, {Left} left out, {Unprinted} not printed, server unreachable {Unreachable}.",
                document.Bills.Count,
                Count(run, HandoverBillResult.Saved),
                Count(run, HandoverBillResult.Updated),
                Count(run, HandoverBillResult.Unchanged),
                run.Outcomes.Count(o => o.Result is HandoverBillResult.NotSaved or HandoverBillResult.NotUpdated or HandoverBillResult.Skipped),
                Count(run, HandoverBillResult.NotInHandover),
                run.Outcomes.Count(o => o.Print == HandoverPrintResult.Failed),
                run.ServerUnreachable);

            if (_host.ActiveTab == NavTab.Bills)
            {
                _ = _host.Bills.LoadAsync();
            }

            var unprinted = run.Outcomes
                .Where(outcome => outcome.Print == HandoverPrintResult.Failed && outcome.Bill is not null)
                .Select(outcome => outcome.Bill!)
                .ToList();
            var report = HandoverReports.From(run);
            if (report.AllWentWell)
            {
                var toast = new HandoverToastViewModel();
                toast.Show(report);
                toast.OpenBillsRequested += (_, _) => OpenBills();
                ShowToast?.Invoke(toast);
                if (unprinted.Count > 0)
                {
                    BringToFront?.Invoke();
                    _host.OpenSavedBillsPreview(unprinted);
                }

                return;
            }

            var choice = await ShowResultAsync(report);
            if (choice == HandoverResultChoice.TryAgain)
            {
                continue;
            }

            if (choice == HandoverResultChoice.OpenBills)
            {
                _host.ActiveTab = NavTab.Bills;
            }

            if (unprinted.Count > 0)
            {
                _host.OpenSavedBillsPreview(unprinted);
            }

            return;
        }
    }

    private async Task<HandoverRun> RunAsync(HandoverSaveWorkflow workflow, HandoverDocument document)
    {
        var settings = _host.Settings;
        if (_bills is not null && settings.Settings is null)
        {
            // The app started without its settings (the server did not answer then): try once more.
            await settings.LoadAsync();
        }

        if (_bills is null || settings.Settings is not { } loaded)
        {
            return new HandoverRun(
                document.Bills.Select(bill => new HandoverBillOutcome(
                    bill.SaveId,
                    null,
                    bill.PartyName.Trim(),
                    HandoverBillMapper.TotalsOf(bill.Lines.Select(line => line.LineTotal).ToList(), bill.FinalAmount).GrandTotal,
                    bill.Payment,
                    HandoverBillResult.NotSaved,
                    Reason: HandoverSaveWorkflow.ServerUnreachableReason,
                    Retryable: true)).ToList(),
                ServerUnreachable: true);
        }

        return await workflow.RunAsync(document, HandoverMasters.From(loaded.Masters), CancellationToken.None);
    }

    private async Task<string?> RequestAdminTokenAsync(BillResponse bill, string context, CancellationToken cancellationToken)
    {
        if (_host.AdminTokens.IsUnlocked && _host.AdminTokens.Current?.Token is { } open)
        {
            return open;
        }

        BringToFront?.Invoke();
        _host.Admin.HandoverContext = context;
        try
        {
            await _host.RequestAdminUnlockAsync(cancellationToken);
        }
        finally
        {
            _host.Admin.HandoverContext = null;
        }

        return _host.AdminTokens.IsUnlocked ? _host.AdminTokens.Current?.Token : null;
    }

    private async Task<HandoverResultChoice> ShowResultAsync(HandoverReport report)
    {
        BringToFront?.Invoke();
        Result.Show(report);
        var choice = new TaskCompletionSource<HandoverResultChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _resultChoice = choice;
        _host.ActiveDialog = ShellDialogs.HandoverResult;
        var chosen = await choice.Task;
        if (_host.ActiveDialog == ShellDialogs.HandoverResult)
        {
            _host.ActiveDialog = null;
        }

        return chosen;
    }

    private void OnResultClosed(object? sender, HandoverResultChoice choice)
    {
        var pending = _resultChoice;
        _resultChoice = null;
        pending?.TrySetResult(choice);
    }

    private void OpenBills()
    {
        BringToFront?.Invoke();
        _host.ActiveTab = NavTab.Bills;
    }

    private static int Count(HandoverRun run, HandoverBillResult result) => run.Outcomes.Count(outcome => outcome.Result == result);
}
