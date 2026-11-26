using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ShowroomBilling.Desktop.Shell;

/// <summary>
/// Defers constructing a WPF view until the host is first shown, then
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
    private bool _checkQueued;

    public LazyViewHost()
    {
        Loaded += (_, _) => CheckOnceBindingsHaveRun();
        IsVisibleChanged += (_, _) => CheckOnceBindingsHaveRun();
    }

    /// <summary>
    /// A host can read as visible before its <c>Visibility</c> binding has a value. When the main
    /// window first showed, none of its hosts had one yet, so every tab and every dialog was built at
    /// once (about three seconds of a full core at start-up). The bindings that are still waiting run
    /// at <see cref="DispatcherPriority.DataBind"/>, so the host looks again after them: still visible,
    /// it builds its view, and that is still before the next frame is drawn.
    /// </summary>
    private void CheckOnceBindingsHaveRun()
    {
        if (_view is not null || _checkQueued || !IsVisible)
        {
            return;
        }

        _checkQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.DataBind, () =>
        {
            _checkQueued = false;
            EnsureContentIfVisible();
        });
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
