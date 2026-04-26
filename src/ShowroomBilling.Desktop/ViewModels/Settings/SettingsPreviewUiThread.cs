namespace ShowroomBilling.Desktop.ViewModels.Settings;

internal static class SettingsPreviewUiThread
{
    internal static Task InvokeAsync(Action action)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return app.Dispatcher.InvokeAsync(action).Task;
    }

    internal static Task<T> InvokeAsync<T>(Func<T> func)
    {
        var app = System.Windows.Application.Current;
        if (app is null || app.Dispatcher.CheckAccess())
        {
            return Task.FromResult(func());
        }
        return app.Dispatcher.InvokeAsync(func).Task;
    }
}
