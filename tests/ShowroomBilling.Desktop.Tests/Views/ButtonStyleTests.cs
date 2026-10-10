using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The button styles in <c>Styles.xaml</c>: what each draws when idle, under
/// the mouse, pressed and disabled, and that the text inside takes the
/// button's own colour. Hover and press cannot be seen in a snapshot, so they
/// are driven here.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class ButtonStyleTests
{
    [Theory]
    [InlineData("BtnBase", "BgPanelBrush", "BorderStrongBrush", "InkBrush")]
    [InlineData("BtnPrimary", "AccentBrush", "AccentBrush", "InkInverseBrush")]
    [InlineData("BtnGhost", null, null, "InkBrush")]
    [InlineData("BtnDanger", "BgPanelBrush", "ErrBrush", "ErrBrush")]
    public void Idle_button_draws_its_own_brushes_and_its_text_takes_its_colour(
        string style, string? background, string? border, string foreground)
        => WpfTestHost.Run(() =>
        {
            var button = Build(style, "Save");

            var chrome = Chrome(button);
            AssertBrush(background, chrome.Background);
            AssertBrush(border, chrome.BorderBrush);
            AssertBrush(foreground, Texts(button).Single().Foreground);
            return true;
        });

    [Fact]
    public void Text_blocks_placed_inside_a_button_also_take_its_colour()
        => WpfTestHost.Run(() =>
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = "Save" });
            var hint = new Border { Style = (Style)Resource("KbdHintOnPrimary"), Child = new TextBlock { Text = "Ctrl+S" } };
            content.Children.Add(hint);

            var button = Build("BtnPrimary", content);

            Assert.All(Texts(button), text => AssertBrush("InkInverseBrush", text.Foreground));

            button.IsEnabled = false;
            Layout(button);
            Assert.All(Texts(button), text => AssertBrush("InkDisabledBrush", text.Foreground));
            return true;
        });

    [Theory]
    [InlineData("BtnBase", "BgHoverBrush", "BorderStrongBrush", "BgSelectedBrush", "BorderStrongBrush")]
    [InlineData("BtnPrimary", "AccentHoverBrush", "AccentHoverBrush", "AccentPressedBrush", "AccentPressedBrush")]
    [InlineData("BtnSm", "BgHoverBrush", "BorderStrongBrush", "BgSelectedBrush", "BorderStrongBrush")]
    public void Hover_and_press_change_the_fill(
        string style, string hoverBackground, string hoverBorder, string pressedBackground, string pressedBorder)
        => WpfTestHost.Run(() =>
        {
            var button = Build(style, "Save");
            var chrome = Chrome(button);

            SetReadOnly(button, typeof(UIElement), "IsMouseOverPropertyKey", true);
            AssertBrush(hoverBackground, chrome.Background);
            AssertBrush(hoverBorder, chrome.BorderBrush);

            // A pressed button is also under the mouse; press wins.
            SetReadOnly(button, typeof(ButtonBase), "IsPressedPropertyKey", true);
            AssertBrush(pressedBackground, chrome.Background);
            AssertBrush(pressedBorder, chrome.BorderBrush);
            return true;
        });

    [Theory]
    [InlineData("BtnBase", "BgSunkenBrush", "BorderStrongBrush")]
    [InlineData("BtnPrimary", "BgSunkenBrush", "BorderBrushTok")]
    public void A_disabled_button_is_sunken_with_greyed_text(string style, string background, string border)
        => WpfTestHost.Run(() =>
        {
            var button = Build(style, "Save");
            button.IsEnabled = false;
            Layout(button);

            var chrome = Chrome(button);
            AssertBrush(background, chrome.Background);
            AssertBrush(border, chrome.BorderBrush);
            AssertBrush("InkDisabledBrush", Texts(button).Single().Foreground);
            return true;
        });

    private static Button Build(string style, object content)
    {
        var button = new Button { Style = (Style)Resource(style), Content = content };
        Layout(button);
        return button;
    }

    private static void Layout(FrameworkElement element)
    {
        element.Measure(new Size(400, 100));
        element.Arrange(new Rect(element.DesiredSize));
        element.UpdateLayout();
        WpfTestHost.DrainDispatcher();
    }

    private static Border Chrome(Button button) => (Border)button.Template.FindName("bd", button);

    private static List<TextBlock> Texts(DependencyObject root)
    {
        var found = new List<TextBlock>();
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is TextBlock text) found.Add(text);
            found.AddRange(Texts(child));
        }

        return found;
    }

    private static object Resource(string key) => System.Windows.Application.Current.FindResource(key);

    private static void AssertBrush(string? expectedKey, Brush? actual)
    {
        var expected = expectedKey is null ? Brushes.Transparent : (Brush)Resource(expectedKey);
        Assert.Equal(
            ((SolidColorBrush)expected).Color,
            Assert.IsType<SolidColorBrush>(actual).Color);
    }

    // IsMouseOver and IsPressed are read-only: WPF sets them from real input.
    private static void SetReadOnly(DependencyObject target, Type owner, string keyField, object value)
    {
        var key = (DependencyPropertyKey)owner
            .GetField(keyField, BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        target.SetValue(key, value);
    }
}
