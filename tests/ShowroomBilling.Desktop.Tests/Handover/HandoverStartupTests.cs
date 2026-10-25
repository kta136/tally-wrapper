using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.Handover;

/// <summary>The link scheme's registration and its guard, and the pipe a second start hands a link over.</summary>
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
    public void The_pipe_name_differs_by_environment()
    {
        Assert.NotEqual(HandoverPipe.NameFor("Production"), HandoverPipe.NameFor("Development"));
        Assert.Equal(HandoverPipe.NameFor("Production"), HandoverPipe.NameFor("Production"));
    }
}
