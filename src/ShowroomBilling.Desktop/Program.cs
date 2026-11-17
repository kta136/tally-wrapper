using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop;

/// <summary>
/// Where the app starts (the project's <c>StartupObject</c>; the <c>Main</c> WPF writes for
/// <c>App.xaml</c> is not used).
/// <para>
/// Every bill hand-over starts the exe once more, only to pass its link to the copy already
/// running (docs/18_bill_handover.md §7). That start is over before anything of WPF is loaded:
/// creating <see cref="App"/> reads the four resource dictionaries of <c>App.xaml</c>, which took
/// about a third of the time the link spent on its way.
/// </para>
/// </summary>
public static class Program
{
    private static readonly TimeSpan HandoverForwardTimeout = TimeSpan.FromMilliseconds(800);

    [STAThread]
    public static int Main(string[] args)
    {
        string? name = null;
        string PipeName() => name ??= HandoverPipe.NameFor(EnvironmentName);

        // Started by a hand-over link while a copy of the app is already running: give it the
        // link and stop. A plain start is not affected; with no copy running this one takes it.
        if (TryHandToRunningCopy(args, PipeName, HandoverForwardTimeout, out var handoverLink))
        {
            return 0;
        }

        return RunApp(handoverLink, PipeName);
    }

    internal static string EnvironmentName
        => Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? Environments.Production;

    /// <summary>
    /// Offers the hand-over link among <paramref name="args"/>, if there is one, to the copy of
    /// the app listening on the pipe. True when that copy took it and this start has nothing left
    /// to do. Otherwise <paramref name="handoverLink"/> is the link this start handles itself,
    /// or null for a plain start, which never opens the pipe.
    /// <para>
    /// With no copy running or starting (<see cref="HandoverPipe.IsCopyRunning"/>) the answer is
    /// immediate. With one, the pipe is given <paramref name="timeout"/> to appear and answer,
    /// because a copy that started a moment ago has not opened it yet. This is the only time a
    /// start offers its link.
    /// </para>
    /// </summary>
    internal static bool TryHandToRunningCopy(
        IReadOnlyList<string> args,
        Func<string> pipeName,
        TimeSpan timeout,
        out string? handoverLink)
    {
        handoverLink = null;
        foreach (var argument in args)
        {
            if (HandoverLinkReader.IsHandoverArgument(argument))
            {
                handoverLink = argument;
                break;
            }
        }

        if (handoverLink is null)
        {
            return false;
        }

        try
        {
            var name = pipeName();
            return HandoverPipe.IsCopyRunning(name) && HandoverPipe.TrySend(name, handoverLink, timeout);
        }
        catch (Exception)
        {
            // Nothing can show a message this early. The app starts and takes the link itself;
            // whatever is wrong with the pipe is reported when it sets up its own end.
            return false;
        }
    }

    // A method of its own, so that a start which only passes a link on never loads a WPF type.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunApp(string? handoverLink, Func<string> pipeName)
    {
        // First of all, so that a link arriving while this copy is still starting waits for it
        // instead of opening a second window.
        using var running = MarkRunning(pipeName);

        var app = new App { HandoverLink = handoverLink };
        app.InitializeComponent();
        return app.Run();
    }

    private static IDisposable? MarkRunning(Func<string> pipeName)
    {
        try
        {
            return HandoverPipe.MarkRunning(pipeName());
        }
        catch (Exception)
        {
            // Without the marker a later start with a link opens a window of its own. The app
            // itself is not held up for that.
            return null;
        }
    }
}
