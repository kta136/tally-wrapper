using ShowroomBilling.Contracts.Bills;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal static class BillsStatusFormatter
{
    public static string FormatBatchPushStatus(BillBatchPushResponse response)
    {
        if (response.Failed == 0)
        {
            return response.Succeeded == 0
                ? "No bills were pushed."
                : $"Pushed {response.Succeeded} bill(s).";
        }

        return response.Succeeded == 0
            ? $"Push stopped immediately: {response.FailureMessage}"
            : $"Pushed {response.Succeeded} bill(s); stopped on failure: {response.FailureMessage}";
    }
}
