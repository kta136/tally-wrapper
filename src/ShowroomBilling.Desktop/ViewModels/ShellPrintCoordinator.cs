using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Shell;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels;

internal interface IShellPrintHost
{
    string? ActiveDialog { get; set; }
    InvoiceViewModel Invoice { get; }
    BillsViewModel Bills { get; }
    BillDetailsViewModel BillDetails { get; }
    PrintPreviewViewModel PrintPreview { get; }
}

internal sealed class ShellPrintCoordinator(
    IBillsApiClient billsApiClient,
    ICompanyProfileProvider companyProfileProvider,
    IPrintLayoutOptionsProvider printLayoutOptionsProvider,
    IPrintDispatcher printDispatcher,
    IPrintPreferencesStore printPreferences,
    IShellPrintHost host)
{
    private PrintSettingsDto? _printDefaults;
    // The post-save prints so far: each new one runs after it.
    private Task _postSavePrints = Task.CompletedTask;

    public void ApplyPrintSettings(PrintSettingsDto print, MasterDataSettingsDto? masters = null)
    {
        _printDefaults = print;
        companyProfileProvider.Apply(print);
        if (masters is not null)
        {
            companyProfileProvider.ApplyMasters(masters);
        }
        printLayoutOptionsProvider.ApplyFontSizes(print.PrintFontSize, print.PrintTermsFontSize);
    }

    public async Task RefreshPrintLayoutAsync()
    {
        try
        {
            await printLayoutOptionsProvider.RefreshAsync();
        }
        catch
        {
            // Best effort; preview falls back to the previous layout.
        }
    }

    /// <summary>
    /// Called when the Invoice tab has saved a bill: frees the form for the
    /// next bill at once, then prints the saved one.
    /// </summary>
    public Task HandleInvoiceSaveCompletedAsync()
    {
        // What to print is taken now, from the bill as it was saved, because
        // the form is emptied on the next line and the print may have to wait
        // for the one before it.
        var content = host.Invoice.BuildSavedPrintContent(companyProfileProvider.Current);
        var layout = printLayoutOptionsProvider.Current;

        // The bill is saved, so nothing on the form is needed any more. The
        // form used to be emptied only when the print had finished, which
        // wiped whatever the operator had started on the next bill meanwhile.
        host.Invoice.ClearCommand.Execute(null);
        if (content is null)
        {
            return Task.CompletedTask;
        }

        // One print at a time, in the order the bills were saved. A bill
        // saved while another is printing waits its turn; it used to be
        // skipped without a word.
        _postSavePrints = PrintAfterAsync(_postSavePrints, content, layout);
        return _postSavePrints;
    }

    private async Task PrintAfterAsync(Task earlierPrints, BillPrintContent content, PrintLayoutOptions layout)
    {
        try
        {
            await earlierPrints;
        }
        catch
        {
            // An earlier bill's print failing is that bill's problem.
        }

        await PrintSavedInvoiceAsync(content, layout);
    }

    private async Task PrintSavedInvoiceAsync(BillPrintContent content, PrintLayoutOptions layout)
    {
        var number = content.InvoiceNumber;
        var directPrintFailed = false;

        if (printPreferences.DirectPrintAfterSave)
        {
            var printer = printPreferences.LastPrinterName ?? printDispatcher.DefaultPrinter();
            if (!string.IsNullOrWhiteSpace(printer))
            {
                var options = PrintDocumentOptions.ForInvoice(
                    content,
                    BuildCopiesFromDefaults(),
                    layout);

                host.Invoice.SaveStatus = $"Saved · {number} · printing to {printer}…";

                bool printed;
                try
                {
                    printed = await Task.Run(() =>
                        printDispatcher.PrintToPrinter(options, printer!, printPreferences.PrintJobSettings));
                }
                catch
                {
                    printed = false;
                }

                if (printed)
                {
                    host.Invoice.SaveStatus = $"Printed · {number} · {printer}";
                    return;
                }

                directPrintFailed = true;
            }
        }

        // By now the operator may be in another dialog (a direct print takes
        // seconds). Opening the preview would replace that dialog, so the
        // status line says what to do instead.
        if (host.ActiveDialog is not null)
        {
            host.Invoice.SaveStatus = $"{number} was saved but not printed. Print it from the Bills tab.";
            return;
        }

        if (directPrintFailed)
        {
            host.Invoice.ShowStatus($"Saved · {number} · direct print failed, so the preview is open", "warn");
        }
        else
        {
            host.Invoice.SaveStatus = $"Saved · {number}";
        }

        OpenPreview([content], layout);
    }

    /// <summary>
    /// Prints saved bills straight to the printer, as a direct print after save does: the
    /// remembered printer, else the Windows default, with the copy defaults of the settings.
    /// Several bills go as one print job, in the order given, each with its copies. Printed on
    /// both sides of the paper they go as a job each instead, or one bill would start on the
    /// back of another's last page. The answer says for each bill whether it was sent: false
    /// when there is no printer or its job failed; the caller then opens the preview.
    /// </summary>
    public async Task<IReadOnlyList<bool>> PrintSavedBillsAsync(IReadOnlyList<BillResponse> bills)
    {
        var printed = new bool[bills.Count];
        var printer = printPreferences.LastPrinterName ?? printDispatcher.DefaultPrinter();
        if (string.IsNullOrWhiteSpace(printer))
        {
            return printed;
        }

        var copies = BuildCopiesFromDefaults();
        var layout = printLayoutOptionsProvider.Current;
        var settings = printPreferences.PrintJobSettings;
        var jobs = new List<(int Bill, PrintDocumentOptions Options)>(bills.Count);
        for (var i = 0; i < bills.Count; i++)
        {
            var content = BillDetailsViewModel.CreatePrintContent(
                bills[i],
                companyProfileProvider.Current,
                companyProfileProvider.KaratMappings);
            if (content is not null)
            {
                jobs.Add((i, PrintDocumentOptions.ForInvoice(content, copies, layout)));
            }
        }

        await Task.Run(() =>
        {
            if (jobs.Count > 1 && !PrintsOnBothSides(printer, settings))
            {
                var sent = Sent(() => printDispatcher.PrintToPrinter(jobs.Select(job => job.Options).ToList(), printer, settings));
                foreach (var job in jobs)
                {
                    printed[job.Bill] = sent;
                }

                return;
            }

            // One bill, or two-sided printing: a job for each, as every bill used to be printed.
            foreach (var job in jobs)
            {
                printed[job.Bill] = Sent(() => printDispatcher.PrintToPrinter(job.Options, printer, settings));
            }
        });

        return printed;

        static bool Sent(Func<bool> print)
        {
            try
            {
                return print();
            }
            catch
            {
                return false;
            }
        }
    }

    private bool PrintsOnBothSides(string printer, PrintJobSettings settings)
    {
        try
        {
            return printDispatcher.PrintsOnBothSides(printer, settings);
        }
        catch
        {
            // Not knowing is treated as two-sided: a job for each bill is always right.
            return true;
        }
    }

    /// <summary>Opens Print preview with saved bills, merged, for the operator to print by hand.</summary>
    public void OpenSavedBillsPreview(IReadOnlyList<BillResponse> bills)
    {
        var contents = bills
            .Select(bill => BillDetailsViewModel.CreatePrintContent(
                bill,
                companyProfileProvider.Current,
                companyProfileProvider.KaratMappings))
            .OfType<BillPrintContent>()
            .ToList();
        if (contents.Count > 0)
        {
            OpenPreview(contents, printLayoutOptionsProvider.Current);
        }
    }

    public void OpenInvoicePreview()
    {
        if (host.Invoice.RefuseEstimateIfNotPriceable())
        {
            return;
        }

        var content = host.Invoice.BuildPrintContent(companyProfileProvider.Current);
        if (content is null)
        {
            host.Invoice.SaveStatus = "Add a line before previewing the invoice.";
            return;
        }
        OpenPreview([content], printLayoutOptionsProvider.Current);
    }

    public void OpenFinalPrintPreview()
    {
        var content = host.BillDetails.BuildPrintContent(
            companyProfileProvider.Current,
            companyProfileProvider.KaratMappings);
        if (content is null) return;
        OpenPreview([content], printLayoutOptionsProvider.Current);
    }

    public async Task OpenSelectedBillsPrintPreviewAsync(CancellationToken cancellationToken)
    {
        var selectedIds = host.Bills.SelectedBillIds;
        if (selectedIds.Count == 0) return;

        var response = await billsApiClient.GetManyAsync(new BillBatchGetRequest(selectedIds), cancellationToken);
        var contents = new List<BillPrintContent>(response.Bills.Count);
        foreach (var bill in response.Bills)
        {
            var content = BillDetailsViewModel.CreatePrintContent(
                bill,
                companyProfileProvider.Current,
                companyProfileProvider.KaratMappings);
            if (content is not null)
            {
                contents.Add(content);
            }
        }

        if (contents.Count == 0) return;

        OpenPreview(contents, printLayoutOptionsProvider.Current);
    }

    private void OpenPreview(IReadOnlyList<BillPrintContent> contents, PrintLayoutOptions? layout)
    {
        host.ActiveDialog = ShellDialogs.PrintPreview;
        host.PrintPreview.Initialize(contents, layout, ResolveCopyDefaults(), onPrintSucceeded: () => host.ActiveDialog = null);
    }

    public Task WarmUpPrintPipelineAsync() => Task.Run(() =>
    {
        try
        {
            var content = PreviewSampleProvider.CreateSampleContent(companyProfileProvider.Current);
            var options = PrintDocumentOptions.ForInvoice(
                content,
                [CopyLabel.Original],
                printLayoutOptionsProvider.Current);
            _ = printDispatcher.GeneratePdf(options);
            _ = printDispatcher.AvailablePrinters();

            // The first question to a printer in a run of the app is the slow one (most of a
            // second, where later ones take about 10 ms). A hand-over of several bills asks
            // this before it prints; asked here once, it does not wait for it then.
            if ((printPreferences.LastPrinterName ?? printDispatcher.DefaultPrinter()) is { Length: > 0 } printer)
            {
                _ = printDispatcher.PrintsOnBothSides(printer, printPreferences.PrintJobSettings);
            }
        }
        catch
        {
            // Best effort. The real print/preview path will surface failures.
        }
    });

    private IReadOnlyList<CopyLabel> BuildCopiesFromDefaults()
    {
        var copies = new List<CopyLabel>(3);
        if (_printDefaults is null)
        {
            copies.Add(CopyLabel.Original);
            return copies;
        }

        if (_printDefaults.CopyOriginalDefault) copies.Add(CopyLabel.Original);
        if (_printDefaults.CopyDuplicateDefault) copies.Add(CopyLabel.Duplicate);
        if (_printDefaults.CopyTriplicateDefault) copies.Add(CopyLabel.Triplicate);
        if (copies.Count == 0) copies.Add(CopyLabel.Original);
        return copies;
    }

    private CopyDefaults? ResolveCopyDefaults()
    {
        if (_printDefaults is null) return null;
        return new CopyDefaults(
            Original: _printDefaults.CopyOriginalDefault,
            Duplicate: _printDefaults.CopyDuplicateDefault,
            Triplicate: _printDefaults.CopyTriplicateDefault);
    }
}
