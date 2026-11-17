using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.Handover;

/// <summary>
/// The link scheme's registration and its guard, the pipe a second start hands a link over, and
/// the app's entry point, which does that before anything else.
/// </summary>
public sealed class HandoverStartupTests
{
    [Theory]
    [InlineData("Production", null, null, false, true, true)]
    [InlineData("production", "", "", false, true, true)]
    [InlineData("Development", null, null, false, true, false)]
    [InlineData("Production", @"C:\scratch\appdata", null, false, true, false)]
    [InlineData("Production", null, @"C:\scratch\local", false, true, false)]
    [InlineData("Production", null, null, true, true, false)]
    [InlineData("Production", null, null, false, false, false)]
    public void Only_the_published_app_registers_the_link_scheme(
        string environment, string? dataOverride, string? localOverride, bool debugBuild, bool singleFile, bool registers)
        => Assert.Equal(registers, HandoverUrlScheme.ShouldRegister(environment, dataOverride, localOverride, debugBuild, singleFile));

    [Fact]
    public void A_test_run_never_registers()
    {
        Assert.False(HandoverUrlScheme.ShouldRegisterThisProcess(Environments.Production));
    }

    [Fact]
    public void Registering_writes_the_scheme_once_and_rewrites_it_when_it_points_elsewhere()
    {
        // A scratch key of its own, never HKCU\Software\Classes itself.
        var scratch = $@"Software\ShowroomBilling.Tests\{Guid.NewGuid():N}";
        try
        {
            using var classes = Registry.CurrentUser.CreateSubKey(scratch);
            const string Exe = @"C:\Apps\TallyWrapper.exe";

            Assert.True(HandoverUrlScheme.Register(classes, Exe));
            Assert.False(HandoverUrlScheme.Register(classes, Exe));

            using (var scheme = classes.OpenSubKey("tallywrapper"))
            {
                Assert.Equal(string.Empty, scheme!.GetValue("URL Protocol"));
                using var command = scheme.OpenSubKey(@"shell\open\command");
                Assert.Equal("\"C:\\Apps\\TallyWrapper.exe\" \"%1\"", command!.GetValue(null));
            }

            Assert.True(HandoverUrlScheme.Register(classes, @"D:\Elsewhere\TallyWrapper.exe"));
            using var moved = classes.OpenSubKey(@"tallywrapper\shell\open\command");
            Assert.Equal("\"D:\\Elsewhere\\TallyWrapper.exe\" \"%1\"", moved!.GetValue(null));
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(scratch, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task A_second_start_hands_its_link_to_the_running_copy()
    {
        var pipeName = $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new HandoverPipe.Server(pipeName, link => received.TrySetResult(link));
        server.Start();
        var link = "tallywrapper:handover?d=" + new string('x', 20_000) + "&s=y";

        var sent = await Task.Run(() => HandoverPipe.TrySend(pipeName, link, TimeSpan.FromSeconds(5)), TestContext.Current.CancellationToken);

        Assert.True(sent);
        Assert.Equal(link, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void With_no_copy_running_the_link_is_not_handed_over()
    {
        Assert.False(HandoverPipe.TrySend($"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}", "tallywrapper:handover?d=x&s=y", TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public async Task A_start_with_a_link_gives_it_to_the_running_copy_and_has_nothing_left_to_do()
    {
        var pipeName = $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var running = HandoverPipe.MarkRunning(pipeName);
        using var server = new HandoverPipe.Server(pipeName, link => received.TrySetResult(link));
        server.Start();
        const string Link = "tallywrapper:handover?d=x&s=y";
        string? kept = null;

        // As Windows starts it for a link: the link is the only argument.
        var handedOver = await Task.Run(
            () => Program.TryHandToRunningCopy([Link], () => pipeName, TimeSpan.FromSeconds(5), out kept),
            TestContext.Current.CancellationToken);

        Assert.True(handedOver);
        Assert.Equal(Link, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_start_with_a_link_and_no_copy_running_keeps_the_link_for_itself_without_waiting()
    {
        const string Link = "tallywrapper:handover?d=x&s=y";
        var clock = Stopwatch.StartNew();

        var handedOver = Program.TryHandToRunningCopy(
            ["--some-switch", Link],
            () => $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}",
            TimeSpan.FromSeconds(30),
            out var kept);

        // The app goes on to start, and handles this link once it has. It is not
        // offered to a running copy a second time: App takes it as given.
        Assert.False(handedOver);
        Assert.Equal(Link, kept);

        // No copy holds the marker, so there is no pipe to wait for. Every such start used
        // to wait the whole time it allows a pipe to appear (0.8 s in the app, 30 s here).
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"The start waited {clock.Elapsed.TotalSeconds:0.0} s for a copy that is not there.");
    }

    [Fact]
    public async Task A_copy_that_is_still_starting_gets_the_link_when_its_pipe_opens()
    {
        var pipeName = $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        const string Link = "tallywrapper:handover?d=x&s=y";

        // The copy has begun to start: it holds the marker, and its pipe is not open yet.
        using var running = HandoverPipe.MarkRunning(pipeName);
        string? kept = null;
        var offering = Task.Run(
            () => Program.TryHandToRunningCopy([Link], () => pipeName, TimeSpan.FromSeconds(20), out kept),
            TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(offering.IsCompleted);

        using var server = new HandoverPipe.Server(pipeName, link => received.TrySetResult(link));
        server.Start();

        // No second window: the start waited for the copy instead of opening the app itself.
        Assert.True(await offering);
        Assert.Equal(Link, await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_copy_that_holds_the_marker_but_never_opens_its_pipe_is_waited_for_only_so_long()
    {
        var pipeName = $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}";
        using var running = HandoverPipe.MarkRunning(pipeName);

        var handedOver = Program.TryHandToRunningCopy(
            ["tallywrapper:handover?d=x&s=y"], () => pipeName, TimeSpan.FromMilliseconds(200), out var kept);

        Assert.False(handedOver);
        Assert.Equal("tallywrapper:handover?d=x&s=y", kept);
    }

    [Fact]
    public void The_marker_is_there_for_as_long_as_the_copy_holds_it()
    {
        var pipeName = $"TallyWrapper.Handover.Tests.{Guid.NewGuid():N}";
        Assert.False(HandoverPipe.IsCopyRunning(pipeName));

        using (HandoverPipe.MarkRunning(pipeName))
        {
            Assert.True(HandoverPipe.IsCopyRunning(pipeName));

            // A copy of the app under another name (another environment, another settings folder) is not this one.
            Assert.False(HandoverPipe.IsCopyRunning(pipeName + ".other"));
        }

        Assert.False(HandoverPipe.IsCopyRunning(pipeName));
    }

    [Fact]
    public void A_plain_start_never_opens_the_pipe()
    {
        var handedOver = Program.TryHandToRunningCopy(
            ["--some-switch"],
            () => throw new InvalidOperationException("A start without a link asked for the pipe."),
            TimeSpan.FromSeconds(5),
            out var kept);

        Assert.False(handedOver);
        Assert.Null(kept);
    }

    [Fact]
    public void A_pipe_that_cannot_even_be_named_leaves_the_link_to_this_start()
    {
        // Nothing can show a message before the app exists, so the start goes on.
        var handedOver = Program.TryHandToRunningCopy(
            ["tallywrapper:handover?d=x&s=y"],
            () => throw new InvalidOperationException("No name."),
            TimeSpan.FromSeconds(5),
            out var kept);

        Assert.False(handedOver);
        Assert.Equal("tallywrapper:handover?d=x&s=y", kept);
    }

    /// <summary>
    /// The exe itself, started as Windows starts it for a link, against a stand-in for the
    /// running copy. This is what shows that <c>Program.Main</c> is the entry point and that the
    /// start ends without opening a window. The Development name and this run's scratch folders
    /// (which the exe inherits) keep it away from a real copy of the app; a build's exe never
    /// registers the link scheme. The stand-in holds the marker a real copy holds from the
    /// moment it starts; without it the exe would rightly take the link itself.
    /// </summary>
    [Fact]
    public async Task The_exe_started_with_a_link_gives_it_to_the_running_copy_and_ends()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "TallyWrapper.exe");
        Assert.True(File.Exists(exe), $"The Desktop's exe is not next to the tests: {exe}");

        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pipeName = HandoverPipe.NameFor(Environments.Development);
        using var running = HandoverPipe.MarkRunning(pipeName);
        using var server = new HandoverPipe.Server(pipeName, link => received.TrySetResult(link));
        server.Start();
        const string Link = "tallywrapper:handover?d=x&s=y";

        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add(Link);
        start.Environment["DOTNET_ENVIRONMENT"] = Environments.Development;
        using var process = Process.Start(start)!;
        try
        {
            Assert.Equal(Link, await received.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            // Had the link not been taken, the exe would have gone on to open the app.
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    [Fact]
    public void The_pipe_name_differs_by_environment()
    {
        Assert.NotEqual(HandoverPipe.NameFor("Production"), HandoverPipe.NameFor("Development"));
        Assert.Equal(HandoverPipe.NameFor("Production"), HandoverPipe.NameFor("Production"));
    }
}
