using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.Handover;

/// <summary>
/// One row of the hand-over toast or result dialog: the invoice number ("—" until there is one),
/// the party, the bill's total, a chip and, in the dialog, the reason under the row.
/// <see cref="Tone"/> and <see cref="WhyTone"/> are "ok", "warn", "err" or "neutral".
/// </summary>
public sealed record HandoverRowViewModel(
    string InvoiceNumber,
    bool HasInvoiceNumber,
    string Party,
    string Amount,
    string Note,
    string Tone,
    string? Why,
    string WhyTone)
{
    public bool HasWhy => !string.IsNullOrWhiteSpace(Why);
}

/// <summary>What the operator is told about a hand-over: a toast when it all went well, else the result dialog.</summary>
internal sealed record HandoverReport(
    bool AllWentWell,
    string Title,
    string? Banner,
    IReadOnlyList<HandoverRowViewModel> Rows,
    string FooterNote,
    bool CanTryAgain,
    bool CanOpenBills);

/// <summary>Works out the titles, chips and sentences of the toast and the result dialog. Nothing here talks to the server.</summary>
internal static class HandoverReports
{
    public const string PrintFailed = "Printing failed. It opens in Print preview when you close this.";

    /// <summary>A hand-over that failed its checks: nothing in it was used.</summary>
    public static HandoverReport Refused(string problem)
        => new(false, "Nothing was saved", problem, [], string.Empty, CanTryAgain: false, CanOpenBills: false);

    public static HandoverReport From(HandoverRun run)
    {
        var bills = run.Outcomes.Where(outcome => outcome.Result != HandoverBillResult.NotInHandover).ToList();
        var leftovers = run.Outcomes.Count - bills.Count;
        var problems = bills.Count(outcome => outcome.Result is HandoverBillResult.NotSaved or HandoverBillResult.NotUpdated or HandoverBillResult.Skipped);
        var done = bills.Count(outcome => outcome.Result is HandoverBillResult.Saved or HandoverBillResult.Updated or HandoverBillResult.Unchanged);

        if (problems == 0 && leftovers == 0 && !run.ServerUnreachable)
        {
            return Toast(bills);
        }

        string title;
        string? banner = null;
        if (run.ServerUnreachable && done == 0)
        {
            title = bills.Count == 1 ? "Bill not saved" : "Bills not saved";
            banner = "The server can't be reached. Nothing was saved.";
        }
        else
        {
            if (run.ServerUnreachable)
            {
                banner = "The server can't be reached. Try again to finish this hand-over.";
            }

            title = problems > 0
                ? ProblemTitle(bills, problems)
                : leftovers switch
                {
                    0 => "Hand-over not finished",
                    1 => "A bill wasn’t in this hand-over",
                    _ => $"{leftovers} bills weren’t in this hand-over",
                };
        }

        return new HandoverReport(
            false,
            title,
            banner,
            // The banner says the server cannot be reached; the rows do not say it again.
            run.Outcomes.Select(outcome => run.ServerUnreachable && outcome.Reason == HandoverSaveWorkflow.ServerUnreachableReason
                    ? ResultRow(outcome with { Reason = null })
                    : ResultRow(outcome))
                .ToList(),
            string.Empty,
            run.CanTryAgain,
            CanOpenBills: done > 0 || leftovers > 0 || run.Outcomes.Any(outcome => outcome.InvoiceNumber is not null));
    }

    private static string ProblemTitle(IReadOnlyList<HandoverBillOutcome> bills, int problems)
    {
        var notSaved = bills.Count(outcome => outcome.Result == HandoverBillResult.NotSaved);
        var verb = notSaved == problems ? "saved" : notSaved == 0 ? "updated" : "saved or updated";
        if (bills.Count == 1)
        {
            return $"Bill not {verb}";
        }

        return problems == bills.Count
            ? $"Bills not {verb}"
            : $"{problems} of {bills.Count} bills not {verb}";
    }

    private static HandoverReport Toast(IReadOnlyList<HandoverBillOutcome> bills)
    {
        var saved = bills.Count(outcome => outcome.Result == HandoverBillResult.Saved);
        var updated = bills.Count(outcome => outcome.Result == HandoverBillResult.Updated);
        var printedAll = bills.All(outcome => outcome.Print != HandoverPrintResult.Failed);
        var suffix = printedAll ? " and printed" : string.Empty;
        var single = bills.Count == 1;

        string title;
        if (saved + updated == 0)
        {
            title = single ? "Bill unchanged" : $"{bills.Count} bills unchanged";
        }
        else if (updated == 0)
        {
            title = single ? $"Bill saved{suffix}" : $"{saved} {BillsWord(saved)} saved{suffix}";
        }
        else if (saved == 0)
        {
            title = single ? $"Bill updated{suffix}" : $"{updated} {BillsWord(updated)} updated{suffix}";
        }
        else
        {
            title = $"{saved + updated} bills saved{suffix}";
        }

        return new HandoverReport(
            true,
            title,
            null,
            bills.Select(ToastRow).ToList(),
            saved + updated > 0 ? "Pending in Bills" : string.Empty,
            CanTryAgain: false,
            CanOpenBills: true);
    }

    private static string BillsWord(int count) => count == 1 ? "bill" : "bills";

    private static HandoverRowViewModel ToastRow(HandoverBillOutcome outcome)
    {
        var (note, tone) = outcome switch
        {
            { Print: HandoverPrintResult.Failed } => ("Not printed", "warn"),
            { Result: HandoverBillResult.Saved } => (outcome.Payment, "neutral"),
            { Result: HandoverBillResult.Updated } => ("Updated · printed", "ok"),
            _ => ("Unchanged", "muted"),
        };
        return Row(outcome, note, tone, null, "neutral");
    }

    private static HandoverRowViewModel ResultRow(HandoverBillOutcome outcome)
    {
        var printFailed = outcome.Print == HandoverPrintResult.Failed;
        return outcome.Result switch
        {
            HandoverBillResult.Saved => printFailed
                ? Row(outcome, "Saved · not printed", "warn", PrintFailed, "warn")
                : Row(outcome, "Saved · printed", "ok", null, "neutral"),
            HandoverBillResult.Updated => printFailed
                ? Row(outcome, "Updated · not printed", "warn", PrintFailed, "warn")
                : Row(outcome, "Updated · printed", "ok", null, "neutral"),
            HandoverBillResult.Unchanged => Row(outcome, "Unchanged", "neutral", null, "neutral"),
            HandoverBillResult.NotSaved => Row(outcome, "Not saved", "err", outcome.Reason, "err"),
            HandoverBillResult.Skipped => Row(outcome, "Not updated", "warn", outcome.Reason, "warn"),
            HandoverBillResult.NotInHandover => Row(outcome, "Not in this hand-over", "warn", outcome.Reason, "warn"),
            _ => Row(outcome, "Not updated", "err", outcome.Reason, "err"),
        };
    }

    private static HandoverRowViewModel Row(HandoverBillOutcome outcome, string note, string tone, string? why, string whyTone)
    {
        var number = string.IsNullOrWhiteSpace(outcome.InvoiceNumber) ? null : outcome.InvoiceNumber!.Trim();
        return new HandoverRowViewModel(
            number ?? "—",
            number is not null,
            outcome.PartyName,
            outcome.Amount is { } amount ? $"₹{UiCulture.Rupees(amount)}" : "—",
            note,
            tone,
            why,
            whyTone);
    }
}
