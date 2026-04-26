using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

/// <summary>
/// Live print preview docked inside the Settings screen. Observes
/// <see cref="SettingsDraft"/> + <see cref="PrintLayoutViewModel"/> and re-renders
/// whenever a print-affecting field changes. Debounces 300 ms, drops stale async
/// results via generation counters, and suppresses refreshes during bulk updates
/// (Load / Save / Discard) to avoid N renders per settings reload.
/// </summary>
public partial class SettingsPreviewViewModel : ObservableObject
{
    private const int PreviewRenderDpi = 120;
    private const double MinZoomPercent = 35;
    private const double MaxZoomPercent = 200;
    private const double ZoomStepPercent = 10;
    private const double PreviewPaneChromeWidth = 72;
    private static readonly TimeSpan DefaultDebounceInterval = TimeSpan.FromMilliseconds(300);

    private readonly TimeSpan _debounceInterval;
    private readonly SettingsDraft? _draft;
    private readonly PrintLayoutViewModel? _printLayout;
    private readonly SettingsViewModel? _settings;
    private readonly IPrintDispatcher? _dispatcher;
    private readonly IPrintAssetApiClient? _assets;
    private readonly IPrintPreferencesStore? _preferences;
    private readonly SettingsPreviewAssetCache _assetCache;

    // Bumped on every enqueued refresh; any in-flight task whose captured generation
    // does not match the current generation discards its result on completion.
    private int _renderGeneration;

    private Timer? _debounce;
    private bool _bulkWasActive;
    private bool _suppressZoomPersist;

    public SettingsPreviewViewModel() : this(null, null, null, null, null, null, null) { }

    public SettingsPreviewViewModel(
        SettingsDraft? draft,
        PrintLayoutViewModel? printLayout,
        SettingsViewModel? settings,
        IPrintDispatcher? dispatcher,
        IPrintAssetApiClient? assets)
        : this(draft, printLayout, settings, dispatcher, assets, null, null) { }

    public SettingsPreviewViewModel(
        SettingsDraft? draft,
        PrintLayoutViewModel? printLayout,
        SettingsViewModel? settings,
        IPrintDispatcher? dispatcher,
        IPrintAssetApiClient? assets,
        IPrintPreferencesStore? preferences)
        : this(draft, printLayout, settings, dispatcher, assets, preferences, null) { }

    public SettingsPreviewViewModel(
        SettingsDraft? draft,
        PrintLayoutViewModel? printLayout,
        SettingsViewModel? settings,
        IPrintDispatcher? dispatcher,
        IPrintAssetApiClient? assets,
        IPrintPreferencesStore? preferences,
        TimeSpan? debounceInterval)
    {
        _draft = draft;
        _printLayout = printLayout;
        _settings = settings;
        _dispatcher = dispatcher;
        _assets = assets;
        _preferences = preferences;
        _debounceInterval = debounceInterval ?? DefaultDebounceInterval;
        _assetCache = new SettingsPreviewAssetCache(_assets, _printLayout, EnqueueRefresh);

        PreviewPages = new ObservableCollection<BitmapSource>();
        RefreshCommand = new RelayCommand(RefreshNow);
        FitWidthCommand = new RelayCommand(FitWidth, () => HasPreviewPages);
        ZoomOutCommand = new RelayCommand(() =>
        {
            IsFitToWidth = false;
            PreviewZoomPercent -= ZoomStepPercent;
        }, () => PreviewZoomPercent > MinZoomPercent);
        ResetZoomCommand = new RelayCommand(() =>
        {
            IsFitToWidth = false;
            PreviewZoomPercent = 100;
        }, () => Math.Abs(PreviewZoomPercent - 100) > 0.01);
        ZoomInCommand = new RelayCommand(() =>
        {
            IsFitToWidth = false;
            PreviewZoomPercent += ZoomStepPercent;
        }, () => PreviewZoomPercent < MaxZoomPercent);

        if (_preferences is not null)
        {
            previewZoomPercent = _preferences.PrintPreviewZoomPercent;
            _preferences.PrintPreviewZoomPercentChanged += OnStoredZoomChanged;
        }

        if (_draft is not null) _draft.PropertyChanged += OnSourceChanged;
        if (_printLayout is not null) _printLayout.PropertyChanged += OnLayoutChanged;
        if (_settings is not null) _settings.PropertyChanged += OnSettingsChanged;
    }

    public ObservableCollection<BitmapSource> PreviewPages { get; }
    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand FitWidthCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ResetZoomCommand { get; }
    public IRelayCommand ZoomInCommand { get; }

    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private bool isActive;
    [ObservableProperty] private double paneWidth = 420;
    [ObservableProperty] private double previewZoomPercent = 100;
    [ObservableProperty] private bool isFitToWidth = true;
    [ObservableProperty] private int pageCount;

    public double PreviewScale => PreviewZoomPercent / 100.0;
    public string PreviewZoomDisplay => $"{PreviewZoomPercent:0}%";
    public bool HasPreviewPages => PageCount > 0;
    public bool ShowEmptyState => !IsBusy && PageCount == 0;
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public string PreviewSummary => PageCount switch
    {
        0 => "No pages rendered",
        1 => IsFitToWidth ? "1 page · fit width" : $"1 page · {PreviewZoomDisplay}",
        _ => IsFitToWidth ? $"{PageCount} pages · fit width" : $"{PageCount} pages · {PreviewZoomDisplay}",
    };

    public bool IsBulkUpdating =>
        (_settings?.IsLoading ?? false)
        || (_settings?.IsSaving ?? false)
        || (_printLayout?.IsBusy ?? false);

    partial void OnStatusMessageChanged(string value) => OnPropertyChanged(nameof(HasStatusMessage));

    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    partial void OnPaneWidthChanged(double value)
    {
        if (IsFitToWidth)
        {
            ApplyFitWidth(persist: false);
        }
    }

    partial void OnIsFitToWidthChanged(bool value)
    {
        OnPropertyChanged(nameof(PreviewSummary));
        if (value)
        {
            ApplyFitWidth(persist: false);
        }
    }

    partial void OnPageCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasPreviewPages));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(PreviewSummary));
        FitWidthCommand.NotifyCanExecuteChanged();
    }

    partial void OnPreviewZoomPercentChanged(double value)
    {
        var normalized = NormalizeZoom(value);
        if (Math.Abs(normalized - value) > 0.01)
        {
            PreviewZoomPercent = normalized;
            return;
        }

        OnPropertyChanged(nameof(PreviewScale));
        OnPropertyChanged(nameof(PreviewZoomDisplay));
        OnPropertyChanged(nameof(PreviewSummary));
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();

        if (!_suppressZoomPersist)
        {
            _preferences?.SavePrintPreviewZoomPercent(normalized);
        }
    }

    /// <summary>Activate (or deactivate) the preview pane. While inactive the VM
    /// does not render; activation triggers an immediate refresh.</summary>
    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;
        if (active)
        {
            _assetCache.IncrementGeneration();
            _ = _assetCache.EnsureServerAssetsAsync();
            EnqueueRefresh();
        }
    }

    private void OnSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!SettingsPreviewDocumentBuilder.IsPrintAffectingProperty(e.PropertyName)) return;
        EnqueueRefresh();
    }

    private void OnLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PrintLayoutViewModel.PendingLogoBytes):
            case nameof(PrintLayoutViewModel.PendingSignatureBytes):
                // Unsaved local bytes; generation bump so any in-flight server
                // download drops its result rather than overwriting these.
                _assetCache.IncrementGeneration();
                EnqueueRefresh();
                break;

            case nameof(PrintLayoutViewModel.LogoAssetId):
            case nameof(PrintLayoutViewModel.SignatureAssetId):
            case nameof(PrintLayoutViewModel.UpdatedAtUtc):
                // Placement changed or layout saved: refetch server bytes once.
                _assetCache.IncrementGeneration();
                _ = _assetCache.EnsureServerAssetsAsync();
                EnqueueRefresh();
                break;

            case nameof(PrintLayoutViewModel.IsBusy):
                HandleBulkTransition();
                break;

            default:
                // Margins, offsets, sizes — pure numeric fields feeding the layout.
                EnqueueRefresh();
                break;
        }
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.IsLoading) or nameof(SettingsViewModel.IsSaving))
        {
            HandleBulkTransition();
        }
    }

    private void HandleBulkTransition()
    {
        var nowBulk = IsBulkUpdating;
        OnPropertyChanged(nameof(IsBulkUpdating));

        // Fire exactly one refresh on the trailing edge (true → false) of bulk.
        if (_bulkWasActive && !nowBulk)
        {
            EnqueueRefresh();
        }
        _bulkWasActive = nowBulk;
    }

    /// <summary>Schedule a debounced refresh. Safe to call on any thread.</summary>
    public void EnqueueRefresh()
    {
        if (!IsActive) return;
        if (IsBulkUpdating) return;
        if (_draft is null || _printLayout is null || _dispatcher is null) return;

        _debounce ??= new Timer(OnDebounceFired, null, Timeout.Infinite, Timeout.Infinite);
        _debounce.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void RefreshNow()
    {
        if (!IsActive || IsBulkUpdating) return;
        _debounce?.Change(Timeout.Infinite, Timeout.Infinite);
        _ = RefreshAsync();
    }

    private void FitWidth()
    {
        IsFitToWidth = true;
        ApplyFitWidth(persist: false);
    }

    private void ApplyFitWidth(bool persist)
    {
        var firstPage = PreviewPages.FirstOrDefault();
        if (firstPage is null) return;

        var pageWidth = firstPage.Width > 0 ? firstPage.Width : firstPage.PixelWidth;
        if (pageWidth <= 0) return;

        var availableWidth = Math.Max(120, PaneWidth - PreviewPaneChromeWidth);
        var zoom = NormalizeZoom(Math.Floor(availableWidth / pageWidth * 100));

        if (Math.Abs(zoom - PreviewZoomPercent) < 0.01) return;

        var priorSuppress = _suppressZoomPersist;
        _suppressZoomPersist = !persist || priorSuppress;
        try
        {
            PreviewZoomPercent = zoom;
        }
        finally
        {
            _suppressZoomPersist = priorSuppress;
        }
    }

    private void OnDebounceFired(object? state) => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_draft is null || _printLayout is null || _dispatcher is null) return;

        // Guard against a timer that was armed before bulk started but is only firing
        // now. The trailing-edge handler will re-enqueue once bulk ends.
        if (IsBulkUpdating) return;

        var generation = Interlocked.Increment(ref _renderGeneration);

        // Build the snapshot on the caller's thread — SettingsDraft and
        // PrintLayoutViewModel are ObservableObjects; reading from a pool thread is
        // fine for the plain value fields, but we Invoke anyway to avoid subtle
        // races with UI mutations.
        PrintDocumentOptions options;
        try
        {
            options = await SettingsPreviewUiThread.InvokeAsync(BuildOptions);
        }
        catch (Exception ex)
        {
            if (generation != Volatile.Read(ref _renderGeneration)) return;
            await SettingsPreviewUiThread.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _renderGeneration)) return;
                StatusMessage = $"Preview snapshot failed: {ex.Message}";
            });
            return;
        }

        await SettingsPreviewUiThread.InvokeAsync(() => { IsBusy = true; StatusMessage = "Rendering preview…"; });
        try
        {
            var pages = await Task.Run(() => _dispatcher.GeneratePageImages(options, dpi: PreviewRenderDpi));

            if (generation != Volatile.Read(ref _renderGeneration)) return; // stale

            await SettingsPreviewUiThread.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _renderGeneration)) return;
                PreviewPages.Clear();
                foreach (var bytes in pages)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.EndInit();
                    bmp.Freeze();
                    PreviewPages.Add(bmp);
                }
                PageCount = PreviewPages.Count;
                if (IsFitToWidth)
                {
                    ApplyFitWidth(persist: false);
                }
                StatusMessage = string.Empty;
            });
        }
        catch (Exception ex)
        {
            // Only surface the failure if this is still the latest render; a stale
            // catch writing "Preview failed" over a newer successful render would be
            // a worse UX than staying quiet.
            if (generation != Volatile.Read(ref _renderGeneration)) return;
            await SettingsPreviewUiThread.InvokeAsync(() =>
            {
                if (generation != Volatile.Read(ref _renderGeneration)) return;
                StatusMessage = $"Preview failed: {ex.Message}";
            });
        }
        finally
        {
            // Same guard on IsBusy — clearing it while a newer render is still
            // running would misreport readiness.
            if (generation == Volatile.Read(ref _renderGeneration))
            {
                await SettingsPreviewUiThread.InvokeAsync(() =>
                {
                    if (generation == Volatile.Read(ref _renderGeneration))
                        IsBusy = false;
                });
            }
        }
    }

    private PrintDocumentOptions BuildOptions()
    {
        return SettingsPreviewDocumentBuilder.BuildOptions(
            _draft!,
            _printLayout!,
            _assetCache.ServerLogoBytes,
            _assetCache.ServerSignatureBytes);
    }

    private void OnStoredZoomChanged(object? sender, double value)
    {
        if (IsFitToWidth && HasPreviewPages)
        {
            return;
        }

        _suppressZoomPersist = true;
        try
        {
            PreviewZoomPercent = NormalizeZoom(value);
        }
        finally
        {
            _suppressZoomPersist = false;
        }
    }

    private static double NormalizeZoom(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0) return 100;
        return Math.Clamp(Math.Round(value), MinZoomPercent, MaxZoomPercent);
    }
}
