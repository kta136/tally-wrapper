using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
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

        await coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);

        Assert.Equal(1, dispatcher.PrintCount);
        Assert.Equal("Counter Printer", dispatcher.LastPrinterName);
        Assert.Equal(settings, dispatcher.LastPrintSettings);
        Assert.Equal("Printed · INV/005 · Counter Printer", host.Invoice.SaveStatus);
        Assert.Null(host.ActiveDialog);
    }

    [Fact]
    public async Task The_form_is_free_for_the_next_bill_while_the_saved_one_prints()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher { HoldPrints = true };
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: true);
        PopulateInvoice(host.Invoice);

        var printing = coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);

        // Emptied at save, not when the printer is done.
        Assert.Equal(0, host.Invoice.ItemCount);
        Assert.Equal(string.Empty, host.Invoice.PartyName);

        // The operator starts the next bill while the first is still printing.
        PopulateInvoice(host.Invoice, number: "INV/006", party: "Next Customer");
        dispatcher.ReleasePrints();
        await printing;

        Assert.Equal(["INV/005"], dispatcher.Printed);
        Assert.Equal("Next Customer", host.Invoice.PartyName);
        Assert.Equal(1, host.Invoice.ItemCount);
        Assert.Null(host.ActiveDialog);
    }

    [Fact]
    public async Task A_bill_saved_while_another_prints_is_printed_after_it()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher { HoldPrints = true };
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: true);

        PopulateInvoice(host.Invoice);
        var first = coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);
        PopulateInvoice(host.Invoice, number: "INV/006", party: "Next Customer");
        var second = coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);

        dispatcher.ReleasePrints();
        await Task.WhenAll(first, second);

        Assert.Equal(["INV/005", "INV/006"], dispatcher.Printed);
        Assert.Equal(0, host.Invoice.ItemCount);
    }

    [Fact]
    public async Task A_failed_direct_print_opens_the_preview_without_touching_the_next_bill()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher { HoldPrints = true, PrintSucceeds = false };
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: true);
        PopulateInvoice(host.Invoice);

        var printing = coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);
        PopulateInvoice(host.Invoice, number: "INV/006", party: "Next Customer");
        dispatcher.ReleasePrints();
        await printing;

        Assert.Equal("printPreview", host.ActiveDialog);
        Assert.Contains("INV/005", host.Invoice.SaveStatus);

        // Closing the preview used to empty the form.
        host.ActiveDialog = null;
        Assert.Equal("Next Customer", host.Invoice.PartyName);
        Assert.Equal(1, host.Invoice.ItemCount);
    }

    [Fact]
    public async Task A_failed_direct_print_is_not_shown_in_the_colour_of_success()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher { PrintSucceeds = false };
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: true);
        PopulateInvoice(host.Invoice);

        await coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);

        // The message opens with "Saved", which on its own reads as green.
        Assert.StartsWith("Saved · INV/005", host.Invoice.StatusText);
        Assert.Equal("warn", host.Invoice.StatusTone);

        // A plain status after it goes back to being judged by its wording.
        host.Invoice.SaveStatus = "Saved · INV/006";
        Assert.Equal("ok", host.Invoice.StatusTone);
    }

    [Fact]
    public async Task A_failed_direct_print_does_not_replace_a_dialog_the_operator_has_opened_meanwhile()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher { HoldPrints = true, PrintSucceeds = false };
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: true);
        PopulateInvoice(host.Invoice);

        var printing = coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);
        host.ActiveDialog = "adminUnlock";
        dispatcher.ReleasePrints();
        await printing;

        Assert.Equal("adminUnlock", host.ActiveDialog);
        Assert.Equal("INV/005 was saved but not printed. Print it from the Bills tab.", host.Invoice.SaveStatus);
    }

    [Fact]
    public async Task Without_direct_print_the_form_is_emptied_and_the_preview_opens()
    {
        var host = new FakePrintHost();
        using var dispatcher = new FakePrintDispatcher();
        var coordinator = CreateCoordinator(host, dispatcher, directPrint: false);
        PopulateInvoice(host.Invoice);

        await coordinator.HandleInvoiceSaveCompletedAsync(host.Invoice.GetPrintSnapshot()!);

        Assert.Equal(0, host.Invoice.ItemCount);
        Assert.Equal("printPreview", host.ActiveDialog);
        Assert.Equal("Saved · INV/005", host.Invoice.SaveStatus);
        Assert.Empty(dispatcher.Printed);
    }

    [Fact]
    public void Invoice_preview_opens_the_preview_dialog_when_the_invoice_has_a_line()
    {
        var host = new FakePrintHost();
        var coordinator = CreateCoordinator(host);
        PopulateInvoice(host.Invoice);

        coordinator.OpenInvoicePreview();

        Assert.Equal("printPreview", host.ActiveDialog);
    }

    [Fact]
    public void Invoice_preview_with_no_lines_explains_why_nothing_opened()
    {
        var host = new FakePrintHost();
        var coordinator = CreateCoordinator(host);

        coordinator.OpenInvoicePreview();

        Assert.Null(host.ActiveDialog);
        Assert.Equal("Add a line before previewing the invoice.", host.Invoice.SaveStatus);
    }

    private static ShellPrintCoordinator CreateCoordinator(
        FakePrintHost host,
        FakePrintDispatcher? dispatcher = null,
        bool directPrint = false) => new(
        billsApiClient: null!,
        companyProfileProvider: new FakeCompanyProfileProvider(),
        printLayoutOptionsProvider: new FakePrintLayoutOptionsProvider(),
        printDispatcher: dispatcher ?? new FakePrintDispatcher(),
        printPreferences: new FakePrintPreferencesStore
        {
            DirectPrintAfterSave = directPrint,
            LastPrinterName = directPrint ? "Counter Printer" : null,
        },
        host: host);

    private static void PopulateInvoice(
        InvoiceViewModel invoice,
        string number = "INV/005",
        string party = "Walk-in Customer")
    {
        invoice.InvoiceNumber = number;
        invoice.PartyName = party;
        invoice.Rate24Kt = 6000m;
        invoice.Lines[0].ItemName = "Gold Chain";
        invoice.Lines[0].GrossWeight = 10m;
        invoice.Lines[0].Karat = "22K";
    }

    private sealed class FakePrintHost : IShellPrintHost
    {
        public string? ActiveDialog { get; set; }
        public InvoiceViewModel Invoice { get; } = new(new FakeBillsApi(), FakeNumberingApi.Offline(), new FakeDialogs(), settings: null);
        public BillsViewModel Bills { get; } = new(new FakeBillsApi(), new FakeDialogs());
        public BillDetailsViewModel BillDetails { get; } = new(new FakeBillsApi(), new FakeDialogs());
        public PrintPreviewViewModel PrintPreview { get; } = new(dispatcher: null, preferences: null);
    }

    private sealed class FakeCompanyProfileProvider : ICompanyProfileProvider
    {
        public CompanyProfile Current { get; } = new(
            Name: "Acme Jewellers",
            Address: "Main Road",
            Phone: null,
            Gstin: "29AAAA0000A1Z5",
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

    private sealed class FakePrintDispatcher : StubPrintDispatcher, IDisposable
    {
        private readonly ManualResetEventSlim _released = new(initialState: false);
        private readonly List<string> _printed = [];

        public int PrintCount { get; private set; }
        public string? LastPrinterName { get; private set; }
        public PrintJobSettings? LastPrintSettings { get; private set; }

        /// <summary>A print waits, as a real one does for the printer, until <see cref="ReleasePrints"/>.</summary>
        public bool HoldPrints { get; init; }
        public bool PrintSucceeds { get; init; } = true;

        /// <summary>The invoice numbers sent to the printer, in order.</summary>
        public IReadOnlyList<string> Printed
        {
            get
            {
                lock (_printed)
                {
                    return _printed.ToArray();
                }
            }
        }

        public void ReleasePrints() => _released.Set();
        public void Dispose() => _released.Dispose();

        public override IReadOnlyList<string> AvailablePrinters() => ["Counter Printer"];
        public override string? DefaultPrinter() => "Counter Printer";

        public override bool PrintToPrinter(
            PrintDocumentOptions options,
            string printerName,
            PrintJobSettings? settings = null)
        {
            if (HoldPrints && !_released.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new TimeoutException("The test never released the print.");
            }

            PrintCount++;
            LastPrinterName = printerName;
            LastPrintSettings = settings;
            if (PrintSucceeds)
            {
                lock (_printed)
                {
                    _printed.Add(options.Content.InvoiceNumber);
                }
            }

            return PrintSucceeds;
        }
    }
}
