using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class PrintLayoutAssetViewModel : ObservableObject
{
    internal PrintLayoutAssetViewModel(string kind, string title, Func<bool> isIdle,
        Func<PrintLayoutAssetViewModel, Task> upload, Func<PrintLayoutAssetViewModel, Task> delete)
    {
        Kind = kind;
        Title = title;
        UploadCommand = new AsyncRelayCommand(() => upload(this), isIdle);
        DeleteCommand = new AsyncRelayCommand(() => delete(this), () => isIdle() && HasAsset);
    }

    internal string Kind { get; }
    public string Title { get; }
    public bool IsWatermark => Kind == PrintAssetKinds.Watermark;

    // A logo or signature is moved within its own place on the bill; the watermark is placed on the page.
    public string OffsetXLabel => IsWatermark ? "X, cm" : "Offset X, cm";
    public string OffsetYLabel => IsWatermark ? "Y, cm" : "Offset Y, cm";
    public bool HasAsset => AssetId.HasValue || (IsWatermark && PendingBytes is not null);
    public IAsyncRelayCommand UploadCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAsset)), NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private Guid? assetId;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasAsset)), NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    private byte[]? pendingBytes;
    [ObservableProperty] private string fileName = "—";
    [ObservableProperty] private double offsetXCm;
    [ObservableProperty] private double offsetYCm;
    [ObservableProperty] private double widthCm;
    [ObservableProperty] private double heightCm;
    [ObservableProperty] private double opacityPercent = PrintLayoutDefaults.WatermarkOpacityPercent;

    internal PrintLayoutAssetPlacement? Placement => AssetId.HasValue
        ? new(AssetId, OffsetXCm, OffsetYCm, WidthCm, HeightCm) : null;

    internal PrintLayoutWatermarkPlacement? WatermarkPlacement(bool preview) =>
        AssetId.HasValue || (preview && PendingBytes is not null)
            ? new(AssetId ?? Guid.Empty, OffsetXCm, OffsetYCm, WidthCm, HeightCm, OpacityPercent) : null;

    internal void Load(PrintLayoutAssetPlacement? placement)
    {
        AssetId = placement?.AssetId;
        OffsetXCm = placement?.OffsetXCm ?? 0;
        OffsetYCm = placement?.OffsetYCm ?? 0;
        WidthCm = placement?.WidthCm ?? 0;
        HeightCm = placement?.HeightCm ?? 0;
    }

    internal void LoadWatermark(PrintLayoutWatermarkPlacement? placement)
    {
        AssetId = placement?.AssetId;
        var defaults = placement ?? PrintLayoutDefaults.CreateWatermark(Guid.Empty);
        OffsetXCm = defaults.OffsetXCm;
        OffsetYCm = defaults.OffsetYCm;
        WidthCm = defaults.WidthCm;
        HeightCm = defaults.HeightCm;
        OpacityPercent = defaults.OpacityPercent;
    }
}
