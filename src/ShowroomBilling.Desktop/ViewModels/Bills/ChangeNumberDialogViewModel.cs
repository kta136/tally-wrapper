using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Numbering;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>
/// State for the Change-Invoice-Number overlay. Lives as a singleton on
/// <c>MainWindowViewModel</c>; the shell calls <see cref="Configure"/> before
/// showing the dialog and awaits <see cref="Closed"/> for the result.
/// </summary>
public partial class ChangeNumberDialogViewModel : ObservableObject
{
    [ObservableProperty] private string currentNumberDisplay = "(none)";
    [ObservableProperty] private string newNumberRaw = string.Empty;
    [ObservableProperty] private string? reason;
    [ObservableProperty] private string previewText = HelpText;

    // The server accepts positive digits only and adds the prefix, fiscal year
    // and suffix itself (BillAdminWorkflow.ChangeInvoiceNumberAsync).
    private const string HelpText = "Type the new number in digits (e.g. 42). The prefix, fiscal year and suffix are added automatically.";

    private string? _prefix;
    private string? _suffix;
    private string _fiscalYear = "—";
    private int _padding = InvoiceNumberFormatter.DefaultPadding;

    public ChangeNumberDialogViewModel()
    {
        OkCommand = new RelayCommand(OnOk, CanOk);
        CancelCommand = new RelayCommand(OnCancel);
    }

    public IRelayCommand OkCommand { get; }
    public IRelayCommand CancelCommand { get; }

    /// <summary>Fires on OK or Cancel with the user's result.</summary>
    public event Action<(bool Confirmed, string NewNumber, string? Reason)>? Closed;

    /// <summary>
    /// Reset state for a fresh open. Called by the shell before flipping
    /// <c>ActiveDialog</c> to "changeNumber".
    /// </summary>
    public void Configure(string? currentNumber, string? prefix, string? suffix, string fiscalYear, int padding = InvoiceNumberFormatter.DefaultPadding)
    {
        CurrentNumberDisplay = string.IsNullOrWhiteSpace(currentNumber) ? "(none)" : currentNumber!;
        _prefix = prefix;
        _suffix = suffix;
        _fiscalYear = string.IsNullOrWhiteSpace(fiscalYear) ? "—" : fiscalYear;
        _padding = padding < 1 ? InvoiceNumberFormatter.DefaultPadding : padding;
        NewNumberRaw = string.Empty;
        Reason = null;
        UpdatePreview();
    }

    partial void OnNewNumberRawChanged(string value)
    {
        UpdatePreview();
        OkCommand.NotifyCanExecuteChanged();
    }

    private void UpdatePreview()
    {
        if (string.IsNullOrWhiteSpace(NewNumberRaw))
        {
            PreviewText = HelpText;
        }
        else if (TryReadCore(out var core))
        {
            PreviewText = $"Will save as: {InvoiceNumberFormatter.Format(_prefix, _suffix, core, _fiscalYear, _padding)}";
        }
        else
        {
            PreviewText = "Digits only, greater than zero. The prefix, fiscal year and suffix are added automatically.";
        }
    }

    private bool TryReadCore(out long core)
    {
        core = 0;
        var v = (NewNumberRaw ?? string.Empty).Trim();
        return v.Length > 0
            && v.All(char.IsAsciiDigit)
            && long.TryParse(v, out core)
            && core > 0;
    }

    private bool CanOk() => TryReadCore(out _);

    private void OnOk()
    {
        if (!TryReadCore(out _)) return;
        var v = NewNumberRaw.Trim();
        var trimmedReason = string.IsNullOrWhiteSpace(Reason) ? null : Reason!.Trim();
        Closed?.Invoke((true, v, trimmedReason));
    }

    private void OnCancel() => Closed?.Invoke((false, string.Empty, null));
}
