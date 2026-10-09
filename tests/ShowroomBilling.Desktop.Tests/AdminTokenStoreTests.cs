using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

public sealed class AdminTokenStoreTests
{
    [Fact]
    public async Task Expiry_raises_Changed_so_admin_ui_can_hide()
    {
        var store = new AdminTokenStore();
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += unlock =>
        {
            if (unlock is null) expired.TrySetResult();
        };

        var now = DateTimeOffset.UtcNow;
        store.Set(new AdminUnlockResponse("token", now, now.AddMilliseconds(100), "admin"));

        await expired.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(store.IsUnlocked);
    }
}
