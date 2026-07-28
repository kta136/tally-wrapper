using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ShowroomBilling.Contracts.PrintAssets;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class PrintLayoutViewModel : ObservableObject
{
    private readonly ISettingsApiClient? _settingsApi;
    private readonly IPrintAssetApiClient? _printAssetApi;
    private readonly IPrintLayoutImagePicker _imagePicker;

    public PrintLayoutViewModel() : this(null, null) { }

    public PrintLayoutViewModel(ISettingsApiClient? settingsApi, IPrintAssetApiClient? printAssetApi)
        : this(settingsApi, printAssetApi, new WpfPrintLayoutImagePicker())
    {
    }

    internal PrintLayoutViewModel(
        ISettingsApiClient? settingsApi,
        IPrintAssetApiClient? printAssetApi,
        IPrintLayoutImagePicker imagePicker)
    {
        _settingsApi = settingsApi;
        _printAssetApi = printAssetApi;
        _imagePicker = imagePicker;

        Assets = new ObservableCollection<PrintAssetResponse>();
        SectionLayouts = new ObservableCollection<PrintLayoutSectionRowViewModel>();

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        UploadLogoCommand = new AsyncRelayCommand(() => UploadAsync(PrintAssetKinds.Logo), () => !IsBusy);
        UploadSignatureCommand = new AsyncRelayCommand(() => UploadAsync(PrintAssetKinds.Signature), () => !IsBusy);
        UploadWatermarkCommand = new AsyncRelayCommand(() => UploadAsync(PrintAssetKinds.Watermark), () => !IsBusy);
        DeleteLogoCommand = new AsyncRelayCommand(() => DeleteByKindAsync(PrintAssetKinds.Logo),
            () => !IsBusy && LogoAssetId is not null);
        DeleteSignatureCommand = new AsyncRelayCommand(() => DeleteByKindAsync(PrintAssetKinds.Signature),
            () => !IsBusy && SignatureAssetId is not null);
        DeleteWatermarkCommand = new AsyncRelayCommand(() => DeleteByKindAsync(PrintAssetKinds.Watermark),
            () => !IsBusy && HasWatermark);
        MoveSectionUpCommand = new RelayCommand<PrintLayoutSectionRowViewModel>(MoveSectionUp);
        MoveSectionDownCommand = new RelayCommand<PrintLayoutSectionRowViewModel>(MoveSectionDown);
        PinBottomFromHereCommand = new RelayCommand<PrintLayoutSectionRowViewModel>(
            row => BottomPinnedFromSectionKey = row?.SectionKey);
        ClearBottomPinCommand = new RelayCommand(
            () => BottomPinnedFromSectionKey = null,
            () => BottomPinnedFromSectionKey is not null);
        ResetPageLayoutCommand = new RelayCommand(ResetPageLayout);

        ApplyPageLayout(null);
    }

    public ObservableCollection<PrintAssetResponse> Assets { get; }
    public ObservableCollection<PrintLayoutSectionRowViewModel> SectionLayouts { get; }
    public IReadOnlyList<string> DensityOptions => PrintPageDensity.All;

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand UploadLogoCommand { get; }
    public IAsyncRelayCommand UploadSignatureCommand { get; }
    public IAsyncRelayCommand UploadWatermarkCommand { get; }
    public IAsyncRelayCommand DeleteLogoCommand { get; }
    public IAsyncRelayCommand DeleteSignatureCommand { get; }
    public IAsyncRelayCommand DeleteWatermarkCommand { get; }
    public IRelayCommand<PrintLayoutSectionRowViewModel> MoveSectionUpCommand { get; }
    public IRelayCommand<PrintLayoutSectionRowViewModel> MoveSectionDownCommand { get; }
    public IRelayCommand<PrintLayoutSectionRowViewModel> PinBottomFromHereCommand { get; }
    public IRelayCommand ClearBottomPinCommand { get; }
    public IRelayCommand ResetPageLayoutCommand { get; }

    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private DateTimeOffset? updatedAtUtc;

    [ObservableProperty] private double leftMarginCm;
    [ObservableProperty] private double rightMarginCm;
    [ObservableProperty] private double topMarginCm;
    [ObservableProperty] private double bottomMarginCm;

    [ObservableProperty] private Guid? logoAssetId;
    [ObservableProperty] private string logoFileName = "—";
    [ObservableProperty] private double logoOffsetXCm;
    [ObservableProperty] private double logoOffsetYCm;
    [ObservableProperty] private double logoWidthCm;
    [ObservableProperty] private double logoHeightCm;

    public bool HasLogo => LogoAssetId is not null;
    public bool HasSignature => SignatureAssetId is not null;
    public bool HasWatermark => WatermarkAssetId is not null || PendingWatermarkBytes is not null;

    [ObservableProperty] private Guid? signatureAssetId;
    [ObservableProperty] private string signatureFileName = "—";
    [ObservableProperty] private double signatureOffsetXCm;
    [ObservableProperty] private double signatureOffsetYCm;
    [ObservableProperty] private double signatureWidthCm;
    [ObservableProperty] private double signatureHeightCm;

    [ObservableProperty] private Guid? watermarkAssetId;
    [ObservableProperty] private string watermarkFileName = "—";
    [ObservableProperty] private double watermarkOffsetXCm = PrintLayoutDefaults.WatermarkOffsetXCm;
    [ObservableProperty] private double watermarkOffsetYCm = PrintLayoutDefaults.WatermarkOffsetYCm;
    [ObservableProperty] private double watermarkWidthCm = PrintLayoutDefaults.WatermarkWidthCm;
    [ObservableProperty] private double watermarkHeightCm = PrintLayoutDefaults.WatermarkHeightCm;
    [ObservableProperty] private double watermarkOpacityPercent = PrintLayoutDefaults.WatermarkOpacityPercent;

    [ObservableProperty] private string pageDensity = PrintPageDensity.Standard;
    [ObservableProperty] private double invoiceBorderThicknessPt = PrintLayoutDefaults.InvoiceBorderThicknessPt;
    [ObservableProperty] private string? bottomPinnedFromSectionKey = PrintLayoutSectionKeys.GstBreakup;

    /// <summary>Unsaved bytes from the last local upload; cleared on delete. Consumed
    /// by the live Settings preview so the pane reflects the browse result immediately,
    /// without waiting for the API round-trip.</summary>
    [ObservableProperty] private byte[]? pendingLogoBytes;
    [ObservableProperty] private byte[]? pendingSignatureBytes;
    [ObservableProperty] private byte[]? pendingWatermarkBytes;

    partial void OnIsBusyChanged(bool value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UploadLogoCommand.NotifyCanExecuteChanged();
        UploadSignatureCommand.NotifyCanExecuteChanged();
        UploadWatermarkCommand.NotifyCanExecuteChanged();
        DeleteLogoCommand.NotifyCanExecuteChanged();
        DeleteSignatureCommand.NotifyCanExecuteChanged();
        DeleteWatermarkCommand.NotifyCanExecuteChanged();
    }

    partial void OnLogoAssetIdChanged(Guid? value)
    {
        DeleteLogoCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasLogo));
    }

    partial void OnSignatureAssetIdChanged(Guid? value)
    {
        DeleteSignatureCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSignature));
    }

    partial void OnWatermarkAssetIdChanged(Guid? value)
    {
        DeleteWatermarkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasWatermark));
    }

    partial void OnPendingWatermarkBytesChanged(byte[]? value)
    {
        DeleteWatermarkCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasWatermark));
    }

    partial void OnBottomPinnedFromSectionKeyChanged(string? value)
    {
        UpdatePinnedStates();
        ClearBottomPinCommand.NotifyCanExecuteChanged();
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_settingsApi is null || _printAssetApi is null)
        {
            StatusMessage = "Print-layout API unavailable.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Loading print layout…";
        try
        {
            var layoutTask = _settingsApi.GetPrintLayoutAsync(cancellationToken);
            var assetsTask = _printAssetApi.ListAsync(cancellationToken);
            await Task.WhenAll(layoutTask, assetsTask);

            var layout = (await layoutTask).Layout;
            var assets = (await assetsTask).Assets;

            LeftMarginCm = layout.LeftMarginCm;
            RightMarginCm = layout.RightMarginCm;
            TopMarginCm = layout.TopMarginCm;
            BottomMarginCm = layout.BottomMarginCm;

            ApplyPlacement(layout.Logo, isLogo: true);
            ApplyPlacement(layout.Signature, isLogo: false);
            ApplyWatermark(layout.Watermark);
            ApplyPageLayout(layout.PageLayout);

            Assets.Clear();
            foreach (var asset in assets)
                Assets.Add(asset);

            UpdateAssetFileNames();
            UpdatedAtUtc = (await layoutTask).UpdatedAtUtc;
            StatusMessage = $"Loaded · {assets.Count} asset(s)";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (_settingsApi is null)
        {
            StatusMessage = "Settings API unavailable.";
            return;
        }
        if (PendingWatermarkBytes is not null && WatermarkAssetId is null)
        {
            StatusMessage =
                "Watermark is a local preview only because upload failed. Update the connected Tally Wrapper Server, then browse the file again before saving.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Saving print layout…";
        try
        {
            var layout = new PrintLayoutSettings(
                LeftMarginCm, RightMarginCm, TopMarginCm, BottomMarginCm,
                LogoAssetId is null
                    ? null
                    : new PrintLayoutAssetPlacement(LogoAssetId, LogoOffsetXCm, LogoOffsetYCm, LogoWidthCm, LogoHeightCm),
                SignatureAssetId is null
                    ? null
                    : new PrintLayoutAssetPlacement(SignatureAssetId, SignatureOffsetXCm, SignatureOffsetYCm, SignatureWidthCm, SignatureHeightCm),
                WatermarkAssetId is null
                    ? null
                    : new PrintLayoutWatermarkPlacement(
                        WatermarkAssetId.Value,
                        WatermarkOffsetXCm,
                        WatermarkOffsetYCm,
                        WatermarkWidthCm,
                        WatermarkHeightCm,
                        WatermarkOpacityPercent),
                BuildPageLayout());

            var response = await _settingsApi.UpdatePrintLayoutAsync(new UpdatePrintLayoutRequest(layout), cancellationToken);
            UpdatedAtUtc = response.UpdatedAtUtc;
            StatusMessage = "Saved.";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Save failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UploadAsync(string kind)
    {
        if (_printAssetApi is null)
        {
            StatusMessage = "Print-asset API unavailable.";
            return;
        }

        PrintLayoutImageSelection? selection;
        try
        {
            selection = await _imagePicker.PickAsync(kind);
        }
        catch (Exception ex)
        {
            StatusMessage = $"File read failed: {ex.Message}";
            return;
        }
        if (selection is null) return;

        var bytes = selection.Bytes;

        if (bytes.Length > 2 * 1024 * 1024)
        {
            StatusMessage = "Image exceeds 2 MB limit.";
            return;
        }

        // Stash bytes locally first so the live preview reflects the browse result
        // before the API upload completes.
        if (kind == PrintAssetKinds.Logo) PendingLogoBytes = bytes;
        else if (kind == PrintAssetKinds.Signature) PendingSignatureBytes = bytes;
        else
        {
            PendingWatermarkBytes = bytes;
            WatermarkFileName = Path.GetFileName(selection.FileName);
        }

        var contentType = GuessContentType(selection.FileName);

        IsBusy = true;
        StatusMessage = $"Uploading {kind}…";
        try
        {
            var request = new PrintAssetUploadRequest(
                AssetKind: kind,
                FileName: Path.GetFileName(selection.FileName),
                ContentType: contentType,
                Base64Content: Convert.ToBase64String(bytes));

            var asset = await _printAssetApi.UploadAsync(request);

            if (kind == PrintAssetKinds.Logo)
            {
                LogoAssetId = asset.Id;
                LogoFileName = asset.FileName;
            }
            else if (kind == PrintAssetKinds.Signature)
            {
                SignatureAssetId = asset.Id;
                SignatureFileName = asset.FileName;
            }
            else
            {
                WatermarkAssetId = asset.Id;
                WatermarkFileName = asset.FileName;
                if (WatermarkWidthCm <= 0 || WatermarkHeightCm <= 0)
                {
                    ApplyWatermark(PrintLayoutDefaults.CreateWatermark(asset.Id));
                }
            }

            await RefreshAssetsAsync();
            StatusMessage = $"Uploaded {kind} · {asset.FileName}";
        }
        catch (HttpRequestException ex)
        {
            HandleUploadFailure(kind, ApiResponseReader.FormatError(ex), ex.StatusCode);
        }
        catch (Exception ex)
        {
            HandleUploadFailure(kind, ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearPendingBytesFor(string kind)
    {
        if (kind == PrintAssetKinds.Logo) PendingLogoBytes = null;
        else if (kind == PrintAssetKinds.Signature) PendingSignatureBytes = null;
        else PendingWatermarkBytes = null;
    }

    private void HandleUploadFailure(string kind, string error, HttpStatusCode? statusCode = null)
    {
        if (kind != PrintAssetKinds.Watermark)
        {
            ClearPendingBytesFor(kind);
            StatusMessage = $"Upload failed: {error}";
            return;
        }

        StatusMessage = statusCode == HttpStatusCode.BadRequest
            ? "Watermark upload was rejected by the connected server. Install the matching Tally Wrapper Server update; showing a local preview only."
            : $"Watermark upload failed: {error}. Showing a local preview only; it is not saved.";
    }

    private async Task DeleteByKindAsync(string kind)
    {
        if (_printAssetApi is null) return;

        var id = kind switch
        {
            PrintAssetKinds.Logo => LogoAssetId,
            PrintAssetKinds.Signature => SignatureAssetId,
            _ => WatermarkAssetId,
        };
        if (id is null)
        {
            if (kind == PrintAssetKinds.Watermark && PendingWatermarkBytes is not null)
            {
                PendingWatermarkBytes = null;
                WatermarkFileName = "—";
                StatusMessage = "Cleared local watermark preview.";
            }
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {kind}…";
        try
        {
            await _printAssetApi.DeleteAsync(id.Value);

            if (kind == PrintAssetKinds.Logo)
            {
                LogoAssetId = null;
                LogoFileName = "—";
                PendingLogoBytes = null;
            }
            else if (kind == PrintAssetKinds.Signature)
            {
                SignatureAssetId = null;
                SignatureFileName = "—";
                PendingSignatureBytes = null;
            }
            else
            {
                WatermarkAssetId = null;
                WatermarkFileName = "—";
                PendingWatermarkBytes = null;
            }

            await RefreshAssetsAsync();
            StatusMessage = $"Deleted {kind}.";
        }
        catch (HttpRequestException ex)
        {
            StatusMessage = $"Delete failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAssetsAsync()
    {
        if (_printAssetApi is null) return;
        var assets = await _printAssetApi.ListAsync();
        Assets.Clear();
        foreach (var asset in assets.Assets)
            Assets.Add(asset);
        UpdateAssetFileNames();
    }

    private void ApplyPlacement(PrintLayoutAssetPlacement? placement, bool isLogo)
    {
        if (isLogo)
        {
            LogoAssetId = placement?.AssetId;
            LogoOffsetXCm = placement?.OffsetXCm ?? 0;
            LogoOffsetYCm = placement?.OffsetYCm ?? 0;
            LogoWidthCm = placement?.WidthCm ?? 0;
            LogoHeightCm = placement?.HeightCm ?? 0;
        }
        else
        {
            SignatureAssetId = placement?.AssetId;
            SignatureOffsetXCm = placement?.OffsetXCm ?? 0;
            SignatureOffsetYCm = placement?.OffsetYCm ?? 0;
            SignatureWidthCm = placement?.WidthCm ?? 0;
            SignatureHeightCm = placement?.HeightCm ?? 0;
        }
    }

    private void ApplyWatermark(PrintLayoutWatermarkPlacement? watermark)
    {
        WatermarkAssetId = watermark?.AssetId;
        WatermarkOffsetXCm = watermark?.OffsetXCm ?? PrintLayoutDefaults.WatermarkOffsetXCm;
        WatermarkOffsetYCm = watermark?.OffsetYCm ?? PrintLayoutDefaults.WatermarkOffsetYCm;
        WatermarkWidthCm = watermark?.WidthCm ?? PrintLayoutDefaults.WatermarkWidthCm;
        WatermarkHeightCm = watermark?.HeightCm ?? PrintLayoutDefaults.WatermarkHeightCm;
        WatermarkOpacityPercent = watermark?.OpacityPercent ?? PrintLayoutDefaults.WatermarkOpacityPercent;
    }

    private void ApplyPageLayout(PrintPageLayoutSettings? pageLayout)
    {
        var resolved = pageLayout ?? PrintLayoutDefaults.CreatePageLayout();

        foreach (var row in SectionLayouts)
        {
            row.PropertyChanged -= OnSectionLayoutPropertyChanged;
        }
        SectionLayouts.Clear();

        foreach (var section in resolved.Sections)
        {
            var row = new PrintLayoutSectionRowViewModel(
                section.SectionKey,
                DisplayNameFor(section.SectionKey),
                PrintLayoutSectionKeys.Optional.Contains(section.SectionKey),
                section.IsVisible,
                section.SpacingBeforeMm,
                section.SpacingAfterMm);
            row.PropertyChanged += OnSectionLayoutPropertyChanged;
            SectionLayouts.Add(row);
        }

        PageDensity = PrintPageDensity.All.Contains(resolved.Density, StringComparer.Ordinal)
            ? resolved.Density
            : PrintPageDensity.Standard;
        InvoiceBorderThicknessPt = resolved.InvoiceBorderThicknessPt;
        BottomPinnedFromSectionKey = resolved.BottomPinnedFromSectionKey;
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    private PrintPageLayoutSettings BuildPageLayout() =>
        new(
            PageDensity,
            InvoiceBorderThicknessPt,
            BottomPinnedFromSectionKey,
            SectionLayouts
                .Select(row => new PrintLayoutSectionSettings(
                    row.SectionKey,
                    row.IsVisible,
                    row.SpacingBeforeMm,
                    row.SpacingAfterMm))
                .ToArray());

    public void MoveSection(string sourceSectionKey, string targetSectionKey)
    {
        var sourceIndex = IndexOfSection(sourceSectionKey);
        var targetIndex = IndexOfSection(targetSectionKey);
        if (sourceIndex < 0 || targetIndex < 0 || sourceIndex == targetIndex) return;

        SectionLayouts.Move(sourceIndex, targetIndex);
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    public void MoveSectionToEnd(string sourceSectionKey)
    {
        var sourceIndex = IndexOfSection(sourceSectionKey);
        if (sourceIndex < 0 || sourceIndex == SectionLayouts.Count - 1) return;
        SectionLayouts.Move(sourceIndex, SectionLayouts.Count - 1);
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    private void MoveSectionUp(PrintLayoutSectionRowViewModel? row)
    {
        if (row is null) return;
        var index = IndexOfSection(row.SectionKey);
        if (index <= 0) return;
        SectionLayouts.Move(index, index - 1);
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    private void MoveSectionDown(PrintLayoutSectionRowViewModel? row)
    {
        if (row is null) return;
        var index = IndexOfSection(row.SectionKey);
        if (index < 0 || index >= SectionLayouts.Count - 1) return;
        SectionLayouts.Move(index, index + 1);
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    private int IndexOfSection(string sectionKey)
    {
        for (var index = 0; index < SectionLayouts.Count; index++)
        {
            if (string.Equals(SectionLayouts[index].SectionKey, sectionKey, StringComparison.Ordinal))
            {
                return index;
            }
        }
        return -1;
    }

    private void UpdatePinnedStates()
    {
        var boundary = BottomPinnedFromSectionKey is null
            ? -1
            : IndexOfSection(BottomPinnedFromSectionKey);
        for (var index = 0; index < SectionLayouts.Count; index++)
        {
            SectionLayouts[index].IsBottomPinned = boundary >= 0 && index >= boundary;
        }
    }

    private void ResetPageLayout() => ApplyPageLayout(PrintLayoutDefaults.CreatePageLayout());

    private void OnSectionLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SectionLayouts));
    }

    private static string DisplayNameFor(string sectionKey) => sectionKey switch
    {
        PrintLayoutSectionKeys.CopyLabel => "Copy label",
        PrintLayoutSectionKeys.Logo => "Logo",
        PrintLayoutSectionKeys.InvoiceTitle => "Invoice title",
        PrintLayoutSectionKeys.CompanyAndParty => "Company and bill-to",
        PrintLayoutSectionKeys.Notes => "Notes",
        PrintLayoutSectionKeys.ItemsTable => "Items table",
        PrintLayoutSectionKeys.Totals => "Totals",
        PrintLayoutSectionKeys.GstBreakup => "GST breakup",
        PrintLayoutSectionKeys.BankDetails => "Bank details",
        PrintLayoutSectionKeys.Terms => "Terms and conditions",
        PrintLayoutSectionKeys.Signature => "Signature",
        _ => sectionKey,
    };

    private void UpdateAssetFileNames()
    {
        var logo = Assets.FirstOrDefault(a => a.Id == LogoAssetId);
        LogoFileName = logo?.FileName ?? "—";
        var signature = Assets.FirstOrDefault(a => a.Id == SignatureAssetId);
        SignatureFileName = signature?.FileName ?? "—";
        var watermark = Assets.FirstOrDefault(a => a.Id == WatermarkAssetId);
        WatermarkFileName = watermark?.FileName ?? "—";
    }

    private static string GuessContentType(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            _ => "application/octet-stream"
        };
    }
}

internal sealed record PrintLayoutImageSelection(string FileName, byte[] Bytes);

internal interface IPrintLayoutImagePicker
{
    Task<PrintLayoutImageSelection?> PickAsync(string kind);
}

internal sealed class WpfPrintLayoutImagePicker : IPrintLayoutImagePicker
{
    public async Task<PrintLayoutImageSelection?> PickAsync(string kind)
    {
        var dialog = new OpenFileDialog
        {
            Title = kind switch
            {
                PrintAssetKinds.Logo => "Choose logo image",
                PrintAssetKinds.Signature => "Choose signature image",
                _ => "Choose watermark image",
            },
            Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return null;

        return new PrintLayoutImageSelection(
            dialog.FileName,
            await File.ReadAllBytesAsync(dialog.FileName));
    }
}
