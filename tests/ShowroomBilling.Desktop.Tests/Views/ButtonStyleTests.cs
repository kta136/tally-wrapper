using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ShowroomBilling.Desktop.Shell;

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
            AssertBrush(foreground, Label(button).Foreground);
            return true;
        });

    [Fact]
    public void Text_blocks_placed_inside_a_button_also_take_its_colour()
        => WpfTestHost.Run(() =>
        {
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = "Save" });
            content.Children.Add(new TextBlock { Text = "now" });

            var button = Build("BtnPrimary", content);

            Assert.All(Texts(button), text => AssertBrush("InkInverseBrush", text.Foreground));

            button.IsEnabled = false;
            Layout(button);
            Assert.All(Texts(button), text => AssertBrush("InkDisabledBrush", text.Foreground));
            return true;
        });

    [Fact]
    public void A_button_shows_its_keyboard_hint_after_the_label_and_nothing_when_it_has_none()
        => WpfTestHost.Run(() =>
        {
            var plain = Build("BtnSm", "Push to Tally");
            var hinted = new Button { Style = (Style)Resource("BtnSm"), Content = "Retry Push" };
            Kbd.SetHint(hinted, "Ctrl+R");
            Layout(hinted);

            Assert.Equal(Visibility.Collapsed, Hint(plain).Visibility);
            Assert.Equal(Visibility.Visible, Hint(hinted).Visibility);
            Assert.Equal(["Retry Push", "Ctrl+R"], Texts(hinted).Select(text => text.Text));
            Assert.True(hinted.DesiredSize.Width > plain.DesiredSize.Width - 20);

            // The chip greys out with the button.
            hinted.IsEnabled = false;
            Layout(hinted);
            Assert.All(Texts(hinted), text => AssertBrush("InkDisabledBrush", text.Foreground));
            return true;
        });

    [Theory]
    [InlineData("Kbd", "InkBrush", "BgSunkenBrush")]
    [InlineData("KbdOnPrimary", "OnFilledInkBrush", "OnFilledChipBrush")]
    public void A_stand_alone_keyboard_chip_shows_its_key(string style, string foreground, string background)
        => WpfTestHost.Run(() =>
        {
            var chip = new ContentControl { Style = (Style)Resource(style), Content = "F2" };
            Layout(chip);

            var text = Texts(chip).Single();
            Assert.Equal("F2", text.Text);
            AssertBrush(foreground, text.Foreground);
            AssertBrush(background, ((Border)VisualTreeHelper.GetChild(chip, 0)).Background);
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
            AssertBrush("InkDisabledBrush", Label(button).Foreground);
            return true;
        });

    [Fact]
    public void The_chip_on_a_primary_button_stays_readable_when_the_button_is_disabled()
        => WpfTestHost.Run(() =>
        {
            var chip = new ContentControl { Style = (Style)Resource("KbdOnPrimary"), Content = "Ctrl+S" };
            var button = Build("BtnPrimary", chip);
            AssertBrush("OnFilledInkBrush", Texts(chip).Single().Foreground);

            button.IsEnabled = false;
            Layout(button);

            AssertBrush("InkDisabledBrush", Texts(chip).Single().Foreground);
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

    private static Border Hint(Button button) => (Border)button.Template.FindName("hint", button);

    // The label comes first; the hint chip's text, shown or not, follows it.
    private static TextBlock Label(Button button) => Texts(button)[0];

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
