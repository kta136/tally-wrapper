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
    [ObservableProperty] private string previewText = "Type the core number (e.g. 42) to auto-format with prefix / fiscal / suffix, or a full custom string to store verbatim.";

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
        var v = (NewNumberRaw ?? string.Empty).Trim();
        if (v.Length == 0)
        {
            PreviewText = "Type the core number (e.g. 42) to auto-format with prefix / fiscal / suffix, or a full custom string to store verbatim.";
            return;
        }
        if (IsDigitsOnly(v) && long.TryParse(v, out var core) && core > 0)
        {
            PreviewText = $"Will save as: {InvoiceNumberFormatter.Format(_prefix, _suffix, core, _fiscalYear, _padding)}";
        }
        else
        {
            PreviewText = "Stored verbatim (no auto-format).";
        }
    }

    private static bool IsDigitsOnly(string value)
    {
        foreach (var c in value)
        {
            if (c < '0' || c > '9') return false;
        }
        return value.Length > 0;
    }

    private bool CanOk()
    {
        var v = (NewNumberRaw ?? string.Empty).Trim();
        return v.Length > 0 && !v.Any(char.IsWhiteSpace);
    }

    private void OnOk()
    {
        var v = (NewNumberRaw ?? string.Empty).Trim();
        if (v.Length == 0) return;
        var trimmedReason = string.IsNullOrWhiteSpace(Reason) ? null : Reason!.Trim();
        Closed?.Invoke((true, v, trimmedReason));
    }

    private void OnCancel() => Closed?.Invoke((false, string.Empty, null));
}
