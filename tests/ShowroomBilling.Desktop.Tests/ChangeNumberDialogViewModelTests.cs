using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class ChangeNumberDialogViewModelTests
{
    [Fact]
    public void Digits_preview_the_formatted_number_in_the_bills_fiscal_year()
    {
        var dialog = Configured(fiscalYear: "2025-26");

        dialog.NewNumberRaw = "42";

        Assert.Equal("Will save as: SR/2025-26/0042", dialog.PreviewText);
        Assert.True(dialog.OkCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("SR/2026-27/0042")]
    [InlineData("42A")]
    [InlineData("4 2")]
    [InlineData("0")]
    [InlineData("-3")]
    public void Anything_the_server_would_reject_cannot_be_confirmed(string input)
    {
        var dialog = Configured();

        dialog.NewNumberRaw = input;

        Assert.False(dialog.OkCommand.CanExecute(null));
        Assert.StartsWith("Digits only", dialog.PreviewText);
    }

    [Fact]
    public void Ok_reports_the_trimmed_digits_and_reason()
    {
        var dialog = Configured();
        (bool Confirmed, string NewNumber, string? Reason)? result = null;
        dialog.Closed += r => result = r;
        dialog.NewNumberRaw = " 42 ";
        dialog.Reason = "  typo  ";

        dialog.OkCommand.Execute(null);

        Assert.Equal((true, "42", "typo"), result);
    }

    private static ChangeNumberDialogViewModel Configured(string fiscalYear = "2026-27")
    {
        var dialog = new ChangeNumberDialogViewModel();
        dialog.Configure("SR/2026-27/0007", prefix: "SR/", suffix: null, fiscalYear, padding: 4);
        return dialog;
    }
}
