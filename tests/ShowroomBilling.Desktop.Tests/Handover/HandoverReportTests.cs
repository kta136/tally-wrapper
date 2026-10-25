using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Handover;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverReportTests
{
    private static HandoverBillOutcome Outcome(
        HandoverBillResult result,
        HandoverPrintResult print = HandoverPrintResult.None,
        string? number = "SR/2026-27/1",
        string party = "Buyer A",
        string payment = PaymentMode.Cash,
        string? reason = null,
        bool retryable = false)
        => new(Guid.NewGuid(), number, party, 10_000m, payment, result, print, reason, Retryable: retryable);

    [Fact]
    public void Two_bills_saved_and_printed_make_a_toast_with_their_payments()
    {
        var report = HandoverReports.From(new HandoverRun(
            [
                Outcome(HandoverBillResult.Saved, HandoverPrintResult.Printed),
                Outcome(HandoverBillResult.Saved, HandoverPrintResult.Printed, "SR/2026-27/2", "Buyer B", PaymentMode.CreditDebit),
            ],
            false));

        Assert.True(report.AllWentWell);
        Assert.Equal("2 bills saved and printed", report.Title);
        Assert.Equal(["Cash", "Credit and debit"], report.Rows.Select(row => row.Note));
        Assert.Equal("₹10,000", report.Rows[0].Amount);
        Assert.Equal("Pending in Bills", report.FooterNote);
    }

    [Fact]
    public void One_bill_saved_and_printed_says_Bill_saved_and_printed()
    {
        var report = HandoverReports.From(new HandoverRun([Outcome(HandoverBillResult.Saved, HandoverPrintResult.Printed)], false));

        Assert.True(report.AllWentWell);
        Assert.Equal("Bill saved and printed", report.Title);
    }

    [Fact]
    public void A_hand_over_sent_again_says_what_was_updated_and_what_was_unchanged()
    {
        var report = HandoverReports.From(new HandoverRun(
            [Outcome(HandoverBillResult.Updated, HandoverPrintResult.Printed), Outcome(HandoverBillResult.Unchanged, party: "Buyer B")],
            false));

        Assert.True(report.AllWentWell);
        Assert.Equal("1 bill updated and printed", report.Title);
        Assert.Equal(["Updated · printed", "Unchanged"], report.Rows.Select(row => row.Note));
        Assert.Equal(["ok", "muted"], report.Rows.Select(row => row.Tone));
    }

    [Fact]
    public void Every_bill_unchanged_is_still_a_toast()
    {
        var report = HandoverReports.From(new HandoverRun([Outcome(HandoverBillResult.Unchanged), Outcome(HandoverBillResult.Unchanged)], false));

        Assert.True(report.AllWentWell);
        Assert.Equal("2 bills unchanged", report.Title);
        Assert.Equal(string.Empty, report.FooterNote);
    }

    [Fact]
    public void A_bill_that_did_not_print_leaves_and_printed_out_of_the_title()
    {
        var report = HandoverReports.From(new HandoverRun([Outcome(HandoverBillResult.Saved, HandoverPrintResult.Failed)], false));

        Assert.True(report.AllWentWell);
        Assert.Equal("Bill saved", report.Title);
        Assert.Equal("Not printed", Assert.Single(report.Rows).Note);
    }

    [Fact]
    public void One_bill_not_saved_of_two_opens_the_dialog_with_its_reason()
    {
        var report = HandoverReports.From(new HandoverRun(
            [
                Outcome(HandoverBillResult.Saved, HandoverPrintResult.Printed),
                Outcome(HandoverBillResult.NotSaved, number: null, party: "Buyer B", reason: "No item named “Silver Bowl” in Tally Wrapper’s items."),
            ],
            false));

        Assert.False(report.AllWentWell);
        Assert.Equal("1 of 2 bills not saved", report.Title);
        Assert.Null(report.Banner);
        Assert.Equal(["Saved · printed", "Not saved"], report.Rows.Select(row => row.Note));
        Assert.Equal("—", report.Rows[1].InvoiceNumber);
        Assert.False(report.Rows[1].HasInvoiceNumber);
        Assert.Equal("err", report.Rows[1].WhyTone);
        Assert.False(report.CanTryAgain);
        Assert.True(report.CanOpenBills);
    }

    [Fact]
    public void An_unreachable_server_says_nothing_was_saved_and_offers_Try_again()
    {
        var report = HandoverReports.From(new HandoverRun(
            [
                Outcome(HandoverBillResult.NotSaved, number: null, reason: "The server can't be reached.", retryable: true),
                Outcome(HandoverBillResult.NotSaved, number: null, party: "Buyer B", reason: "The server can't be reached.", retryable: true),
            ],
            true));

        Assert.Equal("Bills not saved", report.Title);
        Assert.Equal("The server can't be reached. Nothing was saved.", report.Banner);
        Assert.True(report.CanTryAgain);
        Assert.False(report.CanOpenBills);
    }

    [Fact]
    public void A_bill_left_out_of_the_hand_over_is_reported_with_what_to_do()
    {
        var report = HandoverReports.From(new HandoverRun(
            [
                Outcome(HandoverBillResult.Updated, HandoverPrintResult.Printed),
                Outcome(HandoverBillResult.NotInHandover, party: "Buyer B", reason: "Void it in Bills if it’s no longer wanted."),
            ],
            false));

        Assert.Equal("A bill wasn’t in this hand-over", report.Title);
        Assert.Equal(["Updated · printed", "Not in this hand-over"], report.Rows.Select(row => row.Note));
        Assert.Equal(["ok", "warn"], report.Rows.Select(row => row.Tone));
        Assert.Equal("warn", report.Rows[1].WhyTone);
    }

    [Fact]
    public void A_skipped_admin_update_is_reported_as_not_updated()
    {
        var report = HandoverReports.From(new HandoverRun([Outcome(HandoverBillResult.Skipped, reason: "Skipped.")], false));

        Assert.Equal("Bill not updated", report.Title);
        Assert.Equal("Not updated", Assert.Single(report.Rows).Note);
        Assert.Equal("warn", report.Rows[0].Tone);
    }

    [Fact]
    public void A_refused_link_says_why_and_nothing_else()
    {
        var report = HandoverReports.Refused("This hand-over is too old. Send it again.");

        Assert.False(report.AllWentWell);
        Assert.Equal("Nothing was saved", report.Title);
        Assert.Equal("This hand-over is too old. Send it again.", report.Banner);
        Assert.Empty(report.Rows);
        Assert.False(report.CanTryAgain);
        Assert.False(report.CanOpenBills);
    }
}
