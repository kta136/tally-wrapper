using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Lets a <see cref="PasswordBox"/> bind its text, which WPF does not allow
/// directly: <c>shell:PasswordBinding.Password="{Binding Passcode}"</c>.
/// Two-way, so a view model that clears the passcode also clears the box.
/// </summary>
public static class PasswordBinding
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.RegisterAttached(
        "Password",
        typeof(string),
        typeof(PasswordBinding),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnPasswordPropertyChanged));

    static PasswordBinding()
    {
        // One handler for every PasswordBox; it acts only on boxes that carry the binding.
        EventManager.RegisterClassHandler(
            typeof(PasswordBox),
            PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler(OnPasswordBoxChanged));
    }

    public static string GetPassword(DependencyObject element) => (string)element.GetValue(PasswordProperty);

    public static void SetPassword(DependencyObject element, string value) => element.SetValue(PasswordProperty, value);

    // View model → box.
    private static void OnPasswordPropertyChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        var password = e.NewValue as string ?? string.Empty;
        if (element is PasswordBox box && box.Password != password)
        {
            box.Password = password;
        }
    }

    // Box → view model.
    private static void OnPasswordBoxChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box
            && BindingOperations.IsDataBound(box, PasswordProperty)
            && GetPassword(box) != box.Password)
        {
            box.SetCurrentValue(PasswordProperty, box.Password);
        }
    }
}
