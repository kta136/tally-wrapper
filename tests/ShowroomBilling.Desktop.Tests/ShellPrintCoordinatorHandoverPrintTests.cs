using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Handover;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// How the saved bills of a hand-over are sent to the printer: several as one print job, or
/// as a job each when the paper is printed on both sides. The printer is a stand-in.
/// </summary>
public sealed class ShellPrintCoordinatorHandoverPrintTests
{
    private readonly Printer _printer = new();
    private readonly Preferences _preferences = new() { LastPrinterName = "Counter Printer" };

    [Fact]
    public async Task Several_bills_go_to_the_printer_as_one_job_in_order_each_with_its_copies()
    {
        var coordinator = Coordinator(copyDuplicate: true);

        var printed = await coordinator.PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B"), Bill("Buyer C")]);

        Assert.Equal([true, true, true], printed);
        var job = Assert.Single(_printer.Jobs);
        Assert.Equal("Counter Printer", job.PrinterName);
        Assert.Equal(["Buyer A", "Buyer B", "Buyer C"], job.Bills.Select(bill => bill.Content.PartyName));

        // All of the first bill's copies, then the second's: each bill is a document of the job.
        Assert.All(job.Bills, bill => Assert.Equal([CopyLabel.Original, CopyLabel.Duplicate], bill.Copies));
        Assert.Equal(_preferences.PrintJobSettings, job.Settings);
    }

    [Fact]
    public async Task One_bill_is_printed_as_it_always_was_and_the_printer_is_not_asked_about_its_sides()
    {
        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A")]);

        Assert.Equal([true], printed);
        var job = Assert.Single(_printer.Jobs);
        Assert.True(job.SentAsOneBill);
        Assert.Equal(0, _printer.AskedAboutSides);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Printed_on_both_sides_each_bill_is_a_job_of_its_own(bool sidesCannotBeRead)
    {
        // In one job the second bill would start on the back of the first bill's page. Not
        // being able to tell how the printer is set counts as two-sided.
        _printer.TwoSided = true;
        _printer.AskingAboutSidesFails = sidesCannotBeRead;

        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B"), Bill("Buyer C")]);

        Assert.Equal([true, true, true], printed);
        Assert.Equal(3, _printer.Jobs.Count);
        Assert.Equal(["Buyer A", "Buyer B", "Buyer C"], _printer.Jobs.Select(job => Assert.Single(job.Bills).Content.PartyName));
    }

    [Fact]
    public async Task The_question_about_sides_is_put_with_the_printer_and_the_settings_the_job_will_use()
    {
        _preferences.PrintJobSettings = new PrintJobSettings(PrintDuplexMode.OneSided, PrintColorMode.Monochrome, PrintCollationMode.PrinterDefault);

        await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B")]);

        Assert.Equal(("Counter Printer", _preferences.PrintJobSettings), _printer.LastSidesQuestion);
        Assert.Single(_printer.Jobs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task When_the_one_job_fails_none_of_its_bills_counts_as_printed(bool printerThrows)
    {
        _printer.Refuses = _ => true;
        _printer.RefusesByThrowing = printerThrows;

        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B")]);

        Assert.Equal([false, false], printed);
        Assert.Single(_printer.Jobs);
    }

    [Fact]
    public async Task With_a_job_for_each_bill_one_that_fails_leaves_the_others_printed()
    {
        _printer.TwoSided = true;
        _printer.Refuses = bill => bill.Content.PartyName == "Buyer B";

        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B"), Bill("Buyer C")]);

        Assert.Equal([true, false, true], printed);
    }

    [Fact]
    public async Task Without_a_printer_nothing_is_sent_and_no_bill_counts_as_printed()
    {
        _preferences.LastPrinterName = null;
        _printer.Default = null;

        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B")]);

        Assert.Equal([false, false], printed);
        Assert.Empty(_printer.Jobs);
    }

    [Fact]
    public async Task With_no_printer_remembered_the_Windows_default_is_used()
    {
        _preferences.LastPrinterName = null;

        await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), Bill("Buyer B")]);

        Assert.Equal("Default Printer", Assert.Single(_printer.Jobs).PrinterName);
    }

    [Fact]
    public async Task A_bill_with_nothing_to_print_is_left_out_and_the_rest_still_go_together()
    {
        var empty = FakeBillsApi.Bill(Guid.NewGuid(), payload: null);

        var printed = await Coordinator().PrintSavedBillsAsync([Bill("Buyer A"), empty, Bill("Buyer C")]);

        Assert.Equal([true, false, true], printed);
        Assert.Equal(["Buyer A", "Buyer C"], Assert.Single(_printer.Jobs).Bills.Select(bill => bill.Content.PartyName));
    }

    private ShellPrintCoordinator Coordinator(bool copyDuplicate = false)
    {
        var coordinator = new ShellPrintCoordinator(
            billsApiClient: null!,
            companyProfileProvider: new CompanyProfileProvider(),
            printLayoutOptionsProvider: new PrintLayoutOptionsProvider(new OfflineSettingsApi(), new OfflinePrintAssetApi()),
            printDispatcher: _printer,
            printPreferences: _preferences,
            host: null!);
        var print = HandoverTestMasters.Response().Settings.Print;
        coordinator.ApplyPrintSettings(print with { CopyDuplicateDefault = copyDuplicate });
        return coordinator;
    }

    private static BillResponse Bill(string party)
    {
        var handedOver = HandoverTestLinks.Read(HandoverTestLinks.Document(
            DateTimeOffset.UtcNow,
            [HandoverTestLinks.Bill(Guid.NewGuid(), party, HandoverTestLinks.Line())])).Bills[0];
        return FakeBillsApi.Bill(Guid.NewGuid(), BillStates.Pending, HandoverBillMapper.Map(handedOver, HandoverTestMasters.Masters).Payload!);
    }

    private sealed record Job(IReadOnlyList<PrintDocumentOptions> Bills, string PrinterName, PrintJobSettings? Settings, bool SentAsOneBill);

    private sealed class Printer : IPrintDispatcher
    {
        private readonly List<Job> _jobs = [];

        public IReadOnlyList<Job> Jobs
        {
            get
            {
                lock (_jobs)
                {
                    return _jobs.ToList();
                }
            }
        }

        public string? Default { get; set; } = "Default Printer";
        public bool TwoSided { get; set; }
        public bool AskingAboutSidesFails { get; set; }
        public int AskedAboutSides { get; private set; }
        public (string Printer, PrintJobSettings? Settings)? LastSidesQuestion { get; private set; }

        /// <summary>The bills whose job the printer does not take.</summary>
        public Func<PrintDocumentOptions, bool> Refuses { get; set; } = _ => false;
        public bool RefusesByThrowing { get; set; }

        public IReadOnlyList<string> AvailablePrinters() => ["Counter Printer", "Default Printer"];
        public string? DefaultPrinter() => Default;
        public void RefreshPrinters() { }
        public PrintJobCapabilities GetPrinterCapabilities(string printerName) => PrintJobCapabilities.Unknown;

        public bool PrintsOnBothSides(string printerName, PrintJobSettings? settings = null)
        {
            AskedAboutSides++;
            LastSidesQuestion = (printerName, settings);
            return AskingAboutSidesFails ? throw new InvalidOperationException("The printer did not answer.") : TwoSided;
        }

        public byte[] GeneratePdf(PrintDocumentOptions options) => [];
        public IReadOnlyList<byte[]> GeneratePageImages(PrintDocumentOptions options, int dpi = 150) => [];
        public IReadOnlyList<byte[]> GeneratePrintPageImages(IReadOnlyList<PrintDocumentOptions> options) => [];
        public bool PrintRenderedPages(IReadOnlyList<byte[]> pageImages, string printerName, PrintJobSettings? settings = null) => false;

        public bool PrintToPrinter(PrintDocumentOptions options, string printerName, PrintJobSettings? settings = null)
            => Send(new Job([options], printerName, settings, SentAsOneBill: true));

        public bool PrintToPrinter(IReadOnlyList<PrintDocumentOptions> options, string printerName, PrintJobSettings? settings = null)
            => Send(new Job(options.ToList(), printerName, settings, SentAsOneBill: false));

        private bool Send(Job job)
        {
            lock (_jobs)
            {
                _jobs.Add(job);
            }

            if (!job.Bills.Any(Refuses))
            {
                return true;
            }

            return RefusesByThrowing ? throw new InvalidOperationException("The printer is offline.") : false;
        }
    }

    private sealed class Preferences : IPrintPreferencesStore
    {
        public event EventHandler<double>? PrintPreviewZoomPercentChanged
        {
            add { }
            remove { }
        }

        public string? LastPrinterName { get; set; }
        public string? LastPdfDirectory => null;
        public bool DirectPrintAfterSave => true;
        public double PrintPreviewZoomPercent => 100;
        public PrintJobSettings PrintJobSettings { get; set; } = PrintJobSettings.Default;

        public void SaveLastPrinter(string printerName) { }
        public void SaveLastPdfDirectory(string directory) { }
        public void SaveDirectPrintAfterSave(bool value) { }
        public void SavePrintPreviewZoomPercent(double value) { }
        public void SavePrintJobSettings(PrintJobSettings settings) { }
    }
}

/// <summary>What the real dispatcher answers about two-sided printing without asking a printer.</summary>
public sealed class PrintDispatcherTwoSidedTests
{
    private const string NoSuchPrinter = "No Such Printer (test)";

    [Theory]
    [InlineData(PrintDuplexMode.OneSided, false)]
    [InlineData(PrintDuplexMode.TwoSidedLongEdge, true)]
    [InlineData(PrintDuplexMode.TwoSidedShortEdge, true)]
    public void The_job_settings_decide_when_they_name_a_side(PrintDuplexMode duplex, bool bothSides)
    {
        var settings = new PrintJobSettings(duplex, PrintColorMode.PrinterDefault, PrintCollationMode.PrinterDefault);

        // The printer does not exist, so it was not asked.
        Assert.Equal(bothSides, new PrintDispatcher(new BillPrintRenderer()).PrintsOnBothSides(NoSuchPrinter, settings));
    }

    [Fact]
    public void A_printer_that_cannot_be_asked_counts_as_two_sided()
    {
        // "Printer default" leaves it to the printer, and this one cannot be read: a job for
        // each bill is always right, one job for all of them may not be.
        Assert.True(new PrintDispatcher(new BillPrintRenderer()).PrintsOnBothSides(NoSuchPrinter, PrintJobSettings.Default));
        Assert.True(new PrintDispatcher(new BillPrintRenderer()).PrintsOnBothSides(NoSuchPrinter));
    }
}
