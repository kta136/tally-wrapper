using System.Net;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Contracts.Tally;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.Views.Settings;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Lays out Settings → Tally check in each of its states (never run, comparing, differences, all
/// matching, Tally not answering) and fails on a binding error; ViewSmokeTests sees only the first,
/// where the table has no rows. With <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c> set each is saved as
/// <c>TallyCheck-*.png</c>.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class TallyCheckViewTests
{
    private static readonly Size Canvas = new(1280, 800);

    [Fact]
    public void Every_state_of_the_section_lays_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            Capture(found, bindingErrors, "TallyCheck-NotRun", Never(), _ => { });
            Capture(found, bindingErrors, "TallyCheck-Comparing", () => new TaskCompletionSource<TallyComparisonResponse>().Task, Compare);
            Capture(found, bindingErrors, "TallyCheck-Differences", Answer(TallyCheckViewModelTests.Sample()), Compare);
            // The narrowest the window goes: every column, Open bill included, still has to show.
            Capture(found, bindingErrors, "TallyCheck-Differences-1100", Answer(TallyCheckViewModelTests.Sample()), Compare, new Size(1100, 760));
            Capture(
                found,
                bindingErrors,
                "TallyCheck-Notes",
                Answer(TallyCheckViewModelTests.Sample() with { BillsInOtherCompany = 1, PushedWhileComparing = true }),
                Compare);
            Capture(
                found,
                bindingErrors,
                "TallyCheck-ManyRows",
                Answer(TallyCheckViewModelTests.Sample() with
                {
                    Differences = Enumerable.Repeat(TallyCheckViewModelTests.Sample().Differences[0], 120).ToList(),
                }),
                Compare);
            Capture(found, bindingErrors, "TallyCheck-AllMatch", Answer(new TallyComparisonResponse("2026-27", DateTimeOffset.UtcNow, 13, 13, 13, [])), Compare);
            Capture(
                found,
                bindingErrors,
                "TallyCheck-Failed",
                new TallyCheckViewModelTests.ScriptedApi(
                    () => Task.FromResult(TallyCheckViewModelTests.Sample()),
                    () => Task.FromException<TallyComparisonResponse>(
                        new ApiException(HttpStatusCode.ServiceUnavailable, null, "Tally could not be asked.", null))),
                vm =>
                {
                    Compare(vm);
                    Compare(vm);
                });
            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Open_bill_shows_the_bill_in_Bill_details()
    {
        var shown = WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell(settings: DesignSample.CreateSettings(
                tallyCheckApi: new TallyCheckViewModelTests.ScriptedApi(() => Task.FromResult(TallyCheckViewModelTests.Sample()))));
            Compare(shell.Settings.TallyCheck);

            shell.Settings.TallyCheck.OpenBillCommand.Execute(shell.Settings.TallyCheck.ShownRows.First(row => row.HasBill));
            return shell.ActiveDialog;
        });

        Assert.Equal(ShellDialogs.BillDetails, shown);
    }

    private static void Compare(TallyCheckViewModel vm) => _ = vm.CompareCommand.ExecuteAsync(null);

    private static Func<Task<TallyComparisonResponse>> Answer(TallyComparisonResponse response) => () => Task.FromResult(response);

    private static Func<Task<TallyComparisonResponse>> Never() =>
        () => Task.FromException<TallyComparisonResponse>(new InvalidOperationException("not asked"));

    private static void Capture(
        List<string> problems,
        BindingErrors bindingErrors,
        string name,
        Func<Task<TallyComparisonResponse>> answer,
        Action<TallyCheckViewModel> arrange,
        Size? canvas = null)
        => Capture(problems, bindingErrors, name, new TallyCheckViewModelTests.ScriptedApi(answer), arrange, canvas);

    private static void Capture(
        List<string> problems,
        BindingErrors bindingErrors,
        string name,
        ITallyCheckApiClient api,
        Action<TallyCheckViewModel> arrange,
        Size? size = null)
    {
        var canvasSize = size ?? Canvas;
        ViewSnapshot.Capture(problems, bindingErrors, name, canvasSize, () =>
        {
            var shell = DesignSample.CreateShell(settings: DesignSample.CreateSettings(tallyCheckApi: api));
            DesignSample.MakeHealthy(shell);
            shell.ActiveTab = NavTab.Settings;
            shell.Settings.SelectedSection = SettingsSectionKey.TallyCheck;
            arrange(shell.Settings.TallyCheck);
            var view = new SettingsView { DataContext = shell.Settings };
            var root = new Grid { DataContext = shell, Width = canvasSize.Width, Height = canvasSize.Height };
            root.Children.Add(view);
            return root;
        });
    }
}
