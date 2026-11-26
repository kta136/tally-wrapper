using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The progress bar's marquee runs only while the bar can be seen. A running animation makes WPF
/// draw every frame; the Settings preview's bar, indeterminate for good and hidden, kept the app
/// at a third of a core while it sat idle on any tab.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class ProgressBarMarqueeTests
{
    [Fact]
    public void The_marquee_runs_only_while_the_bar_is_indeterminate_and_shown()
    {
        var states = WpfTestHost.Run(() =>
        {
            var bar = new ProgressBar { Width = 150, IsIndeterminate = true };
            var holder = new Border { Child = bar, Visibility = Visibility.Collapsed };
            var window = new Window
            {
                Content = holder,
                Left = -32000,
                Top = -32000,
                Width = 300,
                Height = 120,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };

            try
            {
                window.Show();
                // Built while hidden, as the app's views are at start-up: the template is there.
                bar.ApplyTemplate();
                // A stopped storyboard leaves its clock on the transform, so whether the transform
                // has an animation does not tell; the state of the template's storyboard does.
                var marquee = bar.Template.Triggers
                    .SelectMany(trigger => trigger.EnterActions)
                    .OfType<BeginStoryboard>()
                    .Single()
                    .Storyboard;
                bool Running()
                {
                    WpfTestHost.DrainDispatcher();
                    try
                    {
                        return marquee.GetCurrentState(bar) == ClockState.Active;
                    }
                    catch (InvalidOperationException)
                    {
                        return false;   // never begun on this bar
                    }
                }

                var hidden = Running();
                holder.Visibility = Visibility.Visible;
                var shown = Running();
                bar.IsIndeterminate = false;
                var determinate = Running();
                bar.IsIndeterminate = true;
                var again = Running();
                holder.Visibility = Visibility.Collapsed;
                var hiddenAgain = Running();
                return (hidden, shown, determinate, again, hiddenAgain);
            }
            finally
            {
                window.Close();
            }
        });

        Assert.Equal((false, true, false, true, false), states);
    }
}
