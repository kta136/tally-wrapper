using System;
using System.Windows;
using System.Windows.Controls;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Defers constructing a WPF view until the host first becomes visible, then
/// keeps the realized view cached for later tab/dialog activations.
/// </summary>
public sealed class LazyViewHost : ContentControl
{
    public static readonly DependencyProperty ViewTypeProperty =
        DependencyProperty.Register(
            nameof(ViewType),
            typeof(Type),
            typeof(LazyViewHost),
            new PropertyMetadata(null, OnViewTypeChanged));

    public static readonly DependencyProperty ContentDataContextProperty =
        DependencyProperty.Register(
            nameof(ContentDataContext),
            typeof(object),
            typeof(LazyViewHost),
            new PropertyMetadata(null, OnContentDataContextChanged));

    private FrameworkElement? _view;

    public LazyViewHost()
    {
        Loaded += (_, _) => EnsureContentIfVisible();
        IsVisibleChanged += (_, _) => EnsureContentIfVisible();
    }

    public Type? ViewType
    {
        get => (Type?)GetValue(ViewTypeProperty);
        set => SetValue(ViewTypeProperty, value);
    }

    public object? ContentDataContext
    {
        get => GetValue(ContentDataContextProperty);
        set => SetValue(ContentDataContextProperty, value);
    }

    private static void OnViewTypeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LazyViewHost host) return;

        host._view = null;
        host.Content = null;
        host.EnsureContentIfVisible();
    }

    private static void OnContentDataContextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is LazyViewHost { _view: not null } host && e.NewValue is not null)
        {
            host._view.DataContext = e.NewValue;
        }
    }

    private void EnsureContentIfVisible()
    {
        if (!IsVisible || _view is not null)
        {
            return;
        }

        if (ViewType is null)
        {
            return;
        }

        if (!typeof(FrameworkElement).IsAssignableFrom(ViewType))
        {
            throw new InvalidOperationException($"{nameof(ViewType)} must derive from {nameof(FrameworkElement)}.");
        }

        _view = (FrameworkElement)(Activator.CreateInstance(ViewType)
            ?? throw new InvalidOperationException($"Could not create view {ViewType.FullName}."));

        if (ContentDataContext is not null)
        {
            _view.DataContext = ContentDataContext;
        }

        Content = _view;
    }
}
