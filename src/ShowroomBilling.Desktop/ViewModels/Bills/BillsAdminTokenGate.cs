using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Bills;

internal sealed class BillsAdminTokenGate(AdminTokenStore? adminTokens)
{
    public Func<CancellationToken, Task>? AdminUnlockHandler { get; set; }

    public async Task<(string? Token, string? StatusMessage)> EnsureAsync(CancellationToken cancellationToken)
    {
        var current = adminTokens?.Current?.Token;
        if (!string.IsNullOrWhiteSpace(current)) return (current, null);

        if (AdminUnlockHandler is null)
        {
            return (null, "Admin unlock required — no unlock handler wired.");
        }

        try
        {
            await AdminUnlockHandler(cancellationToken);
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
