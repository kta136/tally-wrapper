using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.Views.Admin;
using ShowroomBilling.Desktop.Views.Handover;
using ShowroomBilling.Desktop.Views.Settings;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Lays out the hand-over's toast, result dialog, admin unlock and key dialog in every case of
/// their boards (TwToast, TwResult, TwAdmin, TwSettings), and fails on a binding error. With
/// <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c> set each is saved as <c>Handover-*.png</c>.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class HandoverViewSnapshotTests
{
    private static readonly Size DialogCanvas = new(1240, 600);
    private static readonly Size ToastCanvas = new(520, 360);

    [Fact]
    public void The_hand_over_views_lay_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            foreach (var kind in Enum.GetValues<HandoverSamples.ToastKind>())
            {
                Capture(found, bindingErrors, $"Handover-Toast-{kind}", ToastCanvas, _ => new HandoverToastView { DataContext = HandoverSamples.Toast(kind) });
            }

            foreach (var kind in Enum.GetValues<HandoverSamples.ResultKind>())
            {
                Capture(found, bindingErrors, $"Handover-Result-{kind}", DialogCanvas, shell =>
                {
                    HandoverSamples.ShowResult(shell.Handover.Result, kind);
                    shell.ActiveDialog = ShellDialogs.HandoverResult;
                    return new HandoverResultDialog();
                });
            }

            Capture(found, bindingErrors, "Handover-Admin", DialogCanvas, shell =>
            {
                shell.Admin.IsPasscodeConfigured = true;
                shell.Admin.HandoverContext = "SR/2026-27/14 · Buyer A is in Tally. Updating it needs the admin passcode.";
                return new AdminUnlockDialog();
            });

            Capture(found, bindingErrors, "Handover-Key", DialogCanvas, shell =>
            {
                shell.Settings.Handover.NewKey = "not a key";
                shell.Settings.Handover.SaveKeyCommand.Execute(null);
                return new HandoverKeyDialog();
            });

            Capture(found, bindingErrors, "Handover-Settings", new Size(1280, 800), shell =>
            {
                shell.ActiveTab = NavTab.Settings;
                shell.Settings.SelectedSection = SettingsSectionKey.Handover;
                return new SettingsView { DataContext = shell.Settings };
            });

            Capture(found, bindingErrors, "Handover-Settings-Section", new Size(900, 300), shell =>
                new HandoverSettingsSectionView { DataContext = shell.Settings.Handover });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Capture(
        List<string> problems,
        BindingErrors bindingErrors,
        string name,
        Size canvas,
        Func<MainWindowViewModel, FrameworkElement> view)
    {
        try
        {
            var shell = DesignSample.CreateShell();
            DesignSample.MakeHealthy(shell);
            shell.ConfirmDialog.Cancel();
            var content = view(shell);
            var root = new Grid
            {
                DataContext = shell,
                Width = canvas.Width,
                Height = canvas.Height,
                Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BorderStrongBrush"),
            };
            if (content is HandoverToastView)
            {
                content.HorizontalAlignment = HorizontalAlignment.Right;
                content.VerticalAlignment = VerticalAlignment.Bottom;
                content.Margin = new Thickness(24);
            }

            root.Children.Add(content);
            var window = new Window { Content = root, DataContext = shell };
            for (var pass = 0; pass < 3; pass++)
            {
                root.Measure(canvas);
                root.Arrange(new Rect(canvas));
                root.UpdateLayout();
                WpfTestHost.DrainDispatcher();
            }

            ViewSnapshot.Save(root, name);
            window.Content = null;
        }
        catch (Exception ex)
        {
            problems.Add($"{name}: {ex.GetBaseException().Message}");
        }

        problems.AddRange(bindingErrors.Take().Select(error => $"{name}: {error}"));
    }
}
