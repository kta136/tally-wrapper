using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal interface IBillDetailsActionHost
{
    BillResponse? Bill { get; set; }
    bool IsActing { get; set; }
    string StatusMessage { get; set; }
    Action? BillMutated { get; }
    Func<ConfirmRequest, Task<bool>>? ConfirmHandler { get; }
    Task LoadAsync(Guid billId, CancellationToken cancellationToken = default);
    Task ReloadSupportingStateAsync(Guid billId, CancellationToken cancellationToken);
}

/// <summary>The action buttons in the Bill Details dialog.</summary>
internal sealed class BillDetailsActionWorkflow(
    IBillsApiClient? billsApi,
    IBillDetailsActionHost host)
{
    public Task PushAsync(CancellationToken cancellationToken) => RunAsync(
        "Pushing to Tally…",
        "Pushed to Tally.",
        "Push failed",
        async (api, billId) =>
        {
            host.Bill = await api.PushAsync(billId, new PushBillRequest(null), cancellationToken);
            await host.ReloadSupportingStateAsync(billId, cancellationToken);
        });

    public Task RetryAsync(CancellationToken cancellationToken) => RunAsync(
        "Retrying posting…",
        "Retry complete.",
        "Retry failed",
        async (api, billId) =>
        {
            await api.RetryAsync(billId, new RetryBillPostingRequest(null), cancellationToken);
            await host.LoadAsync(billId, cancellationToken);
        });

    public async Task RepostAsync(CancellationToken cancellationToken)
    {
        if (host.Bill is not { } bill) return;

        var question = BillConfirmations.Repost(
            $"invoice {bill.InvoiceNumber ?? "—"}",
            BillStateCapabilities.IsPosted(bill.State));
        if (host.ConfirmHandler is null || !await host.ConfirmHandler(question)) return;

        await RunAsync(
            "Reposting…",
            "Repost complete.",
            "Repost failed",
            async (api, billId) =>
            {
                await api.RepostAsync(
                    billId,
                    new RepostBillRequest(Guid.NewGuid().ToString("N"), null),
                    cancellationToken);
                await host.LoadAsync(billId, cancellationToken);
            });
    }

    public async Task VoidAsync(CancellationToken cancellationToken)
    {
        if (host.Bill is not { } bill) return;

        var question = BillConfirmations.Void($"invoice {bill.InvoiceNumber ?? "—"}");
        if (host.ConfirmHandler is null || !await host.ConfirmHandler(question)) return;

        await RunAsync(
            "Voiding…",
            "Bill voided.",
            "Void failed",
            async (api, billId) =>
            {
                host.Bill = await api.VoidAsync(billId, new VoidBillRequest(null), cancellationToken);
                await host.ReloadSupportingStateAsync(billId, cancellationToken);
            });
    }

    public Task ReviseAsync(CancellationToken cancellationToken) => RunAsync(
        "Creating revision…",
        "Revision created.",
        "Revise failed",
        async (api, billId) =>
        {
            // The revision is a new bill; the dialog follows it.
            var revision = await api.ReviseAsync(billId, new ReviseBillRequest(null), cancellationToken);
            host.Bill = revision;
            await host.ReloadSupportingStateAsync(revision.Id, cancellationToken);
        });

    private async Task RunAsync(string starting, string done, string failure, Func<IBillsApiClient, Guid, Task> action)
    {
        if (billsApi is null || host.Bill is null) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = starting;
        try
        {
            await action(billsApi, billId);
            host.StatusMessage = done;
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"{failure}: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            host.IsActing = false;
        }
    }
}
