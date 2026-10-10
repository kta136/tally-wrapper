namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Tests that depend on the process-wide WPF Application run one at a time.
/// The view tests block its dispatcher thread while they lay views out, and
/// the settings preview marshals its state updates onto that same thread once
/// an Application exists, so the two must not overlap.
/// </summary>
[CollectionDefinition(Name)]
public sealed class WpfApplicationCollection
{
    public const string Name = "WPF application";
}
