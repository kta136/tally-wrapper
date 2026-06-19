using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests;

public sealed class ShellPrintCoordinatorDirectPrintTests
{
    [Fact]
    public async Task Direct_print_after_save_passes_stored_print_job_settings()
    {
        var host = new FakePrintHost();
        var dispatcher = new FakePrintDispatcher();
        var settings = new PrintJobSettings(
            PrintDuplexMode.TwoSidedShortEdge,
            PrintColorMode.Monochrome,
            PrintCollationMode.Collated);
        var preferences = new FakePrintPreferencesStore
        {
            DirectPrintAfterSave = true,
            LastPrinterName = "Counter Printer",
            PrintJobSettings = settings,
        };
        var coordinator = new ShellPrintCoordinator(
            billsApiClient: null!,
            companyProfileProvider: new FakeCompanyProfileProvider(),
            printLayoutOptionsProvider: new FakePrintLayoutOptionsProvider(),
            printDispatcher: dispatcher,
            printPreferences: preferences,
            host: host);

        PopulateInvoice(host.Invoice);

        await coordinator.HandleInvoiceSaveCompletedAsync();

        Assert.Equal(1, dispatcher.PrintCount);
        Assert.Equal("Counter Printer", dispatcher.LastPrinterName);
        Assert.Equal(settings, dispatcher.LastPrintSettings);
        Assert.Equal(string.Empty, host.Invoice.SaveStatus);
        Assert.Null(host.ActiveDialog);
    }

    private static void PopulateInvoice(InvoiceViewModel invoice)
    {
        invoice.InvoiceNumber = "INV/005";
        invoice.PartyName = "Walk-in Customer";
        invoice.Rate24Kt = 6000m;
        invoice.Lines[0].ItemName = "Gold Chain";
        invoice.Lines[0].GrossWeight = 10m;
        invoice.Lines[0].Karat = "22K";
    }

    private sealed class FakePrintHost : IShellPrintHost
    {
        public string? ActiveDialog { get; set; }
        public InvoiceViewModel Invoice { get; } = new();
        public BillsViewModel Bills { get; } = new();
        public BillDetailsViewModel BillDetails { get; } = new();
        public PrintPreviewViewModel PrintPreview { get; } = new();
    }

    private sealed class FakeCompanyProfileProvider : ICompanyProfileProvider
    {
        public CompanyProfile Current { get; } = new(
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
            TermsAndConditions: null);

        public IReadOnlyList<KaratMasterEntry> KaratMappings { get; } = Array.Empty<KaratMasterEntry>();
        public void Apply(PrintSettingsDto print) { }
        public void ApplyMasters(MasterDataSettingsDto masters) { }
    }

    private sealed class FakePrintLayoutOptionsProvider : IPrintLayoutOptionsProvider
    {
        public PrintLayoutOptions Current { get; private set; } = PrintLayoutOptions.Default;
        public Task<PrintLayoutOptions> RefreshAsync(CancellationToken cancellationToken = default) => Task.FromResult(Current);
        public void ApplyFontSizes(int invoiceFontSize, int termsFontSize)
        {
            Current = Current with { InvoiceFontSize = invoiceFontSize, TermsFontSize = termsFontSize };
        }
    }

    private sealed class FakePrintDispatcher : IPrintDispatcher
    {
        public int PrintCount { get; private set; }
        public string? LastPrinterName { get; private set; }
        public PrintJobSettings? LastPrintSettings { get; private set; }

        public IReadOnlyList<string> AvailablePrinters() => ["Counter Printer"];
        public string? DefaultPrinter() => "Counter Printer";
        public PrintJobCapabilities GetPrinterCapabilities(string printerName) => PrintJobCapabilities.Unknown;
        public byte[] GeneratePdf(PrintDocumentOptions options) => Array.Empty<byte>();
        public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150) => Array.Empty<byte[]>();
        public IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options) => Array.Empty<byte[]>();

        public bool PrintToPrinter(
            PrintDocumentOptions options,
            string printerName,
            PrintJobSettings? settings = null)
        {
            PrintCount++;
            LastPrinterName = printerName;
            LastPrintSettings = settings;
            return true;
        }

        public bool PrintToPrinter(
            IReadOnlyList<PrintDocumentOptions> options,
            string printerName,
            PrintJobSettings? settings = null) => false;

        public bool PrintRenderedPages(
            IReadOnlyList<byte[]> pageImages,
            string printerName,
            PrintJobSettings? settings = null) => false;

        public string SavePdfToDisk(PrintDocumentOptions options, string directory, string fileNameWithoutExtension) => string.Empty;
    }

    private sealed class FakePrintPreferencesStore : IPrintPreferencesStore
    {
        public event EventHandler<double>? PrintPreviewZoomPercentChanged;

        public string? LastPrinterName { get; init; }
        public string? LastPdfDirectory { get; private set; }
        public bool DirectPrintAfterSave { get; init; }
        public double PrintPreviewZoomPercent { get; set; } = 100;
        public PrintJobSettings PrintJobSettings { get; init; } = PrintJobSettings.Default;

        public void SaveLastPrinter(string printerName) { }
        public void SaveLastPdfDirectory(string directory) => LastPdfDirectory = directory;
        public void SaveDirectPrintAfterSave(bool value) { }
        public void SavePrintPreviewZoomPercent(double value)
        {
            PrintPreviewZoomPercent = value;
            PrintPreviewZoomPercentChanged?.Invoke(this, value);
        }
        public void SavePrintJobSettings(PrintJobSettings settings) { }
    }
}
