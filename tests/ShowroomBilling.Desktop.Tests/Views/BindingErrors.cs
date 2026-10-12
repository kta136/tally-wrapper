using System.Diagnostics;
using System.Windows;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>Collects WPF data-binding errors, which are only ever traced.</summary>
internal sealed class BindingErrors : TraceListener
{
    private readonly List<string> _errors = [];
    private SourceLevels _previousLevel;

    public static BindingErrors Attach()
    {
        // Binding tracing is off outside a debugger until refreshed.
        PresentationTraceSources.Refresh();
        var listener = new BindingErrors
        {
            _previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level,
        };
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        return listener;
    }

    public IReadOnlyList<string> Take()
    {
        var taken = _errors.ToList();
        _errors.Clear();
        return taken;
    }

    public override void Write(string? message)
    {
    }

    public override void WriteLine(string? message)
    {
        if (!string.IsNullOrWhiteSpace(message) && !IsTemplateFreezableArtifact(message))
        {
            _errors.Add(message.Trim());
        }
    }

    // The two preview panes scale each page with a ScaleTransform inside an
    // item template, bound to an ancestor. WPF evaluates that binding once
    // on the template's own shared copy of the transform, which has no
    // ancestors, and traces this error; every real item then binds normally.
    private static bool IsTemplateFreezableArtifact(string message)
        => message.Contains("Cannot find source for binding with reference 'RelativeSource FindAncestor", StringComparison.Ordinal)
            && message.Contains("target element is 'ScaleTransform'", StringComparison.Ordinal);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(this);
            PresentationTraceSources.DataBindingSource.Switch.Level = _previousLevel;
        }

        base.Dispose(disposing);
    }
}
