using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.Views;
using ShowroomBilling.Desktop.Views.Bills;
using ShowroomBilling.Desktop.Views.Handover;
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

    [Fact]
    public void Bottom_bar_follows_the_active_company_when_settings_reload()
    {
        WpfTestHost.Run(() =>
        {
            var shell = DesignSample.CreateShell();
            var settings = shell.Settings;
            var view = new BottomBarView { DataContext = shell };
            WpfTestHost.Layout(view, ViewSize);
            var label = Assert.Single(Descendants(view).OfType<TextBlock>(), block => block.Text == settings.ActiveCompanyName);
            var current = settings.Settings!;
            var changed = current with { Connection = current.Connection with { ActiveCompanyName = "Second company" } };
            settings.ApplyEffectiveSettings(new("cloud", "Updated", changed, [], [], DateTimeOffset.UtcNow));
            WpfTestHost.DrainDispatcher();
            Assert.Equal("Second company", label.Text);
            return true;
        });
    }

    [Fact]
    public void Settings_sidebar_shows_its_section_and_preview_and_returns_to_Server_on_lock()
    {
        WpfTestHost.Run(() =>
        {
            var tokens = DesignSample.UnlockedTokens();
            var shell = DesignSample.CreateShell(tokenStore: tokens);
            var settings = shell.Settings;
            var view = new SettingsView { DataContext = settings };
            WpfTestHost.Layout(view, ViewSize);
            var sidebar = Descendants(view).OfType<ListBox>().Single(list => list.ItemsSource == settings.SectionGroups);
            var panels = (Panel)Descendants(view).OfType<ServerSettingsSectionView>().Single().Parent;
            var preview = Descendants(view).OfType<SettingsPreviewPaneView>().Single();

            foreach (var section in Enum.GetValues<SettingsSectionKey>())
            {
                sidebar.SelectedItem = section;
                WpfTestHost.Layout(view, ViewSize);
                Assert.Equal(section, settings.SelectedSection);
                var shown = Assert.Single(panels.Children.OfType<UserControl>(), panel => panel.Visibility == Visibility.Visible);
                Assert.Equal($"{section}SettingsSectionView", shown.GetType().Name);
                var showsPreview = section is SettingsSectionKey.Invoice or SettingsSectionKey.PrintLayout;
                Assert.Equal(showsPreview, preview.Visibility == Visibility.Visible);
                Assert.Equal(showsPreview, settings.Preview.IsActive);
            }

            sidebar.SelectedItem = SettingsSectionKey.Admin;
            tokens.Clear();
            WpfTestHost.DrainDispatcher();
            Assert.DoesNotContain(SettingsSectionKey.Admin, settings.Sections);
            Assert.Equal(SettingsSectionKey.Server, sidebar.SelectedItem);
            Assert.Equal(Visibility.Visible, panels.Children.OfType<ServerSettingsSectionView>().Single().Visibility);
            return true;
        });
    }

    private static readonly Size ViewSize = new(1280, 800);

    private static List<string> LoadEveryView()
    {
        var problems = new List<string>();
        var shell = DesignSample.CreateShell();
        var window = new Window { DataContext = shell };

        using var bindingErrors = BindingErrors.Attach();
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
                        WpfTestHost.Layout(view, ViewSize);
                        ViewSnapshot.Save(view, $"{viewType.Name}-{section}");
                    }
                }
                else
                {
                    WpfTestHost.Layout(view, ViewSize);
                    ViewSnapshot.Save(view, viewType.Name);
                }

                problems.AddRange(InertMenuItems(view).Select(item => $"{viewType.Name}: {item}"));
            }
            catch (Exception ex)
            {
                problems.Add($"{viewType.Name}: {ex.GetBaseException().Message}");
            }

            problems.AddRange(bindingErrors.Take().Select(error => $"{viewType.Name}: {error}"));
            window.Content = null;
        }

        // The shell itself: its XAML, the dialog hosts and the chrome around
        // the tabs. It is never shown, so the tab and dialog views stay
        // unrealised here; the loop above covered them.
        try
        {
            var mainWindow = new MainWindow(shell);
            var root = (FrameworkElement)mainWindow.Content;
            WpfTestHost.Layout(root, ViewSize);
            ViewSnapshot.Save(root, nameof(MainWindow));
        }
        catch (Exception ex)
        {
            problems.Add($"{nameof(MainWindow)}: {ex.GetBaseException().Message}");
        }

        problems.AddRange(bindingErrors.Take().Select(error => $"{nameof(MainWindow)}: {error}"));
        return problems;
    }

    /// <summary>
    /// A context menu is a separate tree that only connects to its owner when
    /// it opens, so a menu item bound to the wrong view model does nothing and
    /// reports nothing. This connects each menu the way opening it would and
    /// names every item whose command binding found no command.
    /// </summary>
    private static List<string> InertMenuItems(DependencyObject root)
    {
        var inert = new List<string>();
        foreach (var owner in Descendants(root).OfType<FrameworkElement>())
        {
            if (owner.ContextMenu is not { } menu)
            {
                continue;
            }

            menu.PlacementTarget = owner;
            menu.DataContext = owner.DataContext;
            WpfTestHost.DrainDispatcher();
            inert.AddRange(menu.Items.OfType<MenuItem>()
                .Where(item => BindingOperations.GetBindingExpression(item, MenuItem.CommandProperty) is not null
                    && item.Command is null)
                .Select(item => $"context menu item '{item.Header}' is bound to a command that was not found"));
        }

        return inert.Distinct().ToList();
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
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
    // inherits the shell view model from the window, as the dialogs do. The
    // hand-over toast lives in a window of its own, with its own view model.
    private static object? DataContextFor(Type viewType, MainWindowViewModel shell)
    {
        if (viewType == typeof(InvoiceView)) return shell.Invoice;
        if (viewType == typeof(BillsView)) return shell.Bills;
        if (viewType == typeof(SettingsView)) return shell.Settings;
        if (viewType == typeof(HandoverToastView)) return HandoverSamples.Toast(HandoverSamples.ToastKind.Two);
        return null;
    }
}
