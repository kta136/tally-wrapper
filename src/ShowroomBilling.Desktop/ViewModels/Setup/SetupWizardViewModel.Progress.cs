using System.ComponentModel;
using System.Globalization;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Setup;

/// <summary>One step on the rail of the first-launch dialog.</summary>
public sealed record SetupStep(int Number, string Title, string Detail, bool IsDone, bool IsCurrent, bool IsLast);

/// <summary>
/// How far the first-launch form has got: three steps, each done or not, and what is still missing. The
/// rail on the left of the dialog draws these, and Finish stays off until all three are done.
/// </summary>
public partial class SetupWizardViewModel
{
    /// <summary>The eight ledger-side choices of step 3: seven ledgers and the sales voucher type.</summary>
    public const int LedgerChoiceCount = 8;

    private static readonly HashSet<string> ProgressInputs =
    [
        nameof(TallyHost), nameof(TallyPort), nameof(TallyTimeoutSeconds), nameof(ActiveCompanyName),
        nameof(PrintCompanyName), nameof(InvoicePrefix), nameof(InvoiceSuffix), nameof(InvoicePadding),
        nameof(SalesLedger), nameof(CashLedger), nameof(CreditDebitLedger), nameof(CgstLedger),
        nameof(SgstLedger), nameof(RoundOffLedger), nameof(DiscountLedger), nameof(SalesVoucherType),
    ];

    /// <summary>Step 1: a host, a port and timeout in range, and a company.</summary>
    public bool IsTallyDone =>
        !string.IsNullOrWhiteSpace(TallyHost)
        && int.TryParse(TallyPort, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
        && int.TryParse(TallyTimeoutSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout)
        && timeout is >= SettingLimits.MinTallyTimeoutSeconds and <= SettingLimits.MaxTallyTimeoutSeconds
        && !string.IsNullOrWhiteSpace(ActiveCompanyName);

    /// <summary>Step 2: the name that goes on the invoice, and a number width that can be used.</summary>
    public bool IsCompanyDone =>
        !string.IsNullOrWhiteSpace(PrintCompanyName)
        && int.TryParse(InvoicePadding, NumberStyles.Integer, CultureInfo.InvariantCulture, out var padding) && padding is >= 1 and <= 10;

    public int LedgersChosen => new[]
    {
        SalesLedger, SalesVoucherType, CashLedger, CreditDebitLedger, CgstLedger, SgstLedger, RoundOffLedger, DiscountLedger,
    }.Count(value => !string.IsNullOrWhiteSpace(value));

    public int LedgersRemaining => LedgerChoiceCount - LedgersChosen;

    public bool IsLedgersDone => LedgersRemaining == 0;

    public bool CanFinish => IsTallyDone && IsCompanyDone && IsLedgersDone;

    /// <summary>1, 2 or 3: the first step that is not done. 4 when every step is.</summary>
    public int CurrentStep => !IsTallyDone ? 1 : !IsCompanyDone ? 2 : !IsLedgersDone ? 3 : 4;

    /// <summary>The three steps for the rail: done, current (the first not done) or still to come.</summary>
    public IReadOnlyList<SetupStep> Steps =>
    [
        new(1, "Tally", "Host, port, company", IsTallyDone, CurrentStep == 1, IsLast: false),
        new(2, "Company and numbers", "Name, invoice prefix", IsCompanyDone, CurrentStep == 2, IsLast: false),
        new(3, "Ledgers", LedgersSummary, IsLedgersDone, CurrentStep == 3, IsLast: true),
    ];

    public string LedgersSummary => $"{LedgersChosen} of {LedgerChoiceCount} chosen";

    public string LedgersBadge => IsLedgersDone ? "Done" : $"{LedgersRemaining} more to choose";

    /// <summary>The number the first bill will carry, with <c>**bold**</c> round it.</summary>
    public string FirstNumberMarkup
    {
        get
        {
            _ = int.TryParse(InvoicePadding, NumberStyles.Integer, CultureInfo.InvariantCulture, out var padding);
            var number = InvoiceNumberFormatter.Format(
                InvoicePrefix, InvoiceSuffix, 1, InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.Now),
                padding is >= 1 and <= 10 ? padding : InvoiceNumberFormatter.DefaultPadding);
            return $"The first bill will be numbered **{number}**";
        }
    }

    /// <summary>The foot of the dialog says what stands between the operator and Finish.</summary>
    public string FooterNote
    {
        get
        {
            if (!IsTallyDone) return "Fill in the Tally connection to finish.";
            if (!IsCompanyDone) return "Fill in the company name and the invoice numbering to finish.";
            if (!IsLedgersDone) return $"Choose the {LedgersRemaining} remaining ledger{(LedgersRemaining == 1 ? string.Empty : "s")} to finish.";
            return "Everything is filled in. Finish setup to start billing.";
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is { } name && ProgressInputs.Contains(name))
        {
            NotifyProgressChanged();
        }
    }

    private void NotifyProgressChanged()
    {
        OnPropertyChanged(nameof(IsTallyDone));
        OnPropertyChanged(nameof(IsCompanyDone));
        OnPropertyChanged(nameof(LedgersChosen));
        OnPropertyChanged(nameof(LedgersRemaining));
        OnPropertyChanged(nameof(IsLedgersDone));
        OnPropertyChanged(nameof(CanFinish));
        OnPropertyChanged(nameof(CurrentStep));
        OnPropertyChanged(nameof(LedgersSummary));
        OnPropertyChanged(nameof(Steps));
        OnPropertyChanged(nameof(LedgersBadge));
        OnPropertyChanged(nameof(FirstNumberMarkup));
        OnPropertyChanged(nameof(FooterNote));
        SaveBasicsCommand?.NotifyCanExecuteChanged();
    }
}
