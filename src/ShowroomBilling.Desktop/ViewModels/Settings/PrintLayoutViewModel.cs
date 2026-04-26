using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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

    public PrintLayoutViewModel() : this(null, null) { }

    public PrintLayoutViewModel(ISettingsApiClient? settingsApi, IPrintAssetApiClient? printAssetApi)
    {
        _settingsApi = settingsApi;
        _printAssetApi = printAssetApi;

        Assets = new ObservableCollection<PrintAssetResponse>();

        LoadCommand = new AsyncRelayCommand(LoadAsync, () => !IsBusy);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        UploadLogoCommand = new AsyncRelayCommand(() => UploadAsync(PrintAssetKinds.Logo), () => !IsBusy);
        UploadSignatureCommand = new AsyncRelayCommand(() => UploadAsync(PrintAssetKinds.Signature), () => !IsBusy);
        DeleteLogoCommand = new AsyncRelayCommand(() => DeleteByKindAsync(PrintAssetKinds.Logo),
            () => !IsBusy && LogoAssetId is not null);
        DeleteSignatureCommand = new AsyncRelayCommand(() => DeleteByKindAsync(PrintAssetKinds.Signature),
            () => !IsBusy && SignatureAssetId is not null);
    }

    public ObservableCollection<PrintAssetResponse> Assets { get; }

    public IAsyncRelayCommand LoadCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand UploadLogoCommand { get; }
    public IAsyncRelayCommand UploadSignatureCommand { get; }
    public IAsyncRelayCommand DeleteLogoCommand { get; }
    public IAsyncRelayCommand DeleteSignatureCommand { get; }

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

    [ObservableProperty] private Guid? signatureAssetId;
    [ObservableProperty] private string signatureFileName = "—";
    [ObservableProperty] private double signatureOffsetXCm;
    [ObservableProperty] private double signatureOffsetYCm;
    [ObservableProperty] private double signatureWidthCm;
    [ObservableProperty] private double signatureHeightCm;

    /// <summary>Unsaved bytes from the last local upload; cleared on delete. Consumed
    /// by the live Settings preview so the pane reflects the browse result immediately,
    /// without waiting for the API round-trip.</summary>
    [ObservableProperty] private byte[]? pendingLogoBytes;
    [ObservableProperty] private byte[]? pendingSignatureBytes;

    partial void OnIsBusyChanged(bool value)
    {
        LoadCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
        UploadLogoCommand.NotifyCanExecuteChanged();
        UploadSignatureCommand.NotifyCanExecuteChanged();
        DeleteLogoCommand.NotifyCanExecuteChanged();
        DeleteSignatureCommand.NotifyCanExecuteChanged();
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
                    : new PrintLayoutAssetPlacement(SignatureAssetId, SignatureOffsetXCm, SignatureOffsetYCm, SignatureWidthCm, SignatureHeightCm));

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

        var dialog = new OpenFileDialog
        {
            Title = kind == PrintAssetKinds.Logo ? "Choose logo image" : "Choose signature image",
            Filter = "Image files (*.png;*.jpg;*.jpeg)|*.png;*.jpg;*.jpeg|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = $"File read failed: {ex.Message}";
            return;
        }

        if (bytes.Length > 2 * 1024 * 1024)
        {
            StatusMessage = "Image exceeds 2 MB limit.";
            return;
        }

        // Stash bytes locally first so the live preview reflects the browse result
        // before the API upload completes.
        if (kind == PrintAssetKinds.Logo) PendingLogoBytes = bytes;
        else PendingSignatureBytes = bytes;

        var contentType = GuessContentType(dialog.FileName);

        IsBusy = true;
        StatusMessage = $"Uploading {kind}…";
        try
        {
            var request = new PrintAssetUploadRequest(
                AssetKind: kind,
                FileName: Path.GetFileName(dialog.FileName),
                ContentType: contentType,
                Base64Content: Convert.ToBase64String(bytes));

            var asset = await _printAssetApi.UploadAsync(request);

            if (kind == PrintAssetKinds.Logo)
            {
                LogoAssetId = asset.Id;
                LogoFileName = asset.FileName;
            }
            else
            {
                SignatureAssetId = asset.Id;
                SignatureFileName = asset.FileName;
            }

            await RefreshAssetsAsync();
            StatusMessage = $"Uploaded {kind} · {asset.FileName}";
        }
        catch (HttpRequestException ex)
        {
            ClearPendingBytesFor(kind);
            StatusMessage = $"Upload failed: {ApiResponseReader.FormatError(ex)}";
        }
        catch (Exception ex)
        {
            ClearPendingBytesFor(kind);
            StatusMessage = $"Upload failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ClearPendingBytesFor(string kind)
    {
        if (kind == PrintAssetKinds.Logo) PendingLogoBytes = null;
        else PendingSignatureBytes = null;
    }

    private async Task DeleteByKindAsync(string kind)
    {
        if (_printAssetApi is null) return;

        var id = kind == PrintAssetKinds.Logo ? LogoAssetId : SignatureAssetId;
        if (id is null) return;

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
            else
            {
                SignatureAssetId = null;
                SignatureFileName = "—";
                PendingSignatureBytes = null;
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

    private void UpdateAssetFileNames()
    {
        var logo = Assets.FirstOrDefault(a => a.Id == LogoAssetId);
        LogoFileName = logo?.FileName ?? "—";
        var signature = Assets.FirstOrDefault(a => a.Id == SignatureAssetId);
        SignatureFileName = signature?.FileName ?? "—";
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
