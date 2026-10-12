using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests.Views;

[Collection(WpfApplicationCollection.Name)]
public sealed class UiDensityTests
{
    [Fact]
    public void Standard_values_match_the_token_sheet_defaults()
    {
        var mismatches = WpfTestHost.Run(() =>
        {
            // Read the token sheet itself, not the application resources: a
            // density applied earlier would shadow the defaults there.
            var tokenSheet = new ResourceDictionary
            {
                Source = new Uri("/TallyWrapper;component/Resources/DesignTokens.xaml", UriKind.Relative),
            };
            var problems = UiDensityManager.Tokens
                .Where(token => !Equals(tokenSheet[token.Key], token.Standard))
                .Select(token => $"{token.Key}: sheet {tokenSheet[token.Key] ?? "missing"}, manager {token.Standard}")
                .ToList();

            var cellPadding = tokenSheet["CellPadding"] as Thickness?;
            if (cellPadding != new Thickness(10, 0, 10, 0))
            {
                problems.Add($"CellPadding: sheet {cellPadding}, expected 10,0,10,0");
            }

            return problems;
        });

        Assert.True(mismatches.Count == 0, string.Join(Environment.NewLine, mismatches));
    }

    [Fact]
    public void Every_token_grows_from_compact_to_standard_to_comfortable()
    {
        var notIncreasing = UiDensityManager.Tokens
            .Where(token => !(token.Compact <= token.Standard && token.Standard <= token.Comfortable))
            .Select(token => token.Key)
            .ToList();

        Assert.True(notIncreasing.Count == 0, string.Join(", ", notIncreasing));
    }

    [Fact]
    public void Changing_density_resizes_controls_that_are_already_on_screen()
    {
        var (compact, standard, comfortable) = WpfTestHost.Run(() =>
        {
            try
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Compact, persist: false);
                var button = new Button { Content = "Save" };
                var window = new Window { Content = button };
                window.Measure(new Size(400, 300));
                window.Arrange(new Rect(0, 0, 400, 300));
                var before = (button.FontSize, button.Height);

                UiDensityManager.ApplyDensity(UiDensityManager.Standard, persist: false);
                WpfTestHost.DrainDispatcher();
                var middle = (button.FontSize, button.Height);

                UiDensityManager.ApplyDensity(UiDensityManager.Comfortable, persist: false);
                WpfTestHost.DrainDispatcher();

                return (before, middle, (button.FontSize, button.Height));
            }
            finally
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Standard, persist: false);
            }
        });

        Assert.Equal((12.5, 30d), compact);
        Assert.Equal((13.5, 34d), standard);
        Assert.Equal((14.5, 40d), comfortable);
    }

    [Fact]
    public void The_cell_padding_a_table_reads_follows_the_density()
    {
        var padding = WpfTestHost.Run(() =>
        {
            try
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Compact, persist: false);
                var compact = (Thickness)System.Windows.Application.Current.Resources["CellPadding"];
                UiDensityManager.ApplyDensity(UiDensityManager.Comfortable, persist: false);
                var comfortable = (Thickness)System.Windows.Application.Current.Resources["CellPadding"];
                return (compact, comfortable);
            }
            finally
            {
                UiDensityManager.ApplyDensity(UiDensityManager.Standard, persist: false);
            }
        });

        Assert.Equal(new Thickness(8, 0, 8, 0), padding.compact);
        Assert.Equal(new Thickness(12, 0, 12, 0), padding.comfortable);
    }

    [Theory]
    [InlineData("Compact", 1, "Standard")]
    [InlineData("Comfortable", 1, "Comfortable")]
    [InlineData("Compact", 2, "Compact")]
    [InlineData("Standard", 2, "Standard")]
    [InlineData("Comfortable", 2, "Comfortable")]
    [InlineData("nonsense", 2, "Standard")]
    public void A_stored_density_keeps_its_meaning_across_the_move_to_three_levels(
        string stored, int version, string expected)
        => Assert.Equal(expected, UiDensityManager.Migrate(new UiDensityManager.UiPreferences(stored, version)));

    [Fact]
    public void A_missing_preferences_file_means_standard()
        => Assert.Equal(UiDensityManager.Standard, UiDensityManager.Migrate(null));
}
