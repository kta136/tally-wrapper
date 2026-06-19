using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Printing;

public readonly record struct CopyDefaults(bool Original, bool Duplicate, bool Triplicate);

public sealed record PrintSettingOption<T>(T Value, string DisplayName);

public partial class PrintPreviewViewModel : ObservableObject
{
    private const double MinZoomPercent = 50;
    private const double MaxZoomPercent = 200;
    private const double ZoomStepPercent = 10;
    private const string PreparingPrintPagesMessage = "Preparing printer-ready pages…";

    private readonly IPrintDispatcher? _dispatcher;
    private readonly IPrintPreferencesStore? _preferences;

    private IReadOnlyList<BillPrintContent> _contents = Array.Empty<BillPrintContent>();
    private PrintLayoutOptions _layout = PrintLayoutOptions.Default;
    private CancellationTokenSource? _previewRenderCts;
    private CancellationTokenSource? _printPrepareCts;
    private int _previewRenderVersion;
    private int _printPrepareVersion;
    private int _printerLoadVersion;
    private int _printerCapabilityLoadVersion;
    private Task<IReadOnlyList<byte[]>>? _printPrepareTask;
    private IReadOnlyList<byte[]>? _preparedPrintPages;
    private bool _suppressZoomPersist;
    private bool _suppressPrintSettingsPersist;
    private PrintJobSettings _preferredPrintJobSettings = PrintJobSettings.Default;
    private Action? _onPrintSucceeded;

    public PrintPreviewViewModel() : this(null, null) { }

    public PrintPreviewViewModel(IPrintDispatcher? dispatcher, IPrintPreferencesStore? preferences)
    {
        _dispatcher = dispatcher;
        _preferences = preferences;

        Printers = new ObservableCollection<string>();
        PreviewPages = new ObservableCollection<BitmapSource>();
        DuplexOptions = new ObservableCollection<PrintSettingOption<PrintDuplexMode>>();
        ColorOptions = new ObservableCollection<PrintSettingOption<PrintColorMode>>();
        CollationOptions = new ObservableCollection<PrintSettingOption<PrintCollationMode>>();
        ApplyPendingPrintSettingOptions();

        if (preferences is not null)
        {
            _preferredPrintJobSettings = preferences.PrintJobSettings;
            directPrintAfterSave = preferences.DirectPrintAfterSave;
            previewZoomPercent = preferences.PrintPreviewZoomPercent;
            selectedDuplexMode = _preferredPrintJobSettings.Duplex;
            selectedColorMode = _preferredPrintJobSettings.Color;
            selectedCollationMode = _preferredPrintJobSettings.Collation;
            preferences.PrintPreviewZoomPercentChanged += OnStoredZoomChanged;
        }

        PrintCommand = new AsyncRelayCommand(PrintAsync, CanPrint);
        SavePdfCommand = new AsyncRelayCommand(SavePdfAsync, () => !IsBusy && IsSingleDocument && _contents.Count == 1);
        ZoomOutCommand = new RelayCommand(() => PreviewZoomPercent -= ZoomStepPercent, () => PreviewZoomPercent > MinZoomPercent);
        ResetZoomCommand = new RelayCommand(() => PreviewZoomPercent = 100, () => Math.Abs(PreviewZoomPercent - 100) > 0.01);
        ZoomInCommand = new RelayCommand(() => PreviewZoomPercent += ZoomStepPercent, () => PreviewZoomPercent < MaxZoomPercent);
    }

    public ObservableCollection<string> Printers { get; }
    public ObservableCollection<BitmapSource> PreviewPages { get; }
    public ObservableCollection<PrintSettingOption<PrintDuplexMode>> DuplexOptions { get; }
    public ObservableCollection<PrintSettingOption<PrintColorMode>> ColorOptions { get; }
    public ObservableCollection<PrintSettingOption<PrintCollationMode>> CollationOptions { get; }

    public IAsyncRelayCommand PrintCommand { get; }
    public IAsyncRelayCommand SavePdfCommand { get; }
    public IRelayCommand ZoomOutCommand { get; }
    public IRelayCommand ResetZoomCommand { get; }
    public IRelayCommand ZoomInCommand { get; }

    [ObservableProperty] private bool copyOriginal = true;
    [ObservableProperty] private bool copyDuplicate;
    [ObservableProperty] private bool copyTriplicate;
    [ObservableProperty] private bool directPrintAfterSave;
    private bool _suppressCopyHandler;
    [ObservableProperty] private string? selectedPrinter;
    [ObservableProperty] private PrintDuplexMode selectedDuplexMode;
    [ObservableProperty] private PrintColorMode selectedColorMode;
    [ObservableProperty] private PrintCollationMode selectedCollationMode;
    [ObservableProperty] private string statusMessage = string.Empty;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string invoiceNumber = string.Empty;
    [ObservableProperty] private int documentCount = 1;
    [ObservableProperty] private double dialogWidth = 980;
    [ObservableProperty] private double dialogHeight = 680;
    [ObservableProperty] private double previewZoomPercent = 100;

    public bool IsSingleDocument => DocumentCount == 1;
    public bool HasSelectedPrinter => !string.IsNullOrWhiteSpace(SelectedPrinter);
    public double PreviewScale => PreviewZoomPercent / 100.0;
    public string PreviewZoomDisplay => $"{PreviewZoomPercent:0}%";
    public PrintJobSettings CurrentPrintJobSettings => new(SelectedDuplexMode, SelectedColorMode, SelectedCollationMode);

    partial void OnIsBusyChanged(bool value)
    {
        PrintCommand.NotifyCanExecuteChanged();
        SavePdfCommand.NotifyCanExecuteChanged();
    }

    partial void OnDocumentCountChanged(int value)
    {
        OnPropertyChanged(nameof(IsSingleDocument));
        SavePdfCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPrinterChanged(string? value)
    {
        PrintCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedPrinter));

        var version = Interlocked.Increment(ref _printerCapabilityLoadVersion);
        if (_dispatcher is null || string.IsNullOrWhiteSpace(value))
        {
            ApplyPrinterCapabilities(PrintJobCapabilities.Unknown);
            return;
        }

        _ = LoadPrinterCapabilitiesAsync(value, version);
    }

    partial void OnDirectPrintAfterSaveChanged(bool value) => _preferences?.SaveDirectPrintAfterSave(value);
    partial void OnSelectedDuplexModeChanged(PrintDuplexMode value) => SavePrintJobSettings();
    partial void OnSelectedColorModeChanged(PrintColorMode value) => SavePrintJobSettings();
    partial void OnSelectedCollationModeChanged(PrintCollationMode value) => SavePrintJobSettings();
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
        ZoomOutCommand.NotifyCanExecuteChanged();
        ResetZoomCommand.NotifyCanExecuteChanged();
        ZoomInCommand.NotifyCanExecuteChanged();

        if (!_suppressZoomPersist)
        {
            _preferences?.SavePrintPreviewZoomPercent(normalized);
        }
    }
    partial void OnCopyOriginalChanged(bool value) => OnCopiesChanged();
    partial void OnCopyDuplicateChanged(bool value) => OnCopiesChanged();
    partial void OnCopyTriplicateChanged(bool value) => OnCopiesChanged();

    private void OnCopiesChanged()
    {
        if (_suppressCopyHandler) return;

        // Copy mode 0-all: auto-restore Original
        if (!CopyOriginal && !CopyDuplicate && !CopyTriplicate)
        {
            CopyOriginal = true;
            return;
        }
        PrintCommand.NotifyCanExecuteChanged();
        SchedulePreviewRender();
    }

    public void Initialize(BillPrintContent content, PrintLayoutOptions? layout = null, CopyDefaults? copyDefaults = null, Action? onPrintSucceeded = null)
        => Initialize([content], layout, copyDefaults, onPrintSucceeded);

    public void Initialize(IReadOnlyList<BillPrintContent> contents, PrintLayoutOptions? layout = null, CopyDefaults? copyDefaults = null, Action? onPrintSucceeded = null)
    {
        _onPrintSucceeded = onPrintSucceeded;
        _contents = contents.Where(x => x is not null).ToArray();
        _layout = layout ?? PrintLayoutOptions.Default;
        DocumentCount = _contents.Count;
        InvoiceNumber = DocumentCount <= 1
            ? _contents.FirstOrDefault()?.InvoiceNumber ?? string.Empty
            : $"{DocumentCount} bills";

        // Apply copy defaults inside the suppress gate so the per-property handler
        // does not kick off a render against the previous _contents. The single
        // RenderPreviewAsync below is the only render for this initialize call.
        if (copyDefaults is { } defaults)
        {
            _suppressCopyHandler = true;
            try
            {
                CopyOriginal = defaults.Original;
                CopyDuplicate = defaults.Duplicate;
                CopyTriplicate = defaults.Triplicate;
            }
            finally
            {
                _suppressCopyHandler = false;
            }
            if (!CopyOriginal && !CopyDuplicate && !CopyTriplicate)
            {
                // All-off is invalid; restore Original without triggering a second render.
                _suppressCopyHandler = true;
                try { CopyOriginal = true; }
                finally { _suppressCopyHandler = false; }
            }
        }

        Printers.Clear();
        if (_dispatcher is not null)
        {
            var version = Interlocked.Increment(ref _printerLoadVersion);
            SelectedPrinter = _preferences?.LastPrinterName;
            _ = LoadPrintersAsync(version);
        }

        SchedulePreviewRender();
    }

    private async Task LoadPrintersAsync(int version)
    {
        if (_dispatcher is null)
        {
            return;
        }

        try
        {
            var (printers, defaultPrinter) = await Task.Run(() =>
                (_dispatcher.AvailablePrinters(), _dispatcher.DefaultPrinter()));

            if (version != _printerLoadVersion)
            {
                return;
            }

            Printers.Clear();
            foreach (var name in printers)
            {
                Printers.Add(name);
            }

            SelectedPrinter = _preferences?.LastPrinterName
                              ?? defaultPrinter
                              ?? Printers.FirstOrDefault();
        }
        catch
        {
            if (version == _printerLoadVersion)
            {
                Printers.Clear();
                SelectedPrinter = null;
            }
        }
    }

    private IReadOnlyList<PrintDocumentOptions> BuildOptions()
    {
        if (_contents.Count == 0)
        {
            return Array.Empty<PrintDocumentOptions>();
        }

        var copies = new List<CopyLabel>();
        if (CopyOriginal) copies.Add(CopyLabel.Original);
        if (CopyDuplicate) copies.Add(CopyLabel.Duplicate);
        if (CopyTriplicate) copies.Add(CopyLabel.Triplicate);
        return _contents.Select(content => PrintDocumentOptions.ForInvoice(content, copies, _layout)).ToArray();
    }

    private void SchedulePreviewRender()
    {
        if (_dispatcher is null)
        {
            return;
        }

        CancelPrintPreparation();
        var prior = _previewRenderCts;
        var cts = new CancellationTokenSource();
        _previewRenderCts = cts;
        var version = Interlocked.Increment(ref _previewRenderVersion);
        prior?.Cancel();
        prior?.Dispose();

        _ = RenderPreviewDebouncedAsync(version, cts.Token);
    }

    private async Task RenderPreviewDebouncedAsync(int version, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(150, cancellationToken);
            await RenderPreviewAsync(version, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RenderPreviewAsync(int version, CancellationToken cancellationToken)
    {
        if (_dispatcher is null) return;
        var options = BuildOptions();
        if (options.Count == 0) return;

        if (version == _previewRenderVersion)
        {
            IsBusy = true;
            StatusMessage = "Rendering preview…";
        }
        try
        {
            var pages = await Task.Run(() =>
            {
                var rendered = new List<byte[]>();
                foreach (var option in options)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rendered.AddRange(_dispatcher.GeneratePageImages(option, dpi: 110));
                }

                return rendered;
            }, cancellationToken);

            if (version != _previewRenderVersion || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            PreviewPages.Clear();
            foreach (var bytes in pages)
            {
                var bmp = new BitmapImage();
                using var stream = new MemoryStream(bytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                PreviewPages.Add(bmp);
            }
            StatusMessage = string.Empty;
            SchedulePrintPagePreparation(options);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version == _previewRenderVersion)
            {
                StatusMessage = $"Preview failed: {ex.Message}";
            }
        }
        finally
        {
            if (version == _previewRenderVersion)
            {
                IsBusy = false;
            }
        }
    }

    private async Task LoadPrinterCapabilitiesAsync(string printerName, int version)
    {
        if (_dispatcher is null)
        {
            return;
        }

        try
        {
            var capabilities = await Task.Run(() => _dispatcher.GetPrinterCapabilities(printerName));
            if (version != _printerCapabilityLoadVersion
                || !string.Equals(printerName, SelectedPrinter, StringComparison.Ordinal))
            {
                return;
            }

            ApplyPrinterCapabilities(capabilities);
        }
        catch
        {
            if (version == _printerCapabilityLoadVersion
                && string.Equals(printerName, SelectedPrinter, StringComparison.Ordinal))
            {
                ApplyPrinterCapabilities(PrintJobCapabilities.Unknown);
            }
        }
    }

    private void ApplyPrinterCapabilities(PrintJobCapabilities capabilities)
    {
        var duplexOptions = BuildDuplexOptions(capabilities.DuplexModes).ToArray();
        var colorOptions = BuildColorOptions(capabilities.ColorModes).ToArray();
        var collationOptions = BuildCollationOptions(capabilities.CollationModes).ToArray();
        var desired = capabilities.IsKnown ? _preferredPrintJobSettings : PrintJobSettings.Default;
        var resolved = ResolveSupportedSettings(desired, duplexOptions, colorOptions, collationOptions);

        ApplyPrintSettingOptions(duplexOptions, colorOptions, collationOptions, resolved);

        if (capabilities.IsKnown && resolved != _preferredPrintJobSettings)
        {
            _preferredPrintJobSettings = resolved;
            _preferences?.SavePrintJobSettings(resolved);
        }
    }

    private void SavePrintJobSettings()
    {
        if (_suppressPrintSettingsPersist)
        {
            return;
        }

        _preferredPrintJobSettings = CurrentPrintJobSettings;
        _preferences?.SavePrintJobSettings(_preferredPrintJobSettings);
    }

    private void ApplyPendingPrintSettingOptions()
    {
        ApplyPrintSettingOptions(
            BuildDuplexOptions(
            [
                PrintDuplexMode.PrinterDefault,
                PrintDuplexMode.OneSided,
                PrintDuplexMode.TwoSidedLongEdge,
                PrintDuplexMode.TwoSidedShortEdge,
            ]).ToArray(),
            BuildColorOptions(
            [
                PrintColorMode.PrinterDefault,
                PrintColorMode.Color,
                PrintColorMode.Monochrome,
            ]).ToArray(),
            BuildCollationOptions(
            [
                PrintCollationMode.PrinterDefault,
                PrintCollationMode.Collated,
                PrintCollationMode.Uncollated,
            ]).ToArray(),
            _preferredPrintJobSettings);
    }

    private void ApplyPrintSettingOptions(
        IReadOnlyList<PrintSettingOption<PrintDuplexMode>> duplexOptions,
        IReadOnlyList<PrintSettingOption<PrintColorMode>> colorOptions,
        IReadOnlyList<PrintSettingOption<PrintCollationMode>> collationOptions,
        PrintJobSettings selectedSettings)
    {
        _suppressPrintSettingsPersist = true;
        try
        {
            ReplaceOptions(DuplexOptions, duplexOptions);
            ReplaceOptions(ColorOptions, colorOptions);
            ReplaceOptions(CollationOptions, collationOptions);

            SelectedDuplexMode = selectedSettings.Duplex;
            SelectedColorMode = selectedSettings.Color;
            SelectedCollationMode = selectedSettings.Collation;
        }
        finally
        {
            _suppressPrintSettingsPersist = false;
        }
    }

    private static PrintJobSettings ResolveSupportedSettings(
        PrintJobSettings desired,
        IReadOnlyList<PrintSettingOption<PrintDuplexMode>> duplexOptions,
        IReadOnlyList<PrintSettingOption<PrintColorMode>> colorOptions,
        IReadOnlyList<PrintSettingOption<PrintCollationMode>> collationOptions)
    {
        return new PrintJobSettings(
            duplexOptions.Any(x => x.Value == desired.Duplex)
                ? desired.Duplex
                : PrintDuplexMode.PrinterDefault,
            colorOptions.Any(x => x.Value == desired.Color)
                ? desired.Color
                : PrintColorMode.PrinterDefault,
            collationOptions.Any(x => x.Value == desired.Collation)
                ? desired.Collation
                : PrintCollationMode.PrinterDefault);
    }

    private static void ReplaceOptions<T>(
        ObservableCollection<PrintSettingOption<T>> target,
        IEnumerable<PrintSettingOption<T>> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static IEnumerable<PrintSettingOption<PrintDuplexMode>> BuildDuplexOptions(
        IReadOnlyList<PrintDuplexMode> modes)
    {
        foreach (var mode in modes.Distinct())
        {
            yield return new PrintSettingOption<PrintDuplexMode>(mode, mode switch
            {
                PrintDuplexMode.OneSided => "One-sided",
                PrintDuplexMode.TwoSidedLongEdge => "Both sides - long edge",
                PrintDuplexMode.TwoSidedShortEdge => "Both sides - short edge",
                _ => "Printer default",
            });
        }
    }

    private static IEnumerable<PrintSettingOption<PrintColorMode>> BuildColorOptions(
        IReadOnlyList<PrintColorMode> modes)
    {
        foreach (var mode in modes.Distinct())
        {
            yield return new PrintSettingOption<PrintColorMode>(mode, mode switch
            {
                PrintColorMode.Color => "Color",
                PrintColorMode.Monochrome => "Black & white",
                _ => "Printer default",
            });
        }
    }

    private static IEnumerable<PrintSettingOption<PrintCollationMode>> BuildCollationOptions(
        IReadOnlyList<PrintCollationMode> modes)
    {
        foreach (var mode in modes.Distinct())
        {
            yield return new PrintSettingOption<PrintCollationMode>(mode, mode switch
            {
                PrintCollationMode.Collated => "Collated",
                PrintCollationMode.Uncollated => "Uncollated",
                _ => "Printer default",
            });
        }
    }

    private void CancelPrintPreparation()
    {
        _preparedPrintPages = null;
        _printPrepareTask = null;
        Interlocked.Increment(ref _printPrepareVersion);

        var prior = _printPrepareCts;
        _printPrepareCts = null;
        prior?.Cancel();
        prior?.Dispose();
    }

    private void SchedulePrintPagePreparation(IReadOnlyList<PrintDocumentOptions> options)
    {
        if (_dispatcher is null || options.Count == 0)
        {
            return;
        }

        var prior = _printPrepareCts;
        var cts = new CancellationTokenSource();
        _printPrepareCts = cts;
        _preparedPrintPages = null;
        var version = Interlocked.Increment(ref _printPrepareVersion);
        prior?.Cancel();
        prior?.Dispose();

        _printPrepareTask = PreparePrintPagesAsync(version, options, cts.Token);
    }

    private async Task<IReadOnlyList<byte[]>> PreparePrintPagesAsync(
        int version,
        IReadOnlyList<PrintDocumentOptions> options,
        CancellationToken cancellationToken)
    {
        if (_dispatcher is null)
        {
            return Array.Empty<byte[]>();
        }

        try
        {
            await Task.Delay(50, cancellationToken);
            if (version == _printPrepareVersion && !IsBusy)
            {
                StatusMessage = PreparingPrintPagesMessage;
            }

            var pages = await Task.Run(() => _dispatcher.GeneratePrintPageImages(options), cancellationToken);
            if (version == _printPrepareVersion && !cancellationToken.IsCancellationRequested)
            {
                _preparedPrintPages = pages;
                if (!IsBusy && StatusMessage == PreparingPrintPagesMessage)
                {
                    StatusMessage = pages.Count > 0 ? "Ready to print." : "No pages were generated for this bill.";
                }
            }

            return pages;
        }
        catch (OperationCanceledException)
        {
            return Array.Empty<byte[]>();
        }
        catch
        {
            if (version == _printPrepareVersion && !IsBusy && StatusMessage == PreparingPrintPagesMessage)
            {
                StatusMessage = string.Empty;
            }

            return Array.Empty<byte[]>();
        }
    }

    private async Task<IReadOnlyList<byte[]>> GetPrintPagesAsync(
        IReadOnlyList<PrintDocumentOptions> options,
        CancellationToken cancellationToken)
    {
        if (_dispatcher is null)
        {
            return Array.Empty<byte[]>();
        }

        if (_preparedPrintPages is { Count: > 0 } pages)
        {
            return pages;
        }

        var task = _printPrepareTask;
        if (task is not null)
        {
            StatusMessage = "Finishing print preparation…";
            var prepared = await task.WaitAsync(cancellationToken);
            if (ReferenceEquals(task, _printPrepareTask) && prepared.Count > 0)
            {
                return prepared;
            }
        }

        StatusMessage = "Rendering printer-ready pages…";
        return await Task.Run(() => _dispatcher.GeneratePrintPageImages(options), cancellationToken);
    }

    private bool CanPrint() => !IsBusy && _contents.Count > 0 && !string.IsNullOrWhiteSpace(SelectedPrinter);

    private async Task PrintAsync(CancellationToken cancellationToken)
    {
        if (_dispatcher is null || _contents.Count == 0 || string.IsNullOrWhiteSpace(SelectedPrinter)) return;
        var options = BuildOptions();
        if (options.Count == 0) return;

        IsBusy = true;
        StatusMessage = $"Sending to {SelectedPrinter}…";
        try
        {
            var pages = await GetPrintPagesAsync(options, cancellationToken);
            var settings = CurrentPrintJobSettings;
            var ok = await Task.Run(() => _dispatcher.PrintRenderedPages(pages, SelectedPrinter!, settings), cancellationToken);

            StatusMessage = ok ? "Sent to printer." : "No pages were generated for this bill.";
            if (ok)
            {
                _preferences?.SaveLastPrinter(SelectedPrinter!);
                _onPrintSucceeded?.Invoke();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Print failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SavePdfAsync(CancellationToken cancellationToken)
    {
        if (_dispatcher is null || _contents.Count != 1)
        {
            StatusMessage = "Save as PDF is only available for a single bill preview.";
            return;
        }

        var options = BuildOptions();
        if (options.Count != 1) return;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"{_contents[0].InvoiceNumber.Replace('/', '-')}.pdf",
            Filter = "PDF files (*.pdf)|*.pdf",
            DefaultExt = ".pdf",
            InitialDirectory = _preferences?.LastPdfDirectory is { Length: > 0 } d && Directory.Exists(d)
                ? d
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Title = "Save Bill as PDF",
        };
        if (dialog.ShowDialog() != true) return;

        IsBusy = true;
        StatusMessage = "Saving PDF…";
        try
        {
            var bytes = await Task.Run(() => _dispatcher.GeneratePdf(options[0]), cancellationToken);
            await File.WriteAllBytesAsync(dialog.FileName, bytes, cancellationToken);
            var dir = Path.GetDirectoryName(dialog.FileName);
            if (!string.IsNullOrWhiteSpace(dir)) _preferences?.SaveLastPdfDirectory(dir);
            StatusMessage = $"Saved: {dialog.FileName}";
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

    private void OnStoredZoomChanged(object? sender, double value)
    {
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
