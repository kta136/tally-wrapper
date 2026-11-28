using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Tests.Views;

[Collection(WpfApplicationCollection.Name)]
public sealed class InlineBannerTests
{
    [Fact]
    public void A_long_error_wraps_and_emphasizing_the_message_keeps_the_glyph_at_normal_weight()
        => WpfTestHost.Run(() =>
        {
            var banner = new InlineBanner
            {
                Style = (Style)System.Windows.Application.Current.FindResource("AdminBannerErr"),
                FontWeight = FontWeights.SemiBold,
                Message = "Try again.",
            };
            var available = new Size(180, 300);
            _ = new Window { Content = banner };
            WpfTestHost.Layout(banner, available);
            var label = (TextBlock)banner.Template.FindName("Label", banner);
            var singleLineHeight = label.ActualHeight;

            banner.Message = "The passcode could not be checked because the server did not answer. Try again.";
            WpfTestHost.Layout(banner, available);
            var icon = (TextBlock)banner.Template.FindName("Icon", banner);

            Assert.Equal(banner.Message, label.Text);
            Assert.Equal(banner.Message, UIElementAutomationPeer.CreatePeerForElement(banner).GetName());
            Assert.True(label.ActualHeight > singleLineHeight,
                $"Expected a wrapped message taller than {singleLineHeight}, got {label.ActualHeight}.");
            Assert.Equal(FontWeights.SemiBold, label.FontWeight);
            Assert.Equal(FontWeights.Normal, icon.FontWeight);

            banner.Glyph = null;
            Assert.Equal(Visibility.Collapsed, icon.Visibility);
            return true;
        });
}
