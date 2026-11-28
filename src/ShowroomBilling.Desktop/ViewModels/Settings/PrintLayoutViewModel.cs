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
    private readonly ISettingsApiClient _settingsApi;
    private readonly IPrintAssetApiClient _printAssetApi;
    private readonly IPrintLayoutImagePicker _imagePicker;

    public PrintLayoutViewModel(ISettingsApiClient settingsApi, IPrintAssetApiClient printAssetApi)
        : this(settingsApi, printAssetApi, new WpfPrintLayoutImagePicker())
    {
    }

    internal PrintLayoutViewModel(
        ISettingsApiClient settingsApi,
        IPrintAssetApiClient printAssetApi,
        IPrintLayoutImagePicker imagePicker)
    {
        _settingsApi = settingsApi;
        _printAssetApi = printAssetApi;
        _imagePicker = imagePicker;

        Assets = new ObservableCollection<PrintAssetResponse>();
        SectionLayouts = new ObservableCollection<PrintLayoutSectionRowViewModel>();

        Logo = new(PrintAssetKinds.Logo, "Logo", () => IsIdle, UploadAsync, DeleteAsync);
        Signature = new(PrintAssetKinds.Signature, "Signature", () => IsIdle, UploadAsync, DeleteAsync);
        Watermark = new(PrintAssetKinds.Watermark, "Watermark", () => IsIdle, UploadAsync, DeleteAsync);
        ImageSlots = [Logo, Watermark, Signature];
        Watermark.LoadWatermark(null);
        ApplyPageLayout(null);
    }

    public PrintLayoutAssetViewModel Logo { get; }
    public PrintLayoutAssetViewModel Signature { get; }
    public PrintLayoutAssetViewModel Watermark { get; }
    internal IReadOnlyList<PrintLayoutAssetViewModel> ImageSlots { get; }
    public ObservableCollection<PrintAssetResponse> Assets { get; }
    public ObservableCollection<PrintLayoutSectionRowViewModel> SectionLayouts { get; }
    public IReadOnlyList<string> DensityOptions => PrintPageDensity.All;

    // Nothing loads, saves, uploads or deletes while another of them is in flight.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadCommand), nameof(SaveCommand))]
    private bool isBusy;

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private DateTimeOffset? updatedAtUtc;

    [ObservableProperty] private double leftMarginCm;
    [ObservableProperty] private double rightMarginCm;
    [ObservableProperty] private double topMarginCm;
    [ObservableProperty] private double bottomMarginCm;

    [ObservableProperty] private string pageDensity = PrintPageDensity.Standard;
    [ObservableProperty] private double invoiceBorderThicknessPt = PrintLayoutDefaults.InvoiceBorderThicknessPt;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearBottomPinCommand))]
    private string? bottomPinnedFromSectionKey = PrintLayoutSectionKeys.GstBreakup;

    partial void OnIsBusyChanged(bool value)
    {
        foreach (var slot in ImageSlots)
        {
            slot.UploadCommand.NotifyCanExecuteChanged();
            slot.DeleteCommand.NotifyCanExecuteChanged();
        }
    }

    partial void OnBottomPinnedFromSectionKeyChanged(string? value) => UpdatePinnedStates();

    private bool IsIdle => !IsBusy;

    private bool HasBottomPin => BottomPinnedFromSectionKey is not null;

    [RelayCommand]
    private void PinBottomFromHere(PrintLayoutSectionRowViewModel? row) => BottomPinnedFromSectionKey = row?.SectionKey;

    [RelayCommand(CanExecute = nameof(HasBottomPin))]
    private void ClearBottomPin() => BottomPinnedFromSectionKey = null;

    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
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

            Logo.Load(layout.Logo);
            Signature.Load(layout.Signature);
            Watermark.LoadWatermark(layout.Watermark);
            ApplyPageLayout(layout.PageLayout);

            Assets.Clear();
            foreach (var asset in assets)
                Assets.Add(asset);

            UpdateAssetFileNames();
            UpdatedAtUtc = (await layoutTask).UpdatedAtUtc;
            StatusMessage = $"Loaded · {assets.Count} asset(s)";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        if (Watermark.PendingBytes is not null && Watermark.AssetId is null)
        {
            StatusMessage =
                "Watermark is a local preview only because upload failed. Update the connected Tally Wrapper Server, then browse the file again before saving.";
            return;
        }

        IsBusy = true;
        StatusMessage = "Saving print layout…";
        try
        {
            var layout = BuildLayout(preview: false);
            var response = await _settingsApi.UpdatePrintLayoutAsync(new UpdatePrintLayoutRequest(layout), cancellationToken);
            UpdatedAtUtc = response.UpdatedAtUtc;
            StatusMessage = "Saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task UploadAsync(PrintLayoutAssetViewModel slot)
    {
        var kind = slot.Kind;
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
        slot.PendingBytes = bytes;
        if (slot.IsWatermark) slot.FileName = Path.GetFileName(selection.FileName);

        IsBusy = true;
        StatusMessage = $"Uploading {kind}…";
        try
        {
            var request = new PrintAssetUploadRequest(
                AssetKind: kind,
                FileName: Path.GetFileName(selection.FileName),
                Base64Content: Convert.ToBase64String(bytes));

            var asset = await _printAssetApi.UploadAsync(request);

            slot.AssetId = asset.Id;
            slot.FileName = asset.FileName;
            if (slot.IsWatermark && (slot.WidthCm <= 0 || slot.HeightCm <= 0))
            {
                slot.LoadWatermark(PrintLayoutDefaults.CreateWatermark(asset.Id));
            }

            await RefreshAssetsAsync();
            StatusMessage = $"Uploaded {kind} · {asset.FileName}";
        }
        catch (Exception ex)
        {
            HandleUploadFailure(slot, ApiResponseReader.FormatError(ex), (ex as HttpRequestException)?.StatusCode);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void HandleUploadFailure(PrintLayoutAssetViewModel slot, string error, HttpStatusCode? statusCode = null)
    {
        if (!slot.IsWatermark)
        {
            slot.PendingBytes = null;
            StatusMessage = $"Upload failed: {error}";
            return;
        }

        StatusMessage = statusCode == HttpStatusCode.BadRequest
            ? "Watermark upload was rejected by the connected server. Install the matching Tally Wrapper Server update; showing a local preview only."
            : $"Watermark upload failed: {error}. Showing a local preview only; it is not saved.";
    }

    private async Task DeleteAsync(PrintLayoutAssetViewModel slot)
    {
        var kind = slot.Kind;
        var id = slot.AssetId;
        if (id is null)
        {
            if (slot.IsWatermark && slot.PendingBytes is not null)
            {
                slot.PendingBytes = null;
                slot.FileName = "—";
                StatusMessage = "Cleared local watermark preview.";
            }
            return;
        }

        IsBusy = true;
        StatusMessage = $"Deleting {kind}…";
        try
        {
            await _printAssetApi.DeleteAsync(id.Value);

            slot.AssetId = null;
            slot.FileName = "—";
            slot.PendingBytes = null;

            await RefreshAssetsAsync();
            StatusMessage = $"Deleted {kind}.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Delete failed: {ApiResponseReader.FormatError(ex)}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RefreshAssetsAsync()
    {
        var assets = await _printAssetApi.ListAsync();
        Assets.Clear();
        foreach (var asset in assets.Assets)
            Assets.Add(asset);
        UpdateAssetFileNames();
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

    internal PrintLayoutSettings BuildLayout(bool preview) => new(
        LeftMarginCm, RightMarginCm, TopMarginCm, BottomMarginCm,
        Logo.Placement, Signature.Placement, Watermark.WatermarkPlacement(preview), BuildPageLayout());

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

    [RelayCommand]
    private void MoveSectionUp(PrintLayoutSectionRowViewModel? row)
    {
        if (row is null) return;
        var index = IndexOfSection(row.SectionKey);
        if (index <= 0) return;
        SectionLayouts.Move(index, index - 1);
        UpdatePinnedStates();
        OnPropertyChanged(nameof(SectionLayouts));
    }

    [RelayCommand]
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

    [RelayCommand]
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
        foreach (var slot in ImageSlots)
            slot.FileName = Assets.FirstOrDefault(asset => asset.Id == slot.AssetId)?.FileName ?? "—";
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
