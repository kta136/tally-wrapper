using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

/// <summary>
/// Gets the admin token for an admin-only call: the one in hand, or the one
/// the operator gets by unlocking when asked.
/// </summary>
internal sealed class BillsAdminTokenGate(AdminTokenStore? adminTokens, IDialogService dialogs)
{
    public async Task<(string? Token, string? StatusMessage)> EnsureAsync(CancellationToken cancellationToken)
    {
        try
        {
            var token = await AdminTokenPrompt.GetAsync(adminTokens, dialogs.RequestAdminUnlockAsync, cancellationToken);
            return string.IsNullOrWhiteSpace(token) ? (null, "Admin unlock cancelled.") : (token, null);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }
    }
}
