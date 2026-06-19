using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests;

public sealed class PrintPreviewViewModelPrintTests
{
    private static readonly PrintJobSettings NonDefaultSettings = new(
        PrintDuplexMode.TwoSidedLongEdge,
        PrintColorMode.Monochrome,
        PrintCollationMode.Collated);

    [Fact]
    public async Task PrintCommand_sends_multi_bill_preview_as_one_prepared_printer_job()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var preferences = new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(
            new[] { Content("INV/001"), Content("INV/002") },
            PrintLayoutOptions.Default,
            new CopyDefaults(Original: true, Duplicate: true, Triplicate: false));

        await dispatcher.WaitForPreviewRenderAsync();
        await dispatcher.WaitForPrintPreparationAsync();
        await WaitForAsync(() => !vm.IsBusy && vm.SelectedPrinter == "Counter Printer");

        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal(0, dispatcher.SinglePrintCount);
        Assert.Equal(0, dispatcher.BatchPrintCount);
        Assert.Equal(1, dispatcher.RenderedPrintCount);
        Assert.Equal("Counter Printer", dispatcher.LastPrinterName);
        Assert.Equal("Counter Printer", preferences.LastPrinterName);
        Assert.Equal("Sent to printer.", vm.StatusMessage);

        var printed = Assert.IsAssignableFrom<IReadOnlyList<PrintDocumentOptions>>(dispatcher.LastPreparedOptions);
        Assert.Equal(2, printed.Count);
        Assert.Equal("INV/001", printed[0].Content.InvoiceNumber);
        Assert.Equal("INV/002", printed[1].Content.InvoiceNumber);
        Assert.Equal(new[] { CopyLabel.Original, CopyLabel.Duplicate }, printed[0].Copies);
        Assert.Equal(new[] { CopyLabel.Original, CopyLabel.Duplicate }, printed[1].Copies);

        var renderedPages = Assert.IsAssignableFrom<IReadOnlyList<byte[]>>(dispatcher.LastRenderedPages);
        Assert.Single(renderedPages);
        Assert.Equal(new byte[] { 1, 2, 3 }, renderedPages[0]);
    }

    [Fact]
    public void Print_settings_selection_saves_local_preferences()
    {
        var preferences = new FakePrintPreferencesStore();
        var vm = new PrintPreviewViewModel(dispatcher: null, preferences)
        {
            SelectedDuplexMode = PrintDuplexMode.TwoSidedLongEdge,
            SelectedColorMode = PrintColorMode.Monochrome,
            SelectedCollationMode = PrintCollationMode.Collated,
        };

        Assert.Equal(NonDefaultSettings, vm.CurrentPrintJobSettings);
        Assert.Equal(NonDefaultSettings, preferences.PrintJobSettings);
    }

    [Fact]
    public async Task PrintCommand_passes_selected_print_settings_to_dispatcher()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var preferences = new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(Content("INV/003"));

        await dispatcher.WaitForPreviewRenderAsync();
        await dispatcher.WaitForPrintPreparationAsync();
        await WaitForAsync(() => vm.DuplexOptions.Any(x => x.Value == PrintDuplexMode.TwoSidedLongEdge));

        vm.SelectedDuplexMode = NonDefaultSettings.Duplex;
        vm.SelectedColorMode = NonDefaultSettings.Color;
        vm.SelectedCollationMode = NonDefaultSettings.Collation;

        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal(1, dispatcher.RenderedPrintCount);
        Assert.Equal(NonDefaultSettings, dispatcher.LastPrintSettings);
    }

    [Fact]
    public async Task Unsupported_printer_settings_fall_back_to_printer_defaults()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer")
        {
            Capabilities = new PrintJobCapabilities(
                IsKnown: true,
                DuplexModes: [PrintDuplexMode.PrinterDefault, PrintDuplexMode.OneSided],
                ColorModes: [PrintColorMode.PrinterDefault],
                CollationModes: [PrintCollationMode.PrinterDefault]),
        };
        var preferences = new FakePrintPreferencesStore
        {
            LastPrinterName = "Counter Printer",
            PrintJobSettings = NonDefaultSettings,
        };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(Content("INV/004"));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() =>
            vm.SelectedPrinter == "Counter Printer"
            && vm.DuplexOptions.Count == 2
            && vm.SelectedDuplexMode == PrintDuplexMode.PrinterDefault
            && vm.SelectedColorMode == PrintColorMode.PrinterDefault
            && vm.SelectedCollationMode == PrintCollationMode.PrinterDefault);

        Assert.Equal(PrintJobSettings.Default, preferences.PrintJobSettings);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not met before the timeout.");
            }

            await Task.Delay(10);
        }
    }

    private static BillPrintContent Content(string invoiceNumber) => new(
        InvoiceNumber: invoiceNumber,
        BillDate: new DateOnly(2026, 6, 18),
        PartyName: "Walk-in Customer",
        PartyGstin: null,
        PartyPhone: null,
        PartyAddress: null,
        Payment: "Cash",
        Rate24Kt: null,
        Lines: new[]
        {
            new BillLineItemDto(
                ItemName: "Gold Chain",
                HsnCode: "7113",
                Quantity: 10m,
                Unit: "g",
                Rate: 6000m,
                LineTotal: 60000m,
                Karat: "22K",
                RawJson: null),
        },
        Totals: new BillTotalsDto(
            Subtotal: 60000m,
            DiscountTotal: 0m,
            TaxTotal: 1800m,
            RoundOff: 0m,
            GrandTotal: 61800m),
        Notes: null,
        Company: new CompanyProfile(
            Name: "Acme Jewellers",
            Address: "Main Road",
            Phone: null,
            State: null,
            Country: "India",
            Gstin: "29AAAA0000A1Z5",
            Huid: null,
            BankName: null,
            BankAccount: null,
            BankIfsc: null,
            BankUpi: null,
            TermsAndConditions: null));

    private sealed class FakePrintDispatcher(string printerName) : IPrintDispatcher
    {
        private readonly SemaphoreSlim _previewRendered = new(0);
        private readonly SemaphoreSlim _printPrepared = new(0);

        public int SinglePrintCount { get; private set; }
        public int BatchPrintCount { get; private set; }
        public int RenderedPrintCount { get; private set; }
        public string? LastPrinterName { get; private set; }
        public IReadOnlyList<PrintDocumentOptions>? LastBatch { get; private set; }
        public IReadOnlyList<PrintDocumentOptions>? LastPreparedOptions { get; private set; }
        public IReadOnlyList<byte[]>? LastRenderedPages { get; private set; }
        public PrintJobSettings? LastPrintSettings { get; private set; }
        public PrintJobCapabilities Capabilities { get; set; } = new(
            IsKnown: true,
            DuplexModes:
            [
                PrintDuplexMode.PrinterDefault,
                PrintDuplexMode.OneSided,
                PrintDuplexMode.TwoSidedLongEdge,
                PrintDuplexMode.TwoSidedShortEdge,
            ],
            ColorModes: [PrintColorMode.PrinterDefault, PrintColorMode.Color, PrintColorMode.Monochrome],
            CollationModes:
            [
                PrintCollationMode.PrinterDefault,
                PrintCollationMode.Collated,
                PrintCollationMode.Uncollated,
            ]);

        public IReadOnlyList<string> AvailablePrinters() => new[] { printerName };
        public string? DefaultPrinter() => printerName;
        public PrintJobCapabilities GetPrinterCapabilities(string selectedPrinterName) => Capabilities;
        public byte[] GeneratePdf(PrintDocumentOptions options) => Array.Empty<byte>();

        public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150)
        {
            _previewRendered.Release();
            return Array.Empty<byte[]>();
        }

        public IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options)
        {
            LastPreparedOptions = options.ToArray();
            _printPrepared.Release();
            return new[] { new byte[] { 1, 2, 3 } };
        }

        public bool PrintToPrinter(
            PrintDocumentOptions options,
            string selectedPrinterName,
            PrintJobSettings? settings = null)
        {
            SinglePrintCount++;
            LastPrinterName = selectedPrinterName;
            LastPrintSettings = settings;
            return true;
        }

        public bool PrintToPrinter(
            IReadOnlyList<PrintDocumentOptions> options,
            string selectedPrinterName,
            PrintJobSettings? settings = null)
        {
            BatchPrintCount++;
            LastPrinterName = selectedPrinterName;
            LastBatch = options.ToArray();
            LastPrintSettings = settings;
            return true;
        }

        public bool PrintRenderedPages(
            IReadOnlyList<byte[]> pageImages,
            string selectedPrinterName,
            PrintJobSettings? settings = null)
        {
            RenderedPrintCount++;
            LastPrinterName = selectedPrinterName;
            LastRenderedPages = pageImages.ToArray();
            LastPrintSettings = settings;
            return pageImages.Count > 0;
        }

        public string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileNameWithoutExtension) => string.Empty;

        public async Task WaitForPreviewRenderAsync()
        {
            if (!await _previewRendered.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Preview render did not start before the timeout.");
            }
        }

        public async Task WaitForPrintPreparationAsync()
        {
            if (!await _printPrepared.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Print preparation did not start before the timeout.");
            }
        }
    }

    private sealed class FakePrintPreferencesStore : IPrintPreferencesStore
    {
        public event EventHandler<double>? PrintPreviewZoomPercentChanged;

        public string? LastPrinterName { get; set; }
        public string? LastPdfDirectory { get; private set; }
        public bool DirectPrintAfterSave { get; private set; }
        public double PrintPreviewZoomPercent { get; set; } = 100;
        public PrintJobSettings PrintJobSettings { get; set; } = PrintJobSettings.Default;

        public void SaveLastPrinter(string printerName) => LastPrinterName = printerName;
        public void SaveLastPdfDirectory(string directory) => LastPdfDirectory = directory;
        public void SaveDirectPrintAfterSave(bool value) => DirectPrintAfterSave = value;
        public void SavePrintJobSettings(PrintJobSettings settings) => PrintJobSettings = settings;

        public void SavePrintPreviewZoomPercent(double value)
        {
            PrintPreviewZoomPercent = value;
            PrintPreviewZoomPercentChanged?.Invoke(this, value);
        }
    }
}
