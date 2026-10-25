using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Handover;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>The cases of the hand-over boards (TwToast, TwResult), with made-up bills, for the view snapshots.</summary>
internal static class HandoverSamples
{
    public enum ToastKind
    {
        Two,
        One,
        Resend,
    }

    public enum ResultKind
    {
        NotSaved,
        Server,
        Leftover,
        Refused,
    }

    private static HandoverBillOutcome Bill(
        HandoverBillResult result,
        string? number,
        string party,
        decimal amount,
        string payment = PaymentMode.Cash,
        HandoverPrintResult print = HandoverPrintResult.None,
        string? reason = null,
        bool retryable = false)
        => new(Guid.NewGuid(), number, party, amount, payment, result, print, reason, Retryable: retryable);

    public static HandoverToastViewModel Toast(ToastKind kind)
    {
        var run = kind switch
        {
            ToastKind.One => new HandoverRun([Bill(HandoverBillResult.Saved, "SR/2026-27/14", "Buyer A", 72_569m, print: HandoverPrintResult.Printed)], false),
            ToastKind.Resend => new HandoverRun(
                [
                    Bill(HandoverBillResult.Updated, "SR/2026-27/14", "Buyer A", 74_000m, print: HandoverPrintResult.Printed),
                    Bill(HandoverBillResult.Unchanged, "SR/2026-27/15", "Buyer B", 31_000m, PaymentMode.CreditDebit),
                ],
                false),
            _ => new HandoverRun(
                [
                    Bill(HandoverBillResult.Saved, "SR/2026-27/14", "Buyer A", 72_569m, print: HandoverPrintResult.Printed),
                    Bill(HandoverBillResult.Saved, "SR/2026-27/15", "Buyer B", 31_000m, PaymentMode.CreditDebit, HandoverPrintResult.Printed),
                ],
                false),
        };

        var toast = new HandoverToastViewModel();
        toast.Show(HandoverReports.From(run));
        return toast;
    }

    public static void ShowResult(HandoverResultViewModel result, ResultKind kind)
    {
        var report = kind switch
        {
            ResultKind.Server => HandoverReports.From(new HandoverRun(
                [
                    Bill(HandoverBillResult.NotSaved, null, "Buyer A", 72_569m, reason: HandoverSaveWorkflow.ServerUnreachableReason, retryable: true),
                    Bill(HandoverBillResult.NotSaved, null, "Buyer B", 31_000m, reason: HandoverSaveWorkflow.ServerUnreachableReason, retryable: true),
                ],
                true)),
            ResultKind.Leftover => HandoverReports.From(new HandoverRun(
                [
                    Bill(HandoverBillResult.Updated, "SR/2026-27/14", "Buyer A", 74_000m, print: HandoverPrintResult.Printed),
                    Bill(HandoverBillResult.NotInHandover, "SR/2026-27/15", "Buyer B", 31_000m, reason: "Void it in Bills if it’s no longer wanted."),
                ],
                false)),
            ResultKind.Refused => HandoverReports.Refused("This hand-over isn't signed with this PC's key."),
            _ => HandoverReports.From(new HandoverRun(
                [
                    Bill(HandoverBillResult.Saved, "SR/2026-27/14", "Buyer A", 72_569m, print: HandoverPrintResult.Printed),
                    Bill(HandoverBillResult.NotSaved, null, "Buyer B", 31_000m, reason: "No item named “Silver Bowl” in Tally Wrapper’s items."),
                ],
                false)),
        };

        result.Show(report);
    }
}
