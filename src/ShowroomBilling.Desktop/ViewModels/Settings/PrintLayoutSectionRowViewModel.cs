using CommunityToolkit.Mvvm.ComponentModel;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public sealed partial class PrintLayoutSectionRowViewModel : ObservableObject
{
    private bool _isVisible;

    public PrintLayoutSectionRowViewModel(
        string sectionKey,
        string displayName,
        bool canHide,
        bool isVisible,
        double spacingBeforeMm,
        double spacingAfterMm)
    {
        SectionKey = sectionKey;
        DisplayName = displayName;
        CanHide = canHide;
        _isVisible = canHide ? isVisible : true;
        SpacingBeforeMm = spacingBeforeMm;
        SpacingAfterMm = spacingAfterMm;
    }

    public string SectionKey { get; }

    public string DisplayName { get; }

    public bool CanHide { get; }

    public bool IsVisible
    {
        get => _isVisible;
        set => SetProperty(ref _isVisible, CanHide ? value : true);
    }

    [ObservableProperty] private double spacingBeforeMm;

    [ObservableProperty] private double spacingAfterMm;

    [ObservableProperty] private bool isBottomPinned;
}
