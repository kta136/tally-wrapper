using System.Windows;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>Context-menu actions on one bill row.</summary>
internal sealed class BillsRowActionWorkflow(
    IBillsApiClient? billsApi,
    IBillsActionWorkflowHost host,
    BillsActionRunner runner)
{
    internal async Task PushRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.RunAsync(
            $"Pushing {row.InvoiceNumberDisplay}…",
            "Push failed",
            async () =>
            {
                await billsApi.PushAsync(row.Id, new PushBillRequest("Context menu push"), cancellationToken);
                return $"Pushed {row.InvoiceNumberDisplay}.";
            },
            cancellationToken);
    }

    internal async Task RetryRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.RunAsync(
            $"Retrying {row.InvoiceNumberDisplay}…",
            "Retry failed",
            async () =>
            {
                await billsApi.RetryAsync(row.Id, new RetryBillPostingRequest("Context menu retry"), cancellationToken);
                return $"Retried {row.InvoiceNumberDisplay}.";
            },
            cancellationToken);
    }

    internal async Task RepostRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (!await runner.ConfirmAsync(BillConfirmations.Repost(row.InvoiceNumberDisplay, BillConfirmations.IsPosted(row)))) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.RunAsync(
            $"Reposting {row.InvoiceNumberDisplay}…",
            "Repost failed",
            async () =>
            {
                await billsApi.RepostAsync(
                    row.Id,
                    new RepostBillRequest(Guid.NewGuid().ToString("N"), "Context menu repost"),
                    cancellationToken);
                return $"Reposted {row.InvoiceNumberDisplay}.";
            },
            cancellationToken);
    }

    internal async Task ReviseRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;

        await runner.RunAsync(
            $"Creating revision for {row.InvoiceNumberDisplay}…",
            "Revise failed",
            async () =>
            {
                var revised = await billsApi.ReviseAsync(row.Id, new ReviseBillRequest(null), cancellationToken);
                return $"Revision created: {revised.InvoiceNumber ?? "(pending)"}";
            },
            cancellationToken);
    }

    internal async Task VoidRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (billsApi is null || row is null) return;
        if (!await runner.ConfirmAsync(BillConfirmations.Void(row.InvoiceNumberDisplay))) return;

        await runner.RunAsync(
            $"Voiding {row.InvoiceNumberDisplay}…",
            "Void failed",
            async () =>
            {
                await billsApi.VoidAsync(row.Id, new VoidBillRequest("Voided from Bills tab"), cancellationToken);
                return $"Voided {row.InvoiceNumberDisplay}.";
            },
            cancellationToken);
    }

    internal void CopyInvoiceNumber(BillListRowViewModel? row)
    {
        if (row is null || string.IsNullOrWhiteSpace(row.InvoiceNumber)) return;
        try
        {
            Clipboard.SetText(row.InvoiceNumber!);
            host.StatusMessage = $"Copied invoice number: {row.InvoiceNumber}";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Copy failed: {ex.Message}";
        }
    }

    internal async Task EditRowAsync(BillListRowViewModel? row, CancellationToken cancellationToken)
    {
        if (row is null || host.EditBillHandler is null) return;
        try
        {
            await host.EditBillHandler(row.Id, cancellationToken);
            host.StatusMessage = $"Editing {row.InvoiceNumberDisplay} in the Invoice tab.";
        }
        catch (Exception ex)
        {
            host.StatusMessage = $"Edit failed: {ApiResponseReader.FormatError(ex)}";
        }
    }
}
