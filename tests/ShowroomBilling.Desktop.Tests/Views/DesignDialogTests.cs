using System.Windows;
using System.Windows.Controls;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;
using ShowroomBilling.Desktop.Views;
using ShowroomBilling.Desktop.Views.Admin;
using ShowroomBilling.Desktop.Views.Bills;
using ShowroomBilling.Desktop.Views.Printing;
using ShowroomBilling.Desktop.Views.Setup;
using ShowroomBilling.Desktop.Views.SyntheticBatch;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// Renders each dialog of the redesign with the sample data of its mockup, named like the mockup
/// (<c>docs/design/redesign/Dialog-Confirm.png</c> becomes <c>Design-Dialog-Confirm.png</c>). Nothing is
/// saved unless <c>TALLYWRAPPER_VIEW_SNAPSHOTS</c> is set; the dialogs are laid out and their bindings
/// checked either way.
/// </summary>
[Collection(WpfApplicationCollection.Name)]
public sealed class DesignDialogTests
{
    private static readonly Size CanvasSize = new(1100, 860);

    private static DateTimeOffset LocalAt(int day, int hour, int minute, int second = 0)
        => new(new DateTime(2026, 10, day, hour, minute, second, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, day, hour, minute, second)));

    [Fact]
    public void The_small_dialogs_lay_out_without_binding_errors()
    {
        var problems = WpfTestHost.Run(() =>
        {
            var found = new List<string>();
            using var bindingErrors = BindingErrors.Attach();

            Capture(found, bindingErrors, "Design-Dialog-Confirm", shell =>
            {
                shell.ConfirmDialog.Cancel();
                _ = shell.ConfirmDialog.ShowAsync(BillConfirmations.Repost("SR/2026-27/0007", tallyMayHoldVoucher: true));
                return new ConfirmDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Confirm-Delete", shell =>
            {
                shell.ConfirmDialog.Cancel();
                _ = shell.ConfirmDialog.ShowAsync(new ConfirmRequest(
                    "Delete SR/2026-27/0004?",
                    "This removes the bill from this database and frees its number if it was the latest. Its history stays in the audit log. Nothing is deleted in Tally.",
                    "Delete bill",
                    IsDestructive: true,
                    Note: "This cannot be undone."));
                return new ConfirmDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Reason", shell =>
            {
                shell.ReasonPromptDialog.Configure(new ReasonRequest(
                    "Mark as pushed",
                    "SR/2026-27/0006 · Karthik Ramanathan · ₹59,892",
                    "This marks SR/2026-27/0006 as posted **without contacting Tally**. Do it only after you have checked that the voucher is already in Tally.",
                    "Mark as pushed"));
                shell.ReasonPromptDialog.ReasonText = "Voucher found in Tally on 10 Oct, no. 128";
                return new ReasonPromptDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-ChangeNumber", shell =>
            {
                shell.ChangeNumberDialog.Configure("SR/2026-27/0010", "SR/", null, "2026-27", 4, "Anand Pillai · ₹1,31,592 · Pending");
                shell.ChangeNumberDialog.NewNumberRaw = "15";
                return new ChangeNumberDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-AdminUnlock", shell =>
            {
                shell.Admin.IsPasscodeConfigured = true;
                shell.Admin.Passcode = "secret";
                shell.Admin.ErrorMessage = "That passcode is not right. Try again.";
                return new AdminUnlockDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-AdminSetPasscode", shell =>
            {
                shell.Admin.IsPasscodeConfigured = false;
                return new AdminUnlockDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Shortcuts", _ => new ShortcutsDialog());

            Capture(found, bindingErrors, "Design-Dialog-SetupWizard", shell =>
            {
                var wizard = shell.SetupWizard;
                wizard.TallyHost = "127.0.0.1";
                wizard.TallyPort = "9000";
                wizard.TallyTimeoutSeconds = "30";
                wizard.ActiveCompanyName = "Sample Jewellers";
                wizard.PrintCompanyName = "Sample Jewellers";
                wizard.InvoicePrefix = "SR/";
                wizard.InvoicePadding = "4";
                wizard.SalesLedger = "Jewellery Sales";
                wizard.SalesVoucherType = "Sales";
                wizard.CashLedger = "Cash";
                wizard.RoundOffLedger = "Round Off";
                return new SetupWizardDialog();
            });

            // The batch scheduler: ready to start, with something stopping it, and with each kind of result.
            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch", shell =>
            {
                FillBatch(shell.SyntheticBatch);
                return new SyntheticBatchDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch-NoRate", shell =>
            {
                FillBatch(shell.SyntheticBatch);
                shell.SyntheticBatch.Rate24Kt = 0m;
                return new SyntheticBatchDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch-Done", shell =>
            {
                var batch = FillBatch(shell.SyntheticBatch);
                batch.RunState = "Done";
                batch.Result = new BatchResult(
                    "ok",
                    "8 bills made",
                    string.Empty,
                    "They come to **₹9,99,640** and are in the Bills tab as **pending**. Nothing was sent to Tally.",
                    BillsMayBeSaved: true,
                    "SR/2026-27/0101",
                    "SR/2026-27/0108");
                return new SyntheticBatchDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch-Stopped", shell =>
            {
                var batch = FillBatch(shell.SyntheticBatch);
                batch.RunState = "Stopped";
                batch.Result = new BatchResult(
                    "warn",
                    "Stopped after 3 of 12 bills",
                    "You cancelled the run.",
                    "The 3 saved bills are in the Bills tab as **pending**. Nothing was sent to Tally.",
                    BillsMayBeSaved: true,
                    "SR/2026-27/0101",
                    "SR/2026-27/0103");
                return new SyntheticBatchDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch-NotMade", shell =>
            {
                var batch = FillBatch(shell.SyntheticBatch);
                batch.RunState = "Failed";
                batch.Result = new BatchResult(
                    "err",
                    "The batch was not made",
                    "The From time cannot be before the last bill.",
                    "Nothing was saved.",
                    BillsMayBeSaved: false);
                return new SyntheticBatchDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Confirm-PushOrder", shell =>
            {
                shell.ConfirmDialog.Cancel();
                _ = shell.ConfirmDialog.ShowAsync(BillConfirmations.PushOrder(
                [
                    new UnpushedEarlierBill(Guid.NewGuid(), "SR/2026-27/0006", new DateOnly(2026, 10, 11), BillStates.Pending),
                    new UnpushedEarlierBill(Guid.NewGuid(), "SR/2026-27/0007", new DateOnly(2026, 10, 11), BillStates.Failed),
                ]));
                return new ConfirmDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Confirm-ItemAsTyped", shell =>
            {
                shell.ConfirmDialog.Cancel();
                // A name cut short by Ctrl+S over the sample bill's first item.
                var line = shell.Invoice.Lines.First(candidate => !candidate.IsEmpty);
                line.ItemName = "Gold Ri";
                line.ItemMaster = null;
                _ = shell.Invoice.SaveDraftCommand.ExecuteAsync(null);
                return new ConfirmDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-Confirm-Batch", shell =>
            {
                shell.ConfirmDialog.Cancel();
                _ = shell.ConfirmDialog.ShowAsync(new ConfirmRequest(
                    "Make 5 to 20 pending bills?",
                    "They are saved as soon as you say yes, and wait in the Bills tab. Nothing is sent to Tally.",
                    "Make the bills",
                    IsWarning: true,
                    Note: "The 24K rate here, ₹9,500, is 21% below the Invoice tab's ₹12,000. Check it before you go on.",
                    Facts:
                    [
                        new ConfirmFact("Adds up to", "₹10,00,000"),
                        new ConfirmFact("24K rate", "₹9,500 per gram", Marked: true),
                        new ConfirmFact("Dated", "11 Oct, 10:43 to 11 Oct, 16:45"),
                        new ConfirmFact("Shop hours", "10:00 to 20:00, not on Sunday"),
                        new ConfirmFact("Payment", "Cash"),
                        new ConfirmFact("Items per bill", "1 to 3"),
                        new ConfirmFact("Karats", "24kt, 22kt, 18kt"),
                    ]));
                return new ConfirmDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-PrintPreview", shell =>
            {
                static BillLineItemDto Line(string item, decimal net, string unit, decimal rate, decimal total, string? karat)
                    => new(item, null, net, unit, rate, total, karat, null);

                var content = new BillPrintContent(
                    "SR/2026-27/0009", new DateOnly(2026, 10, 11), "Meera Subramaniam", null, null, null, "Cash", 7780m,
                    [
                        Line("22kt Gold Chain (Rope, 18″)", 12.450m, "g", 8068.88m, 100457.56m, "22kt"),
                        Line("22kt Lakshmi Pendant", 3.770m, "g", 8086.68m, 30486.78m, "22kt"),
                        Line("22kt Mangalsutra", 15.120m, "g", 8284.48m, 125261.34m, "22kt"),
                        Line("Diamond Solitaire Ring", 0.500m, "ct", 77000m, 38500m, null),
                    ],
                    new BillTotalsDto(286122.02m, 5000m, 8583.66m, 0.32m, 289706m),
                    null,
                    new CompanyProfile("Sample Jewellers", null, null, "Tamil Nadu", "India", string.Empty, null, null, null, null, null, null));
                shell.PrintPreview.Initialize(content);
                foreach (var bytes in new BillPrintRenderer().GeneratePageImages(PrintDocumentOptions.ForInvoice(content, [CopyLabel.Original]), 110))
                {
                    shell.PrintPreview.PreviewPages.Add(PreviewBitmapDecoder.Decode(bytes));
                }

                shell.PrintPreview.Printers.Add("Microsoft Print to PDF");
                shell.PrintPreview.SelectedPrinter = "Microsoft Print to PDF";
                shell.PrintPreview.PreviewZoomPercent = 80;
                return new PrintPreviewDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-BillDetails", shell =>
            {
                var created = LocalAt(11, 15, 12);
                static BillLineItemDto Line(string item, decimal net, string unit, decimal rate, decimal total, string? karat)
                    => new(item, null, net, unit, rate, total, karat, null);

                var payload = new BillPayloadDto(
                    "Meera Subramaniam", null, null, null, new DateOnly(2026, 10, 11),
                    [
                        Line("22kt Gold Chain (Rope, 18″)", 12.450m, "gm", 8068.88m, 100457.56m, "22kt"),
                        Line("22kt Lakshmi Pendant", 3.770m, "gm", 8086.68m, 30486.78m, "22kt"),
                        Line("22kt Mangalsutra", 15.120m, "gm", 8284.48m, 125261.34m, "22kt"),
                        Line("Diamond Solitaire Ring", 0.500m, "ct", 77000m, 38500m, null),
                    ],
                    new BillTotalsDto(286122.02m, 5000m, 8583.66m, 0.32m, 289706m),
                    null,
                    "Cash",
                    7780m);
                shell.BillDetails.Bill = new BillResponse(
                    Guid.NewGuid(), BillStates.Draft, "SR/2026-27/0009", "2026-27", null, Guid.NewGuid(),
                    created, created,
                    new BillRevisionResponse(Guid.NewGuid(), 1, created, null, payload));
                shell.BillDetails.AuditTrail.Add(new BillAuditTrailItemViewModel(Guid.NewGuid(), created, "Bill created", "Saved as a draft with 4 lines", "info", null!));
                shell.BillDetails.AuditTrail.Add(new BillAuditTrailItemViewModel(Guid.NewGuid(), LocalAt(11, 15, 18), "Saved again", "Discount of ₹5,000 added", "info", null!));
                return new BillDetailsDialog();
            });

            Capture(found, bindingErrors, "Design-Dialog-SystemHealth", shell =>
            {
                shell.Health.CloudDot = "ok";
                shell.Health.CloudTooltip = "Reachable. Server 192.168.1.20:5108, answered in 42 ms.";
                shell.Health.DatabaseDot = "ok";
                shell.Health.DatabaseTooltip = "The database marker matches this server. No migrations are waiting.";
                shell.Health.TallyDot = "neutral";
                shell.Health.TallyTooltip = "Tally posting is manual. Press Push on a bill to check that Tally is reachable.";
                shell.LastHealthCheckedAtUtc = new DateTimeOffset(2026, 10, 11, 17, 57, 19, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 11))).ToUniversalTime();
                shell.MastersRefreshMessage = "Refreshed ledgers (142), voucher types (12), stock items (5), companies (3).";
                shell.LastHealthSnapshot = new SystemHealthSnapshot(
                    true,
                    new MasterFreshnessSummaryResponse(
                        "fresh",
                        [
                            new MasterSnapshotMetadata(MasterTypes.Ledgers, LocalAt(11, 15, 10, 4), 142, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.VoucherTypes, LocalAt(11, 15, 10, 4), 12, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.StockItems, LocalAt(11, 15, 10, 5), 5, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.Companies, LocalAt(11, 15, 10, 5), 3, "fresh"),
                        ]),
                    null);
                return new SystemHealthDialog();
            });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    /// <summary>The batch scheduler filled in as an operator would: a total, a rate, the hours of one day, shop hours and three karats.</summary>
    private static SyntheticBatchViewModel FillBatch(SyntheticBatchViewModel batch)
    {
        var day = new DateTime(2026, 10, 11);
        var offset = TimeZoneInfo.Local.GetUtcOffset(day);
        foreach (var (label, item) in new[] { ("24kt", "Gold 24kt"), ("22kt", "Gold 22kt"), ("18kt", "Gold 18kt") })
        {
            batch.KaratOptions.Add(new SelectableKarat { Label = label, TallyItem = item, IsSelected = true });
        }

        batch.Rate24Kt = 12_000m;
        batch.EarliestAt = new DateTimeOffset(day.AddHours(10).AddMinutes(43), offset);
        batch.StartAt = new DateTimeOffset(day.AddHours(10).AddMinutes(43), offset);
        batch.EndAt = new DateTimeOffset(day.AddHours(16).AddMinutes(45), offset);
        batch.Hours = new BatchHoursSettings(new TimeOnly(10, 0), new TimeOnly(20, 0), DayOfWeek.Monday, 5);

        batch.Asked =
        [
            new ConfirmFact("Total", "₹10,00,000"),
            new ConfirmFact("Largest single bill", "₹1,99,000"),
            new ConfirmFact("24K rate", "₹12,000 per gram"),
            new ConfirmFact("Payment", "Cash"),
            new ConfirmFact("Items per bill", "1 to 3"),
            new ConfirmFact("Dated", "11 Oct, 10:43 to 16:45"),
            new ConfirmFact("Shop hours", "10:00 to 20:00"),
            new ConfirmFact("Closed on", "Monday"),
            new ConfirmFact("Bills at least", "5 minutes apart"),
            new ConfirmFact("Karats", "24kt, 22kt, 18kt"),
        ];
        return batch;
    }

    private static void Capture(
        List<string> problems,
        BindingErrors bindingErrors,
        string name,
        Func<MainWindowViewModel, FrameworkElement> dialog)
    {
        try
        {
            var shell = DesignSample.CreateShell();
            DesignSample.MakeHealthy(shell);
            var view = dialog(shell);
            var root = new Grid { DataContext = shell, Width = CanvasSize.Width, Height = CanvasSize.Height };
            root.Children.Add(view);
            var window = new Window { Content = root, DataContext = shell };
            for (var pass = 0; pass < 3; pass++)
            {
                root.Measure(CanvasSize);
                root.Arrange(new Rect(CanvasSize));
                root.UpdateLayout();
                WpfTestHost.DrainDispatcher();
            }

            ViewSnapshot.Save(root, name);
            window.Content = null;
        }
        catch (Exception ex)
        {
            problems.Add($"{name}: {ex.GetBaseException().Message}");
        }

        problems.AddRange(bindingErrors.Take().Select(error => $"{name}: {error}"));
    }
}
