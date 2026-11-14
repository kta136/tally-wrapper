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
        var current = adminTokens?.Current?.Token;
        if (!string.IsNullOrWhiteSpace(current)) return (current, null);

        try
        {
            await dialogs.RequestAdminUnlockAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return (null, null);
        }

        var after = adminTokens?.Current?.Token;
        return string.IsNullOrWhiteSpace(after)
            ? (null, "Admin unlock cancelled.")
            : (after, null);
    }
}
