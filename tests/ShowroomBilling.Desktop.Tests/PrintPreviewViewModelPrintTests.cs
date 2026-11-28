using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
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
    public async Task PrintCommand_sends_a_multi_bill_preview_as_one_printer_job()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var preferences = new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(
            new[] { Content("INV/001"), Content("INV/002") },
            PrintLayoutOptions.Default,
            new CopyDefaults(Original: true, Duplicate: true, Triplicate: false));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() => !vm.IsBusy && vm.SelectedPrinter == "Counter Printer");

        await vm.PrintCommand.ExecuteAsync(null);

        // One call for all the bills: the dispatcher makes one job of them.
        Assert.Equal(0, dispatcher.SinglePrintCount);
        Assert.Equal(1, dispatcher.BatchPrintCount);
        Assert.Equal("Counter Printer", dispatcher.LastPrinterName);
        Assert.Equal("Counter Printer", preferences.LastPrinterName);
        Assert.Equal("Sent to printer.", vm.StatusMessage);

        var printed = Assert.IsAssignableFrom<IReadOnlyList<PrintDocumentOptions>>(dispatcher.LastBatch);
        Assert.Equal(2, printed.Count);
        Assert.Equal("INV/001", printed[0].Content.InvoiceNumber);
        Assert.Equal("INV/002", printed[1].Content.InvoiceNumber);
        Assert.Equal(new[] { CopyLabel.Original, CopyLabel.Duplicate }, printed[0].Copies);
        Assert.Equal(new[] { CopyLabel.Original, CopyLabel.Duplicate }, printed[1].Copies);
    }

    [Fact]
    public async Task Opening_the_preview_does_not_make_printer_pages_before_Print_is_pressed()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var vm = new PrintPreviewViewModel(dispatcher, new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" });

        vm.Initialize(Content("INV/001"));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() => !vm.IsBusy && vm.SelectedPrinter == "Counter Printer");

        // The pages for the printer used to be rendered here, at 600 DPI, so
        // that Print would not have to wait for them. They now take a moment.
        Assert.Equal(0, dispatcher.BatchPrintCount);
        Assert.Equal(string.Empty, vm.StatusMessage);
    }

    [Fact]
    public async Task A_print_the_printer_refuses_is_reported_and_the_preview_stays_open()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer") { PrintFailure = new InvalidOperationException("The printer is offline.") };
        var vm = new PrintPreviewViewModel(dispatcher, new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" });
        var closed = false;

        vm.Initialize(Content("INV/001"), onPrintSucceeded: () => closed = true);
        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() => !vm.IsBusy && vm.SelectedPrinter == "Counter Printer");

        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal("Print failed: The printer is offline.", vm.StatusMessage);
        Assert.False(closed);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Opening_the_preview_shows_a_printer_added_since_the_last_open()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var vm = new PrintPreviewViewModel(dispatcher, new FakePrintPreferencesStore());
        var copies = new CopyDefaults(Original: true, Duplicate: false, Triplicate: false);

        vm.Initialize(new[] { Content("INV/001") }, PrintLayoutOptions.Default, copies);
        await WaitForAsync(() => vm.Printers.Count == 1);

        dispatcher.Installed.Add("Back Office Printer");
        vm.Initialize(new[] { Content("INV/001") }, PrintLayoutOptions.Default, copies);
        await WaitForAsync(() => vm.Printers.Count == 2);

        Assert.Equal(new[] { "Counter Printer", "Back Office Printer" }, vm.Printers);
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
    public void Collation_selection_saves_local_preferences()
    {
        var preferences = new FakePrintPreferencesStore();
        var vm = new PrintPreviewViewModel(dispatcher: null, preferences)
        {
            SelectedCollationMode = PrintCollationMode.Uncollated,
        };

        var expected = new PrintJobSettings(
            PrintDuplexMode.PrinterDefault,
            PrintColorMode.PrinterDefault,
            PrintCollationMode.Uncollated);
        Assert.Equal(expected, vm.CurrentPrintJobSettings);
        Assert.Equal(expected, preferences.PrintJobSettings);
    }

    [Fact]
    public async Task PrintCommand_passes_selected_print_settings_to_dispatcher()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var preferences = new FakePrintPreferencesStore { LastPrinterName = "Counter Printer" };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(Content("INV/003"));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() => vm.DuplexOptions.Any(x => x.Value == PrintDuplexMode.TwoSidedLongEdge));

        vm.SelectedDuplexMode = NonDefaultSettings.Duplex;
        vm.SelectedColorMode = NonDefaultSettings.Color;
        vm.SelectedCollationMode = NonDefaultSettings.Collation;

        await vm.PrintCommand.ExecuteAsync(null);

        Assert.Equal(1, dispatcher.BatchPrintCount);
        Assert.Equal(NonDefaultSettings, dispatcher.LastPrintSettings);
    }

    [Fact]
    public async Task Persisted_supported_print_settings_survive_capability_refresh()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
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
            && vm.DuplexOptions.Any(x => x.Value == PrintDuplexMode.TwoSidedLongEdge));

        Assert.Equal(NonDefaultSettings, vm.CurrentPrintJobSettings);
        Assert.Equal(NonDefaultSettings, preferences.PrintJobSettings);
    }

    [Fact]
    public async Task Capability_refresh_notifies_WPF_to_redisplay_unchanged_remembered_settings()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer");
        var preferences = new FakePrintPreferencesStore
        {
            LastPrinterName = "Counter Printer",
            PrintJobSettings = NonDefaultSettings,
        };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);
        var changedProperties = new List<string?>();
        vm.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        vm.Initialize(Content("INV/004"));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() =>
            changedProperties.Contains(nameof(PrintPreviewViewModel.SelectedDuplexMode))
            && changedProperties.Contains(nameof(PrintPreviewViewModel.SelectedColorMode))
            && changedProperties.Contains(nameof(PrintPreviewViewModel.SelectedCollationMode)));

        Assert.Equal(NonDefaultSettings, vm.CurrentPrintJobSettings);
    }

    [Fact]
    public async Task Unsupported_printer_settings_fall_back_to_printer_defaults_without_erasing_preferences()
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

        Assert.Equal(NonDefaultSettings, preferences.PrintJobSettings);
    }

    [Fact]
    public async Task Unknown_printer_capabilities_do_not_overwrite_saved_print_settings()
    {
        var dispatcher = new FakePrintDispatcher("Counter Printer")
        {
            Capabilities = PrintJobCapabilities.Unknown,
        };
        var preferences = new FakePrintPreferencesStore
        {
            LastPrinterName = "Counter Printer",
            PrintJobSettings = NonDefaultSettings,
        };
        var vm = new PrintPreviewViewModel(dispatcher, preferences);

        vm.Initialize(Content("INV/005"));

        await dispatcher.WaitForPreviewRenderAsync();
        await WaitForAsync(() =>
            vm.SelectedPrinter == "Counter Printer"
            && vm.DuplexOptions.Count == 1
            && vm.SelectedDuplexMode == PrintDuplexMode.PrinterDefault
            && vm.SelectedColorMode == PrintColorMode.PrinterDefault
            && vm.SelectedCollationMode == PrintCollationMode.PrinterDefault);

        Assert.Equal(NonDefaultSettings, preferences.PrintJobSettings);
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
            Gstin: "29AAAA0000A1Z5",
            BankName: null,
            BankAccount: null,
            BankIfsc: null,
            BankUpi: null,
            TermsAndConditions: null));

    private sealed class FakePrintDispatcher(string printerName) : StubPrintDispatcher
    {
        private readonly SemaphoreSlim _previewRendered = new(0);

        public int SinglePrintCount { get; private set; }
        public int BatchPrintCount { get; private set; }
        public string? LastPrinterName { get; private set; }
        public IReadOnlyList<PrintDocumentOptions>? LastBatch { get; private set; }
        public PrintJobSettings? LastPrintSettings { get; private set; }

        /// <summary>What the print queue answers a job with, when it refuses one.</summary>
        public Exception? PrintFailure { get; init; }
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

        // What Windows has now, and what the dispatcher saw when it last read.
        public List<string> Installed { get; } = [printerName];
        private string[] _lastRead = [printerName];

        public override IReadOnlyList<string> AvailablePrinters() => _lastRead;
        public override string? DefaultPrinter() => printerName;
        public override void RefreshPrinters() => _lastRead = [.. Installed];
        public override PrintJobCapabilities GetPrinterCapabilities(string selectedPrinterName) => Capabilities;

        public override IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150)
        {
            _previewRendered.Release();
            return Array.Empty<byte[]>();
        }

        public override bool PrintToPrinter(
            PrintDocumentOptions options,
            string selectedPrinterName,
            PrintJobSettings? settings = null)
        {
            SinglePrintCount++;
            LastPrinterName = selectedPrinterName;
            LastPrintSettings = settings;
            return true;
        }

        public override bool PrintToPrinter(
            IReadOnlyList<PrintDocumentOptions> options,
            string selectedPrinterName,
            PrintJobSettings? settings = null)
        {
            BatchPrintCount++;
            LastPrinterName = selectedPrinterName;
            LastBatch = options.ToArray();
            LastPrintSettings = settings;
            if (PrintFailure is not null)
            {
                throw PrintFailure;
            }

            return true;
        }

        public async Task WaitForPreviewRenderAsync()
        {
            if (!await _previewRendered.WaitAsync(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Preview render did not start before the timeout.");
            }
        }
    }
}
