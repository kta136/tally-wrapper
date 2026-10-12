using System.Collections.ObjectModel;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal interface IBillsActionWorkflowHost
{
    ObservableCollection<BillListRowViewModel> Items { get; }
    string StatusMessage { get; set; }
    bool IsActing { get; set; }
    Func<Guid, CancellationToken, Task>? EditBillHandler { get; }
    Func<BillListRowViewModel, CancellationToken, Task<(bool Confirmed, string NewNumber, string? Reason)>>? ChangeNumberPromptHandler { get; }
    Func<ReasonRequest, CancellationToken, Task<string?>>? ReasonPromptHandler { get; }
    Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; }
    Task<bool> EnsureTallyPushAllowedAsync(CancellationToken cancellationToken);
    Task LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The steps every Bills action shares: which rows it applies to, asking the
/// operator first, and the busy flag / status text / reload around the call.
/// </summary>
internal sealed class BillsActionRunner(IBillsActionWorkflowHost host)
{
    public IReadOnlyList<BillListRowViewModel> SelectedRows()
        => host.Items.Where(x => x.IsSelected).ToArray();

    /// <summary>
    /// Rows a context-menu action applies to: the whole selection when the
    /// clicked row is part of a multi-row selection, otherwise just that row.
    /// </summary>
    public IReadOnlyList<BillListRowViewModel> ContextRows(BillListRowViewModel? row)
    {
        if (row is null)
        {
            return [];
        }

        var selected = SelectedRows();
        return row.IsSelected && selected.Count > 1 ? selected : [row];
    }

    public async Task<bool> ConfirmAsync(ConfirmRequest request)
    {
        if (host.ConfirmHandler is null)
        {
            host.StatusMessage = "Confirmation prompt is not wired.";
            return false;
        }

        return await host.ConfirmHandler(request);
    }

    /// <summary>
    /// Runs one action: shows <paramref name="starting"/>, then the text the
    /// action returns, or "<paramref name="failure"/>: reason" if it throws.
    /// The list is reloaded either way.
    /// </summary>
    public async Task RunAsync(
        string starting,
        string failure,
        Func<Task<string>> action,
        CancellationToken cancellationToken)
    {
        host.IsActing = true;
        try
        {
            host.StatusMessage = starting;
            host.StatusMessage = await action();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"{failure}: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            host.IsActing = false;
        }

        await host.LoadAsync(cancellationToken);
    }

    /// <summary>
    /// Runs one call per row with "<paramref name="verb"/> 2 of 5…" progress.
    /// The first failure stops the run.
    /// </summary>
    public Task ForEachRowAsync(
        IReadOnlyList<BillListRowViewModel> rows,
        string verb,
        string failure,
        Func<BillListRowViewModel, Task> action,
        Func<int, string> done,
        CancellationToken cancellationToken)
        => RunAsync(
            $"{verb} 1 of {rows.Count}…",
            failure,
            async () =>
            {
                for (var index = 0; index < rows.Count; index++)
                {
                    host.StatusMessage = $"{verb} {index + 1} of {rows.Count}…";
                    await action(rows[index]);
                }

                return done(rows.Count);
            },
            cancellationToken);
}

/// <summary>The questions the Bills tab and the Bill Details dialog both ask, worded once.</summary>
internal static class BillConfirmations
{
    /// <param name="what">What is being reposted, e.g. an invoice number or "3 bill(s)".</param>
    /// <param name="tallyMayHoldVoucher">True when a bill in the set is posted, so Tally may still hold its voucher.</param>
    public static ConfirmRequest Repost(string what, bool tallyMayHoldVoucher) => tallyMayHoldVoucher
        ? new ConfirmRequest(
            $"Repost {what}?",
            "A repost sends a new voucher to Tally. If Tally still holds the original voucher, this creates a duplicate sale.",
            "Repost to Tally",
            IsWarning: true,
            Note: "Repost only after the original voucher has been deleted in Tally.")
        : new ConfirmRequest($"Repost {what}?", "The bill goes to Tally again as a new voucher.", "Repost to Tally");

    public static ConfirmRequest Void(string what)
        => new(
            $"Void {what}?",
            "The bill is marked void. It can no longer be edited or sent to Tally.",
            "Void bill",
            IsDestructive: true,
            Note: "This cannot be undone.");

    public static bool IsPosted(BillListRowViewModel row) => BillStateCapabilities.IsPosted(row.State);
}
