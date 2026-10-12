using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

/// <summary>What the Batch Data Scheduler dialog says beside its fields: the limit, the rule, how the run is going.</summary>
public partial class SyntheticBatchViewModel
{
    /// <summary>The word in the "Progress" chip.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunTone))]
    [NotifyPropertyChangedFor(nameof(IsFinished))]
    [NotifyPropertyChangedFor(nameof(ProgressValue))]
    private string runState = "Not started";

    /// <summary>Beside the largest-bill field: the cap, which is a GST rule and cannot be raised.</summary>
    public string MaxBillLimitText => $"limit {UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)}";

    /// <summary>The line under the amounts: what a bill can be worth, and why the top is fixed.</summary>
    public string BillRangeNote
        => $"Each bill is between ₹{UiCulture.Rupees(SyntheticBatchPlanLimits.SoftMinBillTotal)} and ₹{UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)}. "
           + $"The ₹{UiCulture.Rupees(SyntheticBatchPlanLimits.HardMaxBillAmount)} cap is a GST rule, not a preference.";

    /// <summary>A rate of zero is the one thing the form cannot guess, so the field itself is marked.</summary>
    public bool IsRateMissing => Rate24Kt <= 0m;

    /// <summary>"ok" when the batch has been made, "err" when it failed, "accent" while it runs, else neutral.</summary>
    public string RunTone => RunState switch
    {
        "Done" => "ok",
        "Failed" => "err",
        "Running" => "accent",
        "Stopped" => "warn",
        _ => "neutral",
    };

    public bool IsFinished => RunState == "Done";

    /// <summary>The bar is empty until the batch is made: the server makes it in one call, so there is no part-way.</summary>
    public double ProgressValue => IsFinished ? 100 : 0;
}
