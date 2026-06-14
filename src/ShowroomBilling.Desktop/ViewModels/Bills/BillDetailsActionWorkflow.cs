using System.Windows;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal interface IBillDetailsActionHost
{
    BillResponse? Bill { get; set; }
    BillPostingStatusResponse? PostingStatus { get; set; }
    bool IsActing { get; set; }
    string StatusMessage { get; set; }
    Action? BillMutated { get; }
    Task LoadAsync(Guid billId, CancellationToken cancellationToken = default);
    Task ReloadSupportingStateAsync(Guid billId, CancellationToken cancellationToken);
}

internal sealed class BillDetailsActionWorkflow(
    IBillsApiClient? billsApi,
    IBillDetailsActionHost host)
{
    public async Task PushAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null || host.Bill is null) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = "Pushing to Tally…";
        try
        {
            host.Bill = await billsApi.PushAsync(billId, new PushBillRequest(null, null), cancellationToken);
            await host.ReloadSupportingStateAsync(billId, cancellationToken);
            host.StatusMessage = "Pushed to Tally.";
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Push failed: {ex.Message}";
        }
        finally
        {
            host.IsActing = false;
        }
    }

    public async Task RetryAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null || host.Bill is null) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = "Retrying posting…";
        try
        {
            host.PostingStatus = await billsApi.RetryAsync(billId, new RetryBillPostingRequest(null), cancellationToken);
            await host.LoadAsync(billId, cancellationToken);
            host.StatusMessage = "Retry complete.";
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Retry failed: {ex.Message}";
        }
        finally
        {
            host.IsActing = false;
        }
    }

    public async Task RepostAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null || host.Bill is null) return;

        var alreadyPosted = BillStateCapabilities.IsPosted(host.Bill.State);
        var warning = alreadyPosted
            ? $"This bill (Invoice {host.Bill.InvoiceNumber ?? "—"}) is already posted to Tally. Reposting will push the current revision to Tally again — if the original voucher hasn't been cleared in Tally first, you will end up with a duplicate voucher.\n\nProceed with repost?"
            : $"Repost Invoice {host.Bill.InvoiceNumber ?? "—"} to Tally?";

        var confirm = MessageBox.Show(
            warning,
            "Confirm Repost",
            MessageBoxButton.OKCancel,
            alreadyPosted ? MessageBoxImage.Warning : MessageBoxImage.Question,
            MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = "Reposting…";
        try
        {
            var key = Guid.NewGuid().ToString("N");
            host.PostingStatus = await billsApi.RepostAsync(billId, new RepostBillRequest(key, null), cancellationToken);
            await host.LoadAsync(billId, cancellationToken);
            host.StatusMessage = "Repost complete.";
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Repost failed: {ex.Message}";
        }
        finally
        {
            host.IsActing = false;
        }
    }

    public async Task VoidAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null || host.Bill is null) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = "Voiding…";
        try
        {
            host.Bill = await billsApi.VoidAsync(billId, new VoidBillRequest(null), cancellationToken);
            await host.ReloadSupportingStateAsync(billId, cancellationToken);
            host.StatusMessage = "Bill voided.";
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Void failed: {ex.Message}";
        }
        finally
        {
            host.IsActing = false;
        }
    }

    public async Task ReviseAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null || host.Bill is null) return;

        var billId = host.Bill.Id;
        host.IsActing = true;
        host.StatusMessage = "Creating revision…";
        try
        {
            host.Bill = await billsApi.ReviseAsync(billId, new ReviseBillRequest(null), cancellationToken);
            await host.ReloadSupportingStateAsync(host.Bill.Id, cancellationToken);
            host.StatusMessage = "Revision created.";
            host.BillMutated?.Invoke();
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Revise failed: {ex.Message}";
        }
        finally
        {
            host.IsActing = false;
        }
    }
}
