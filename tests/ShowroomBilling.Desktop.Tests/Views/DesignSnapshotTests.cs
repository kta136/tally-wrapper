using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.Views;
using ShowroomBilling.Desktop.Views.Bills;
using ShowroomBilling.Desktop.Views.Invoice;
using ShowroomBilling.Desktop.Views.Settings;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Renders each screen of the redesign with the sample data of its mockup, named like the mockup
/// (<c>docs/design/redesign/Main.png</c> becomes <c>Design-Main.png</c>), so the two can be put side by
/// side. Nothing is saved unless <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c> is set; the screens are laid out
/// and their bindings checked either way.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class DesignSnapshotTests
{
    [Fact]
    public void The_sample_estimate_is_priced_as_in_the_mockup()
    {
        var invoice = WpfTestHost.Run(() => DesignSample.CreateFilledInvoice(DesignSample.CreateSettings()));

        Assert.Equal(4, invoice.ItemCount);
        Assert.Equal([100457.56m, 30486.78m, 125261.34m, 38500.00m], invoice.Lines.Take(4).Select(line => line.LineTotal));
        Assert.Equal(294705.68m, invoice.InclusiveTotal);
        Assert.Equal(286122.02m, invoice.Subtotal);
        Assert.Equal(4291.83m, invoice.Cgst);
        Assert.Equal(289706m, invoice.GrandTotal);
        Assert.Equal("1.7%", invoice.DiscountPercentHint);
        Assert.Equal("7,780", invoice.Rates?.Rate24Kt);
        Assert.Equal("7,126", invoice.Rates?.Rate22Kt);
        Assert.Equal("5,835", invoice.Rates?.Rate18Kt);
    }

    [Fact]
    public void Every_estimate_state_lays_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            Capture(found, bindingErrors, "Design-Main", shell =>
            {
                return new InvoiceView { DataContext = shell.Invoice };
            });

            Capture(found, bindingErrors, "Design-Estimate-New", shell =>
            {
                shell.Invoice.ClearCommand.Execute(null);
                shell.Invoice.Rate24Kt = null;
                shell.Invoice.RateMissing = true;
                return new InvoiceView { DataContext = shell.Invoice };
            });

            Capture(found, bindingErrors, "Design-Estimate-Search", shell =>
            {
                shell.Invoice.IsQuickAddOpen = true;
                shell.Invoice.QuickAddQuery = "22kt";
                return new InvoiceView { DataContext = shell.Invoice };
            });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_limited_mode_page_and_every_banner_lay_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            Capture(found, bindingErrors, "Design-Limited-Mode", shell =>
            {
                shell.SystemState = SystemState.Limited;
                shell.Health.CloudDot = "err";
                shell.Health.DatabaseDot = "err";
                shell.Health.TallyDot = "err";
                return new LimitedModeView { DataContext = shell };
            });

            Capture(found, bindingErrors, "Design-Banner-Info", shell =>
            {
                shell.SystemState = SystemState.Degraded;
                return new InvoiceView { DataContext = shell.Invoice };
            });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_settings_section_lays_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            foreach (var section in Enum.GetValues<SettingsSectionKey>())
            {
                Capture(
                    found,
                    bindingErrors,
                    $"Design-Settings-{section}",
                    shell =>
                    {
                        shell.ActiveTab = NavTab.Settings;
                        shell.Settings.SelectedSection = section;
                        shell.Settings.StatusMessage = string.Empty;
                        return new SettingsView { DataContext = shell.Settings };
                    },
                    adminUnlocked: section == SettingsSectionKey.Admin);
            }

            // The Invoice section while it is being edited: the band turns indigo and offers Discard and Save all.
            Capture(found, bindingErrors, "Design-Settings-Invoice-Editing", shell =>
            {
                shell.ActiveTab = NavTab.Settings;
                shell.Settings.SelectedSection = SettingsSectionKey.Invoice;
                shell.Settings.BeginEditCommand.Execute(null);
                shell.Settings.Draft.PrintCompanyName = "Sample Jewellers Pvt";
                shell.Settings.StatusMessage = string.Empty;
                return new SettingsView { DataContext = shell.Settings };
            });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Every_bills_state_lays_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            Capture(found, bindingErrors, "Design-Bills", shell =>
            {
                shell.ActiveTab = NavTab.Bills;
                return new BillsView { DataContext = shell.Bills };
            });

            Capture(found, bindingErrors, "Design-Bills-Actions", shell =>
            {
                shell.ActiveTab = NavTab.Bills;
                shell.Bills.Items[1].IsSelected = true;
                shell.Bills.Items[4].IsSelected = true;
                return new BillsView { DataContext = shell.Bills };
            }, adminUnlocked: true);

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static void Capture(
        List<string> problems,
        BindingErrors bindingErrors,
        string name,
        Func<MainWindowViewModel, FrameworkElement> screen,
        bool adminUnlocked = false)
    {
        try
        {
            var shell = DesignSample.CreateShell(tokenStore: adminUnlocked ? DesignSample.UnlockedTokens() : null);
            DesignSample.MakeHealthy(shell);
            var view = screen(shell);
            var root = DesignSample.ComposeShell(shell, view);
            var window = new Window { Content = root, DataContext = shell };
            Layout(root);
            ViewSnapshot.Save(root, name);
            window.Content = null;
        }
        catch (Exception ex)
        {
            problems.Add($"{name}: {ex.GetBaseException().Message}");
        }

        problems.AddRange(bindingErrors.Take().Select(error => $"{name}: {error}"));
    }

    private static void Layout(FrameworkElement view)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            view.Measure(DesignSample.WindowSize);
            view.Arrange(new Rect(DesignSample.WindowSize));
            view.UpdateLayout();
            WpfTestHost.DrainDispatcher();
        }
    }
}
