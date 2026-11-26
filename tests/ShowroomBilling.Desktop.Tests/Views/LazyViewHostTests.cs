using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// A host builds its view when it is first shown, not when the window first shows. Built the way
/// the main window's markup builds them (the visibility binding set before the host has a parent,
/// the data context set after), every host read as visible at the window's first layout, and all
/// the tabs and dialogs were built at once: about three seconds of a full core at start-up.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class LazyViewHostTests
{
    [Fact]
    public void Only_the_host_that_is_shown_builds_its_view_and_a_hidden_one_builds_when_it_is_shown()
    {
        var seen = WpfTestHost.Run(() =>
        {
            var shown = Host(typeof(TextBlock), "Shown");
            var hidden = Host(typeof(Border), "Hidden");
            var window = new Window
            {
                Content = new Grid { Children = { shown, hidden } },
                Left = -32000,
                Top = -32000,
                Width = 300,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            window.DataContext = new { Shown = true, Hidden = false };

            try
            {
                window.Show();
                WpfTestHost.DrainDispatcher();
                var atStart = (Shown: shown.Content?.GetType().Name, Hidden: hidden.Content?.GetType().Name);

                hidden.Visibility = Visibility.Visible;
                WpfTestHost.DrainDispatcher();
                return (atStart, OnceShown: hidden.Content?.GetType().Name);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal(("TextBlock", (string?)null), seen.atStart);
        Assert.Equal("Border", seen.OnceShown);
    }

    [Fact]
    public void A_dialog_built_when_it_first_opens_still_puts_the_cursor_in_its_field()
    {
        // Every dialog used to exist before it first opened; now the first opening builds it.
        var seen = WpfTestHost.Run(() =>
        {
            var host = Host(typeof(FocusingDialog), "Hidden");
            var other = new TextBox();
            var window = new Window
            {
                Content = new Grid { Children = { other, host } },
                Left = -32000,
                Top = -32000,
                Width = 300,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            window.DataContext = new { Hidden = false };

            try
            {
                window.Show();
                other.Focus();
                WpfTestHost.DrainDispatcher();
                var builtBefore = host.Content is not null;

                host.Visibility = Visibility.Visible;
                WpfTestHost.DrainDispatcher();
                var field = ((FocusingDialog?)host.Content)?.Field;
                return (builtBefore, Focused: field is not null && ReferenceEquals(field, FocusManager.GetFocusedElement(window)), Selected: field?.SelectedText);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal((false, true, "0042"), seen);
    }

    private sealed class FocusingDialog : Border
    {
        public FocusingDialog()
        {
            Field = new TextBox { Text = "0042" };
            FocusOnShown.SetIsEnabled(Field, true);
            Child = Field;
        }

        public TextBox Field { get; }
    }

    private static LazyViewHost Host(Type view, string shownBy)
    {
        var host = new LazyViewHost { ViewType = view };
        host.SetBinding(UIElement.VisibilityProperty, new Binding(shownBy) { Converter = new BooleanToVisibilityConverter() });
        return host;
    }
}
