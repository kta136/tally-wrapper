using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using ShowroomBilling.Desktop.Shell;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>The small attached behaviours the views share instead of code-behind.</summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class AttachedBehaviourTests
{
    [Fact]
    public void A_bound_password_box_writes_what_is_typed_and_clears_when_the_view_model_does()
        => WpfTestHost.Run(() =>
        {
            var model = new Credentials();
            var box = new PasswordBox { DataContext = model };
            BindingOperations.SetBinding(box, PasswordBinding.PasswordProperty, new Binding(nameof(Credentials.Passcode)));

            box.Password = "letmein";
            Assert.Equal("letmein", model.Passcode);

            // After an unlock the view model clears the passcode; the dots must go too.
            model.Passcode = string.Empty;
            Assert.Equal(string.Empty, box.Password);

            box.Password = "again";
            Assert.Equal("again", model.Passcode);
            return true;
        });

    [Fact]
    public void A_password_box_without_the_binding_is_left_alone()
        => WpfTestHost.Run(() =>
        {
            var box = new PasswordBox();

            box.Password = "typed";

            Assert.Equal(string.Empty, PasswordBinding.GetPassword(box));
            return true;
        });

    [Fact]
    public void A_field_marked_focus_on_shown_takes_the_focus_each_time_its_dialog_appears()
        => WpfTestHost.Run(() =>
        {
            // Focus needs a real window; this one is off-screen and never activated.
            var field = new TextBox { Text = "0042" };
            FocusOnShown.SetIsEnabled(field, true);
            var other = new TextBox();
            var dialog = new Border { Child = field, Visibility = Visibility.Collapsed };
            var panel = new StackPanel();
            panel.Children.Add(other);
            panel.Children.Add(dialog);
            var window = new Window
            {
                Content = panel,
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
                other.Focus();
                WpfTestHost.DrainDispatcher();
                Assert.NotSame(field, FocusManager.GetFocusedElement(window));

                dialog.Visibility = Visibility.Visible;
                WpfTestHost.DrainDispatcher();
                Assert.Same(field, FocusManager.GetFocusedElement(window));
                Assert.Equal("0042", field.SelectedText);

                // Hidden, focus moved elsewhere, shown again: it takes the focus again.
                dialog.Visibility = Visibility.Collapsed;
                other.Focus();
                dialog.Visibility = Visibility.Visible;
                WpfTestHost.DrainDispatcher();
                Assert.Same(field, FocusManager.GetFocusedElement(window));
            }
            finally
            {
                window.Close();
            }

            return true;
        });

    private sealed class Credentials : INotifyPropertyChanged
    {
        private string _passcode = string.Empty;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Passcode
        {
            get => _passcode;
            set
            {
                _passcode = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Passcode)));
            }
        }
    }
}
