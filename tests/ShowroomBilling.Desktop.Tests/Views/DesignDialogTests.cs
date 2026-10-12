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

            Capture(found, bindingErrors, "Design-Dialog-SyntheticBatch", shell =>
            {
                var batch = shell.SyntheticBatch;
                batch.StartAt = new DateTimeOffset(new DateTime(2026, 10, 11, 0, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 11)));
                batch.EndAt = new DateTimeOffset(new DateTime(2026, 10, 11, 23, 59, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 11)));
                foreach (var (label, item) in new[] { ("24kt", "Gold 24kt"), ("22kt", "Gold 22kt"), ("18kt", "Gold 18kt") })
                {
                    batch.KaratOptions.Add(new SelectableKarat { Label = label, TallyItem = item, IsSelected = true });
                }

                return new SyntheticBatchDialog();
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
                    Guid.NewGuid(), Guid.NewGuid(), null, "sale", BillStates.Draft, "SR/2026-27/0009", "2026-27", null, Guid.NewGuid(),
                    created, created, null,
                    new BillRevisionResponse(Guid.NewGuid(), 1, created, null, null, null, payload));
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
                            new MasterSnapshotMetadata(MasterTypes.Ledgers, null, LocalAt(11, 15, 10, 4), 142, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.VoucherTypes, null, LocalAt(11, 15, 10, 4), 12, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.StockItems, null, LocalAt(11, 15, 10, 5), 5, "fresh"),
                            new MasterSnapshotMetadata(MasterTypes.Companies, null, LocalAt(11, 15, 10, 5), 3, "fresh"),
                        ]),
                    null);
                return new SystemHealthDialog();
            });

            return found;
        });

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
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
