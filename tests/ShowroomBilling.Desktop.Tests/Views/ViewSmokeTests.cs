using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;
using ShowroomBilling.Desktop.Views;
using ShowroomBilling.Desktop.Views.Bills;
using ShowroomBilling.Desktop.Views.Invoice;
using ShowroomBilling.Desktop.Views.Settings;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Loads every view against the real shell view model and fails on a XAML
/// load error or a binding path error. <c>dotnet build</c> only proves the
/// XAML compiles; a missing resource key, a markup extension on the wrong
/// kind of property or a misspelt binding path all surface at run time.
/// This does not replace launching the app: it cannot see layout, focus or
/// keyboard flow.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class ViewSmokeTests
{
    [Fact]
    public void Every_view_loads_and_binds_without_errors()
    {
        var problems = WpfTestHost.Run(LoadEveryView);

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static readonly Size ViewSize = new(1280, 800);

    private static List<string> LoadEveryView()
    {
        var problems = new List<string>();
        var shell = CreateShell();
        var window = new Window { DataContext = shell };

        using var bindingErrors = BindingErrorListener.Attach();
        foreach (var viewType in ViewTypes())
        {
            try
            {
                var view = (FrameworkElement)Activator.CreateInstance(viewType)!;
                if (DataContextFor(viewType, shell) is { } dataContext)
                {
                    view.DataContext = dataContext;
                }

                // The window is never shown; it is the view's logical parent so
                // AncestorType=Window bindings resolve. The view itself is
                // measured and arranged, which applies its templates and
                // creates its rows exactly as a real layout pass would.
                window.Content = view;
                if (viewType == typeof(SettingsView))
                {
                    // A collapsed section is never measured, so each one is shown in turn.
                    foreach (var section in Enum.GetValues<SettingsSectionKey>())
                    {
                        shell.Settings.SelectedSection = section;
                        Layout(view);
                        ViewSnapshot.Save(view, $"{viewType.Name}-{section}");
                    }
                }
                else
                {
                    Layout(view);
                    ViewSnapshot.Save(view, viewType.Name);
                }
            }
            catch (Exception ex)
            {
                problems.Add($"{viewType.Name}: {ex.GetBaseException().Message}");
            }

            problems.AddRange(bindingErrors.Take().Select(error => $"{viewType.Name}: {error}"));
            window.Content = null;
        }

        return problems;
    }

    private static void Layout(FrameworkElement view)
    {
        // Bindings resolve at a lower dispatcher priority than layout, so the
        // first pass sizes the view and the second lays out what the bindings
        // then filled in.
        for (var pass = 0; pass < 2; pass++)
        {
            view.Measure(ViewSize);
            view.Arrange(new Rect(ViewSize));
            view.UpdateLayout();
            WpfTestHost.DrainDispatcher();
        }
    }

    /// <summary>
    /// Every UserControl in the Desktop's Views namespace. The settings
    /// sections are skipped here because SettingsView hosts them itself,
    /// each with the data context it expects.
    /// </summary>
    private static IEnumerable<Type> ViewTypes()
        => typeof(BillsView).Assembly.GetTypes()
            .Where(type => typeof(UserControl).IsAssignableFrom(type)
                && !type.IsAbstract
                && type.Namespace?.StartsWith("ShowroomBilling.Desktop.Views", StringComparison.Ordinal) == true
                && (type.Namespace != typeof(SettingsView).Namespace || type == typeof(SettingsView)))
            .OrderBy(type => type.FullName, StringComparer.Ordinal);

    // Mirrors the data contexts MainWindow.xaml hands out; any other view
    // inherits the shell view model from the window, as the dialogs do.
    private static object? DataContextFor(Type viewType, MainWindowViewModel shell)
    {
        if (viewType == typeof(InvoiceView)) return shell.Invoice;
        if (viewType == typeof(BillsView)) return shell.Bills;
        if (viewType == typeof(SettingsView)) return shell.Settings;
        if (viewType == typeof(TitleBarView)) return shell.TitleBar;
        if (viewType == typeof(FKeyStripView)) return shell.FKeys;
        if (viewType == typeof(StatusBarView)) return shell.StatusBar;
        return null;
    }

    private static MainWindowViewModel CreateShell()
    {
        var tokenStore = new AdminTokenStore();
        var bills = new BillsViewModel { HideFullyPostedDays = false };
        // One row per state, so the row template, its context menu and every
        // chip tone are realised.
        var number = 0;
        foreach (var state in new[]
        {
            BillStates.Posted, BillStates.Pending, BillStates.Draft, BillStates.Posting, BillStates.Failed,
            BillStates.ReconciliationRequired, BillStates.Revised, BillStates.Voided,
        })
        {
            number++;
            bills.Items.Add(new BillListRowViewModel(new BillSummaryItem(
                Guid.NewGuid(),
                state,
                $"SR/2026-27/{number:0000}",
                "Walk-in Customer",
                new DateOnly(2026, 10, number <= 4 ? 10 : 9),
                1000m * number,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow)));
        }

        bills.Items[1].IsSelected = true;

        // The constructors only store their clients; no view-model method that
        // would call one is invoked here.
        var shell = new MainWindowViewModel(
            runtimeApiClient: null!,
            settingsApiClient: null!,
            healthApiClient: null!,
            mastersApiClient: null!,
            billsApiClient: null!,
            bootstrapOptions: Options.Create(new DesktopBootstrapOptions()),
            companyProfileProvider: null!,
            printLayoutOptionsProvider: null!,
            printDispatcher: null!,
            printPreferences: null!,
            adminTokenStore: tokenStore,
            invoice: new InvoiceViewModel(),
            bills: bills,
            billDetails: new BillDetailsViewModel(),
            printPreview: new PrintPreviewViewModel(),
            settings: new SettingsViewModel(),
            admin: new AdminUnlockViewModel(adminApi: null!, leaseApi: null!, tokenStore),
            setupWizard: new SetupWizardViewModel(
                runtimeApi: null!,
                healthApi: null!,
                settingsApi: null!,
                childProcessSupervisor: null,
                completionStore: null!),
            syntheticBatch: new SyntheticBatchViewModel());

        // Left open so the confirm overlay has its texts; nothing awaits the answer.
        _ = shell.ConfirmDialog.ShowAsync(BillConfirmations.Repost("SR/2026-27/0001", tallyMayHoldVoucher: true));
        return shell;
    }

    /// <summary>Collects WPF data-binding errors, which are only ever traced.</summary>
    private sealed class BindingErrorListener : TraceListener
    {
        private readonly List<string> _errors = [];
        private SourceLevels _previousLevel;

        public static BindingErrorListener Attach()
        {
            // Binding tracing is off outside a debugger until refreshed.
            PresentationTraceSources.Refresh();
            var listener = new BindingErrorListener
            {
                _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level,
            };
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            return listener;
        }

        public IReadOnlyList<string> Take()
        {
            var taken = _errors.ToList();
            _errors.Clear();
            return taken;
        }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message) && !IsTemplateFreezableArtifact(message))
            {
                _errors.Add(message.Trim());
            }
        }

        // The two preview panes scale each page with a ScaleTransform inside an
        // item template, bound to an ancestor. WPF evaluates that binding once
        // on the template's own shared copy of the transform, which has no
        // ancestors, and traces this error; every real item then binds normally.
        private static bool IsTemplateFreezableArtifact(string message)
            => message.Contains("Cannot find source for binding with reference 'RelativeSource FindAncestor", StringComparison.Ordinal)
                && message.Contains("target element is 'ScaleTransform'", StringComparison.Ordinal);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
                PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
            }

            base.Dispose(disposing);
        }
    }
}
