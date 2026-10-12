using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.Options;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Configuration;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels;
using ShowroomBilling.Desktop.ViewModels.Admin;
using ShowroomBilling.Desktop.ViewModels.Bills;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Printing;
using ShowroomBilling.Desktop.ViewModels.Settings;
using ShowroomBilling.Desktop.ViewModels.Setup;
using ShowroomBilling.Desktop.ViewModels.SyntheticBatch;

namespace ShowroomBilling.Desktop.Tests.Views;

/// <summary>
/// The sample bill the redesign mockups in <c>docs/design/redesign</c> show, and a shell around it,
/// so a view can be rendered with the same numbers as its picture and compared by eye.
/// </summary>
internal static class DesignSample
{
    public static readonly Size WindowSize = new(1280, 800);

    private static DateTimeOffset LocalAt(int day, int hour, int minute)
        => new(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, day, hour, minute, 0)));

    /// <summary>The settings of the sample showroom: its company, ledgers, ten items and three karats, loaded as the server would send them.</summary>
    public static SettingsViewModel CreateSettings()
    {
        var settings = new SettingsViewModel(bootstrapOptions: new DesktopBootstrapOptions { ApiBaseUrl = "http://192.168.1.20:5108" });

        ItemMasterEntry Item(string name, decimal wastage, decimal labour, string mapping, string mode = PricingModes.Both, string category = ItemCategories.GoldBased, string unit = ItemUnits.Gram)
            => new(name, unit, wastage, category, mode, labour, mapping);

        var items = new[]
        {
            Item("22kt Gold Chain (Rope, 18″)", 8, 320, "Gold Chain 22kt"),
            Item("22kt Bangle Set (pair)", 10, 450, "Bangles 22kt"),
            Item("22kt Lakshmi Pendant", 9, 260, "Pendant 22kt"),
            Item("22kt Ladies Ring", 12, 280, "Ring 22kt"),
            Item("22kt Kada (Mens)", 6, 550, "Kada 22kt"),
            Item("22kt Mangalsutra", 10, 380, "Mangalsutra 22kt"),
            Item("18kt Rose Gold Chain", 8, 300, "Chain 18kt"),
            Item("18kt Stud Earrings", 12, 260, "Earrings 18kt"),
            Item("24kt Gold Coin 8g", 0, 120, "Coin 24kt", PricingModes.Labour),
            Item("Diamond Solitaire Ring", 0, 0, "Diamond Ring", PricingModes.Wastage, ItemCategories.Diamond, ItemUnits.Carat),
        };
        var karats = new[]
        {
            new KaratMasterEntry("24kt", 100m, "Gold 24kt"),
            new KaratMasterEntry("22kt", 91.6m, "Gold 22kt"),
            new KaratMasterEntry("18kt", 75m, "Gold 18kt"),
        };

        var dto = new EffectiveCloudSettingsDto(
            new ConnectionSettingsDto("localhost", 9000, 30, "Sample Jewellers"),
            new NumberingSettingsDto("SR/", string.Empty, 4),
            new PrintSettingsDto(
                "Sample Jewellers", null, null, null, "Tamil Nadu", "India",
                null, null, null, null, null,
                true, false, false, 10, 8),
            new LedgerMappingsDto(
                "Jewellery Sales", "Cash", "Credit and Debit", "Output CGST", "Output SGST",
                "Round Off", "Discount Allowed", "Sales"),
            new MasterDataSettingsDto(
                JsonSerializer.Serialize(items),
                JsonSerializer.Serialize(karats)));

        settings.ApplyEffectiveSettings(new EffectiveSettingsResponse(
            "cloud",
            "Settings loaded from the server",
            dto,
            ["Company on the invoice", "Bank details", "Invoice numbering", "Print copies and layout", "Ledger mappings", "Item and karat masters", "Tally connection and active company"],
            ["Server address", "Display density", "Print preferences and zoom", "Window and column sizes"],
            LocalAt(11, 15, 12)));

        var master = settings.MasterData;
        master.Companies.Add(new CompanySnapshotItem("Sample Jewellers", true));
        master.Companies.Add(new CompanySnapshotItem("Sample Jewellers (old year)", false));
        master.Companies.Add(new CompanySnapshotItem("Demo Company", false));
        master.CompaniesCount = 3;
        master.CompaniesFreshness = "fresh";
        master.CompaniesFetchedAtUtc = LocalAt(11, 15, 10);
        master.SelectedCompany = master.Companies[0];

        foreach (var ledger in new[]
        {
            "Jewellery Sales", "Cash", "Credit and Debit", "Output CGST", "Output SGST", "Round Off", "Vijaya Stores", "Walk-in Customer",
        })
        {
            master.LedgerOptions.Add(new LedgerSnapshotItem(ledger, null, null, false, null));
        }

        master.VoucherTypeOptions.Add(new VoucherTypeSnapshotItem("Sales", null, true));
        master.LedgersCount = 142;
        master.VoucherTypesCount = 12;
        master.LedgersFreshness = "fresh";
        master.LedgersFetchedAtUtc = LocalAt(11, 15, 10);

        foreach (var stock in new[] { "Gold Chain 22kt", "Bangles 22kt", "Pendant 22kt", "Ring 22kt", "Kada 22kt", "Mangalsutra 22kt", "Gold 24kt", "Gold 22kt", "Gold 18kt", "Coin 24kt", "Diamond Ring" })
        {
            master.StockItems.Add(new StockItemSnapshotItem(stock, null, "gm", "7113", null));
        }

        master.StockItemsCount = master.StockItems.Count;
        master.StockItemsFreshness = "fresh";
        master.StockItemsFetchedAtUtc = LocalAt(11, 15, 10);

        settings.Server.Status = "The server answered in 42 ms. Its database is connected and it has the latest migrations.";
        return settings;
    }

    /// <summary>The estimate of Main.png: three gold lines, one diamond line, a discount of 5,000.</summary>
    public static InvoiceViewModel CreateFilledInvoice(SettingsViewModel settings)
    {
        var invoice = new InvoiceViewModel(null, null, settings);
        invoice.BillDate = new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.FromHours(5.5));
        invoice.PartyName = "Meera Subramaniam";
        invoice.Rate24Kt = 7780m;

        void Gold(string item, decimal gross, decimal less)
        {
            var line = invoice.Lines[^1];
            line.ItemMaster = settings.Draft.ItemMasterRows.First(master => master.Name == item);
            line.KaratMaster = settings.Draft.KaratRows.First(karat => karat.Label == "22kt");
            line.GrossWeight = gross;
            line.LessWeight = less;
        }

        Gold("22kt Gold Chain (Rope, 18″)", 12.450m, 0m);
        Gold("22kt Lakshmi Pendant", 4.120m, 0.350m);
        Gold("22kt Mangalsutra", 16.240m, 1.120m);

        var diamond = invoice.Lines[^1];
        diamond.ItemMaster = settings.Draft.ItemMasterRows.First(master => master.Name == "Diamond Solitaire Ring");
        diamond.GrossWeight = 0.500m;
        diamond.DiamondRate = 77000m;

        invoice.Discount = 5000m;
        invoice.FocusedRowIndex = 1;
        return invoice;
    }

    public static MainWindowViewModel CreateShell(
        InvoiceViewModel? invoice = null,
        SettingsViewModel? settings = null,
        AdminTokenStore? tokenStore = null)
    {
        settings ??= CreateSettings();
        invoice ??= CreateFilledInvoice(settings);

        tokenStore ??= new AdminTokenStore();
        var bills = new BillsViewModel(null, tokenStore) { HideFullyPostedDays = false };
        // The nine bills of Bills.png: every state once, three days.
        static DateTimeOffset At(int day, int hour, int minute)
            => new(new DateTime(2026, 10, day, hour, minute, 0, DateTimeKind.Unspecified), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, day, hour, minute, 0)));

        void Bill(string state, int number, string party, decimal amount, int day, int hour, int minute, bool edited = false)
            => bills.Items.Add(new BillListRowViewModel(new BillSummaryItem(
                Guid.NewGuid(),
                state,
                $"SR/2026-27/{number:0000}",
                party,
                new DateOnly(2026, 10, day),
                amount,
                At(day, hour, minute),
                At(day, hour, minute),
                edited)));

        Bill(BillStates.Posting, 11, "Kavita Iyer", 84250m, 11, 17, 52);
        Bill(BillStates.Pending, 10, "Anand Pillai", 131592m, 11, 16, 40);
        Bill(BillStates.Draft, 9, "Meera Subramaniam", 289706m, 11, 15, 12);
        Bill(BillStates.Posted, 8, "Walk-in Customer", 6464m, 10, 19, 5);
        Bill(BillStates.Failed, 7, "Vijaya Stores", 27500m, 10, 18, 31);
        Bill(BillStates.ReconciliationRequired, 6, "Karthik Ramanathan", 59892m, 10, 15, 48);
        Bill(BillStates.Posted, 5, "Priya Venkatesh", 73500m, 10, 14, 20, edited: true);
        Bill(BillStates.Voided, 4, "Walk-in Customer", 8000m, 9, 20, 14);
        Bill(BillStates.Posted, 3, "Walk-in Customer", 47988m, 9, 11, 2);

        bills.Total = bills.Items.Count;
        bills.Items[1].IsSelected = true;

        // The constructors only store their clients; no view-model method that
        // would call one is invoked here.
        var shell = new MainWindowViewModel(
            settingsApiClient: null!,
            healthApiClient: null!,
            mastersApiClient: null!,
            billsApiClient: null!,
            bootstrapOptions: Options.Create(new DesktopBootstrapOptions()),
            companyProfileProvider: null!,
            printLayoutOptionsProvider: null!,
            printDispatcher: null!,
            printPreferences: null!,
            adminTokenStore: tokenStore,
            invoice: invoice,
            bills: bills,
            billDetails: new BillDetailsViewModel(),
            printPreview: new PrintPreviewViewModel(),
            settings: settings,
            admin: new AdminUnlockViewModel(adminApi: null!, tokenStore),
            setupWizard: new SetupWizardViewModel(
                settingsApi: null!,
                completionStore: null!),
            syntheticBatch: new SyntheticBatchViewModel());

        // Left open so the confirm overlay has its texts; nothing awaits the answer.
        _ = shell.ConfirmDialog.ShowAsync(BillConfirmations.Repost("SR/2026-27/0001", tallyMayHoldVoucher: true));
        return shell;
    }

    /// <summary>An admin session that has just been unlocked, for the screens that show admin-only controls.</summary>
    public static AdminTokenStore UnlockedTokens()
    {
        var tokens = new AdminTokenStore();
        tokens.Set(new ShowroomBilling.Contracts.Admin.AdminUnlockResponse(
            "sample-token", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(30), "admin/kk"));
        return tokens;
    }

    /// <summary>The healthy-system look of the top bar: cloud and database green, Tally not yet checked.</summary>
    public static void MakeHealthy(MainWindowViewModel shell)
    {
        shell.SystemState = ShowroomBilling.Desktop.Shell.SystemState.Healthy;
        shell.Health.CloudDot = "ok";
        shell.Health.DatabaseDot = "ok";
        shell.Health.DatabaseLabel = "DB PROD";
        shell.Health.TallyDot = "neutral";
        shell.TitleBar.Company = "Sample Jewellers";
        shell.StatusBar.Time = "17:57";
        shell.StatusBar.Workstation = "Workstation KK";
    }

    /// <summary>Lays <paramref name="screen"/> out between the top and bottom bars, as MainWindow does.</summary>
    public static FrameworkElement ComposeShell(MainWindowViewModel shell, FrameworkElement screen)
    {
        var root = new Grid { DataContext = shell, Width = WindowSize.Width, Height = WindowSize.Height };
        root.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.FindResource("BgBrush");
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = new ShowroomBilling.Desktop.Views.TopBarView();
        Grid.SetRow(top, 0);
        root.Children.Add(top);

        var strip = new ShowroomBilling.Desktop.Views.BannerView();
        Grid.SetRow(strip, 1);
        root.Children.Add(strip);

        Grid.SetRow(screen, 2);
        root.Children.Add(screen);

        var bottom = new ShowroomBilling.Desktop.Views.BottomBarView();
        Grid.SetRow(bottom, 3);
        root.Children.Add(bottom);
        return root;
    }
}
