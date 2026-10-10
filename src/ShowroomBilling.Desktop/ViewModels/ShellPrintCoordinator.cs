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
    private bool _invoiceAwaitingPostPrintReset;
    private bool _postSavePrintInProgress;

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

    public async Task HandleInvoiceSaveCompletedAsync()
    {
        // A second Ctrl+S while the first save is still printing would
        // otherwise print another "Original".
        if (_postSavePrintInProgress)
        {
            return;
        }

        _postSavePrintInProgress = true;
        try
        {
            await PrintSavedInvoiceAsync();
        }
        finally
        {
            _postSavePrintInProgress = false;
        }
    }

    private async Task PrintSavedInvoiceAsync()
    {
        var content = host.Invoice.BuildSavedPrintContent(companyProfileProvider.Current);
        if (content is null)
        {
            host.Invoice.ClearCommand.Execute(null);
            return;
        }

        var layout = printLayoutOptionsProvider.Current;

        if (printPreferences.DirectPrintAfterSave)
        {
            var printer = printPreferences.LastPrinterName ?? printDispatcher.DefaultPrinter();
            if (!string.IsNullOrWhiteSpace(printer))
            {
                var options = PrintDocumentOptions.ForInvoice(
                    content,
                    BuildCopiesFromDefaults(),
                    layout);

                host.Invoice.SaveStatus = $"Printing to {printer}…";

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
                    host.Invoice.SaveStatus = $"Printed · {printer}";
                    host.Invoice.ClearCommand.Execute(null);
                    return;
                }

                host.Invoice.SaveStatus = "Direct print failed — opening preview.";
            }
        }

        _invoiceAwaitingPostPrintReset = true;
        OpenPreview([content], layout);
    }

    public void OpenInvoicePreview()
    {
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
        }
        catch
        {
            // Best effort. The real print/preview path will surface failures.
        }
    });

    public void OnActiveDialogChanged(string? value)
    {
        if (value is null && _invoiceAwaitingPostPrintReset)
        {
            _invoiceAwaitingPostPrintReset = false;
            host.Invoice.ClearCommand.Execute(null);
        }
    }

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
