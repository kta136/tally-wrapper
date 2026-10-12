using System.Windows;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// One STA thread with a WPF <see cref="System.Windows.Application"/> for the whole test
/// process. WPF allows a single Application per process and ties every
/// element to the thread that created it, so view tests marshal onto this
/// thread instead of creating their own.
/// </summary>
internal static class WpfTestHost
{
    // The same dictionaries App.xaml merges, in the same order.
    private static readonly string[] AppDictionaries =
    [
        "Resources/DesignTokens.xaml",
        "Resources/Styles.xaml",
        "Resources/Converters.xaml",
        "Resources/Controls.xaml",
    ];

    private static readonly Lazy<Dispatcher> AppDispatcher = new(Start);

    public static T Run<T>(Func<T> action) => AppDispatcher.Value.Invoke(action);

    /// <summary>Lets queued binding and layout work finish.</summary>
    public static void DrainDispatcher()
        => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>();
        var thread = new Thread(() =>
        {
            try
            {
                // A plain Application, not the product's App: its constructor
                // installs process-wide exception handlers that show a message box.
                var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // As the product's App does, so amounts group the way the showroom writes them.
                Shell.UiCulture.Apply();
                foreach (var dictionary in AppDictionaries)
                {
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri($"/TallyWrapper;component/{dictionary}", UriKind.Relative),
                    });
                }

                ready.SetResult(Dispatcher.CurrentDispatcher);
            }
            catch (Exception ex)
            {
                ready.SetException(ex);
                return;
            }

            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "wpf-test-host",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.GetAwaiter().GetResult();
    }
}
