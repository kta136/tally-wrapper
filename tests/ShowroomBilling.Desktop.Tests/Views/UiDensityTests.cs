using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.Views;

[Collection(WpfApplicationCollection.Name)]
public sealed class UiDensityTests
{
    [Fact]
    public void Compact_values_match_the_token_sheet_defaults()
    {
        var mismatches = WpfTestHost.Run(() =>
        {
            // Read the token sheet itself, not the application resources: a
            // density applied earlier would shadow the defaults there.
            var tokenSheet = new ResourceDictionary
            {
                Source = new Uri("/TallyWrapper;component/Resources/DesignTokens.xaml", UriKind.Relative),
            };
            return UiDensityManager.Tokens
                .Where(token => !Equals(tokenSheet[token.Key], token.Compact))
                .Select(token => $"{token.Key}: sheet {tokenSheet[token.Key] ?? "missing"}, manager {token.Compact}")
                .ToList();
        });

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Changing_density_resizes_controls_that_are_already_on_screen()
    {
        var (compact, comfortable) = WpfTestHost.Run(() =>
        {
            try
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Compact, persist: false);
                var button = new Button { Content = "Save" };
                var window = new Window { Content = button };
                window.Measure(new Size(400, 300));
                window.Arrange(new Rect(0, 0, 400, 300));
                var before = (button.FontSize, button.Height);

                UiDensityManager.ApplyDensity(UiDensityManager.Comfortable, persist: false);
                WpfTestHost.DrainDispatcher();

                return (before, (button.FontSize, button.Height));
            }
            finally
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Compact, persist: false);
            }
        });

        Assert.Equal((12.5, 28d), compact);
        Assert.Equal((13.5, 34d), comfortable);
    }
}
