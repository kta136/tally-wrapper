using System.Globalization;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Converters;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>What the redesigned dialogs say: the words and numbers their view models work out, which the views only show.</summary>
public sealed class DialogDisplayTests
{
    [Fact]
    public void A_destructive_question_is_also_a_warning_and_keeps_its_note()
    {
        var dialog = new ConfirmDialogViewModel();

        _ = dialog.ShowAsync(new ConfirmRequest("Delete SR/1?", "It goes.", "Delete bill", IsDestructive: true, Note: "This cannot be undone."));

        Assert.True(dialog.IsDestructive);
        Assert.True(dialog.IsWarning);
        Assert.True(dialog.HasNote);
        Assert.Equal("This cannot be undone.", dialog.NoteText);
    }

    [Fact]
    public void A_plain_question_has_no_note()
    {
        var dialog = new ConfirmDialogViewModel();

        _ = dialog.ShowAsync(new ConfirmRequest("Push all?", "Everything goes.", "Push all"));

        Assert.False(dialog.IsDestructive);
        Assert.False(dialog.HasNote);
    }

    [Fact]
    public void The_reason_needs_four_characters_and_counts_them()
    {
        var dialog = new ReasonPromptDialogViewModel();
        dialog.Configure(new ReasonRequest("Mark as pushed", "SR/1", "It marks.", "Mark as pushed"));

        dialog.ReasonText = "abc";
        Assert.False(dialog.OkCommand.CanExecute(null));
        Assert.Equal("3 so far", dialog.CountText);

        dialog.ReasonText = "  found it  ";
        Assert.True(dialog.OkCommand.CanExecute(null));
        Assert.True(dialog.IsLongEnough);
        Assert.Equal("8 so far", dialog.CountText);
    }

    [Fact]
    public void The_setup_form_is_done_step_by_step_and_says_what_is_left()
    {
        var wizard = new SetupWizardViewModel(settingsApi: null!, completionStore: null!)
        {
            TallyHost = "127.0.0.1",
            ActiveCompanyName = "Sample Jewellers",
        };

        Assert.True(wizard.IsTallyDone);
        Assert.False(wizard.IsCompanyDone);
        Assert.Equal(2, wizard.CurrentStep);
        Assert.Equal("Fill in the company name and the invoice numbering to finish.", wizard.FooterNote);
        Assert.False(wizard.SaveBasicsCommand.CanExecute(null));

        wizard.PrintCompanyName = "Sample Jewellers";
        wizard.SalesLedger = "Sales";
        wizard.SalesVoucherType = "Sales";
        wizard.CashLedger = "Cash";
        wizard.RoundOffLedger = "Round Off";

        Assert.Equal(3, wizard.CurrentStep);
        Assert.Equal("4 of 8 chosen", wizard.LedgersSummary);
        Assert.Equal("4 more to choose", wizard.LedgersBadge);
        Assert.Equal("Choose the 4 remaining ledgers to finish.", wizard.FooterNote);
        Assert.Equal([true, true, false], wizard.Steps.Select(step => step.IsDone));
        Assert.Equal([false, false, true], wizard.Steps.Select(step => step.IsCurrent));

        wizard.CreditDebitLedger = "Credit and Debit";
        wizard.CgstLedger = "Output CGST";
        wizard.SgstLedger = "Output SGST";
        wizard.DiscountLedger = "Discount Allowed";

        Assert.True(wizard.CanFinish);
        Assert.Equal(4, wizard.CurrentStep);
        Assert.True(wizard.SaveBasicsCommand.CanExecute(null));
    }

    [Fact]
    public void The_first_number_shown_follows_the_prefix_and_the_digits()
    {
        var wizard = new SetupWizardViewModel(settingsApi: null!, completionStore: null!)
        {
            InvoicePrefix = "SR/",
            InvoicePadding = "5",
        };

        Assert.Matches(@"^The first bill will be numbered \*\*SR/\d{4}-\d{2}/00001\*\*$", wizard.FirstNumberMarkup);
    }

    [Fact]
    public void The_bill_details_lines_read_the_way_the_operator_does()
    {
        var details = new BillDetailsViewModel();
        var created = new DateTimeOffset(2026, 10, 11, 15, 12, 0, TimeSpan.Zero);
        var payload = new BillPayloadDto(
            "Meera Subramaniam", null, null, null, new DateOnly(2026, 10, 11),
            [
                new BillLineItemDto("22kt Gold Chain", null, 12.450m, "gm", 8068.88m, 100457.56m, "22kt", null),
                new BillLineItemDto("Diamond Ring", null, 0.5m, "ct", 77000m, 38500m, null, null),
            ],
            new BillTotalsDto(286122.02m, 5000m, 8583.66m, 0.32m, 289706m),
            null, "Cash", 7780m);

        details.Bill = new BillResponse(
            Guid.NewGuid(), Guid.NewGuid(), null, "sale", BillStates.Draft, "SR/2026-27/0009", "2026-27", null, null,
            created, created, null, new BillRevisionResponse(Guid.NewGuid(), 1, created, null, null, null, payload));

        Assert.Equal(["12.450 g", "0.500 ct"], details.LineRows.Select(line => line.Net));
        Assert.Equal(["22kt", "—"], details.LineRows.Select(line => line.Karat));
        Assert.Equal("1,00,457.56", details.LineRows[0].LineTotal);
        Assert.Equal("₹2,89,706", details.GrandTotalDisplay);
        Assert.Equal("−5,000.00", details.DiscountDisplay);
        Assert.True(details.HasDiscount);
        Assert.Equal("₹7,780 per gram", details.Rate24KtDisplay);
        Assert.Equal("Draft. Nothing has been sent to Tally.", details.FooterText);

        // A draft is waiting for Tally, and the timeline says so after what has happened.
        var timeline = details.Timeline;
        var waiting = Assert.Single(timeline);
        Assert.True(waiting.IsPending);
        Assert.True(waiting.IsLast);
        Assert.Equal("Waiting for Tally", waiting.Title);
    }

    [Fact]
    public void The_last_action_replaces_the_state_in_the_foot_of_bill_details()
    {
        var details = new BillDetailsViewModel { StatusMessage = "Pushed to Tally." };

        Assert.Equal("Pushed to Tally.", details.FooterText);
    }

    [Theory]
    [InlineData("ok", "OK")]
    [InlineData("warn", "Check")]
    [InlineData("err", "Problem")]
    [InlineData("neutral", "Not checked")]
    [InlineData(null, "Not checked")]
    public void A_health_dot_has_a_word_on_its_chip(string? dot, string expected)
        => Assert.Equal(expected, new DotToStatusWordConverter().Convert(dot, typeof(string), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void A_grouped_number_reads_back_whatever_is_typed_round_it()
    {
        var converter = new GroupedNumberConverter();

        Assert.Equal("10,00,000", converter.Convert(1_000_000L, typeof(string), null!, CultureInfo.InvariantCulture));
        Assert.Equal(1_999_000L, converter.ConvertBack("₹ 19,99,000", typeof(long), null!, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void The_batch_says_where_it_is_and_how_far()
    {
        var batch = new SyntheticBatchViewModel();

        Assert.Equal("Not started", batch.RunState);
        Assert.Equal("neutral", batch.RunTone);
        Assert.Equal(0, batch.ProgressValue);
        Assert.True(batch.IsRateMissing);
        Assert.Equal("limit 1,99,000", batch.MaxBillLimitText);

        batch.Rate24Kt = 7780m;
        batch.RunState = "Done";

        Assert.False(batch.IsRateMissing);
        Assert.Equal("ok", batch.RunTone);
        Assert.Equal(100, batch.ProgressValue);
    }
}
