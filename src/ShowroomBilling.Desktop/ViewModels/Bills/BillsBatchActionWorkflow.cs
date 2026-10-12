using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>Toolbar actions on the selected bills, or on every pending bill.</summary>
internal sealed class BillsBatchActionWorkflow(
    IBillsApiClient? billsApi,
    IBillsActionWorkflowHost host,
    BillsActionRunner runner)
{
    internal async Task PushSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = runner.SelectedRows();
        if (rows.Count == 0) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.RunAsync(
            $"Pushing {rows.Count} selected bill(s)…",
            "Push selected failed",
            async () => BillsStatusFormatter.FormatBatchPushStatus(await billsApi.PushSelectedAsync(
                new PushSelectedBillsRequest(rows.Select(x => x.Id).ToArray(), "Selected push from Bills tab"),
                cancellationToken)),
            cancellationToken);
    }

    internal async Task PushAllPendingAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var confirmed = await runner.ConfirmAsync(new ConfirmRequest(
            "Push all pending bills?",
            "Every pending and draft bill goes to Tally, oldest first. The run stops at the first failure.",
            "Push all"));
        if (!confirmed) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.RunAsync(
            "Pushing all pending bills (oldest first)…",
            "Push all pending failed",
            async () =>
            {
                var response = await billsApi.PushPendingAsync(
                    new PushPendingBillsRequest("Push all pending from Bills tab", null),
                    cancellationToken);
                return response.Matched == 0
                    ? "No pending bills to push."
                    : BillsStatusFormatter.FormatBatchPushStatus(response);
            },
            cancellationToken);
    }

    internal async Task RetrySelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = runner.SelectedRows();
        if (rows.Count == 0) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.ForEachRowAsync(
            rows,
            "Retrying",
            "Retry stopped",
            row => billsApi.RetryAsync(row.Id, new RetryBillPostingRequest("Retry selected from Bills tab"), cancellationToken),
            count => $"Retried {count} bill(s).",
            cancellationToken);
    }

    internal async Task RepostSelectedAsync(CancellationToken cancellationToken)
    {
        if (billsApi is null) return;
        var rows = runner.SelectedRows();
        if (rows.Count == 0) return;

        var question = BillConfirmations.Repost($"{rows.Count} bill(s)", rows.Any(BillConfirmations.IsPosted));
        if (!await runner.ConfirmAsync(question)) return;
        if (!await host.EnsureTallyPushAllowedAsync(cancellationToken)) return;

        await runner.ForEachRowAsync(
            rows,
            "Reposting",
            "Repost stopped",
            row => billsApi.RepostAsync(
                row.Id,
                new RepostBillRequest(Guid.NewGuid().ToString("N"), "Batch repost from Bills tab"),
                cancellationToken),
            count => $"Reposted {count} bill(s).",
            cancellationToken);
    }
}
