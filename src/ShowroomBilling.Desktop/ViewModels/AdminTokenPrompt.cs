using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels;

internal static class AdminTokenPrompt
{
    public static async Task<string?> GetAsync(
        AdminTokenStore? tokens, Func<CancellationToken, Task> prompt, CancellationToken cancellationToken)
    {
        var current = tokens?.Current?.Token;
        if (!string.IsNullOrWhiteSpace(current)) return current;
        await prompt(cancellationToken);
        return tokens?.Current?.Token;
    }
}
