using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.ViewModels.Settings;

public partial class SettingsDraft : ObservableObject
{
    [ObservableProperty] private string host = string.Empty;
    [ObservableProperty] private string port = string.Empty;
    [ObservableProperty] private string timeoutSeconds = string.Empty;

    [ObservableProperty] private string printCompanyName = string.Empty;
    [ObservableProperty] private string companyGstin = string.Empty;
    [ObservableProperty] private string companyPhone = string.Empty;
    [ObservableProperty] private string companyAddress = string.Empty;
    [ObservableProperty] private string companyState = string.Empty;
    [ObservableProperty] private string companyCountry = string.Empty;

    [ObservableProperty] private string bankName = string.Empty;
    [ObservableProperty] private string bankAccount = string.Empty;
    [ObservableProperty] private string bankIfsc = string.Empty;
    [ObservableProperty] private string bankUpi = string.Empty;

    [ObservableProperty] private string termsAndConditions = string.Empty;

    [ObservableProperty] private string invoicePrefix = string.Empty;
    [ObservableProperty] private string invoiceSuffix = string.Empty;
    [ObservableProperty] private string invoicePadding = "4";

    [ObservableProperty] private bool copyOriginalDefault;
    [ObservableProperty] private bool copyDuplicateDefault;
    [ObservableProperty] private bool copyTriplicateDefault;

    [ObservableProperty] private string printFontSize = string.Empty;
    [ObservableProperty] private string printTermsFontSize = string.Empty;

    [ObservableProperty] private string salesLedger = string.Empty;
    [ObservableProperty] private string cashLedger = string.Empty;
    [ObservableProperty] private string creditDebitLedger = string.Empty;
    [ObservableProperty] private string cgstLedger = string.Empty;
    [ObservableProperty] private string sgstLedger = string.Empty;
    [ObservableProperty] private string roundOffLedger = string.Empty;
    [ObservableProperty] private string discountLedger = string.Empty;
    [ObservableProperty] private string salesVoucherType = string.Empty;

    public ObservableCollection<ItemMasterRowVm> ItemMasterRows { get; } = new();
    public ObservableCollection<KaratMasterRowVm> KaratRows { get; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = null,
        WriteIndented = false,
    };

    public SettingsDraft()
    {
        ItemMasterRows.CollectionChanged += OnItemMasterRowsChanged;
        KaratRows.CollectionChanged += OnKaratRowsChanged;
    }

    public void LoadFrom(EffectiveCloudSettingsDto dto)
    {
        Host = dto.Connection.Host;
        Port = dto.Connection.Port.ToString(CultureInfo.InvariantCulture);
        TimeoutSeconds = dto.Connection.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);

        PrintCompanyName = dto.Print.CompanyName;
        CompanyGstin = dto.Print.CompanyGstin ?? string.Empty;
        CompanyPhone = dto.Print.CompanyPhone ?? string.Empty;
        CompanyAddress = dto.Print.CompanyAddress ?? string.Empty;
        CompanyState = dto.Print.CompanyState ?? string.Empty;
        CompanyCountry = dto.Print.CompanyCountry ?? string.Empty;

        BankName = dto.Print.BankName ?? string.Empty;
        BankAccount = dto.Print.BankAccount ?? string.Empty;
        BankIfsc = dto.Print.BankIfsc ?? string.Empty;
        BankUpi = dto.Print.BankUpi ?? string.Empty;

        TermsAndConditions = dto.Print.TermsAndConditions ?? string.Empty;

        InvoicePrefix = dto.Numbering.InvoicePrefix;
        InvoiceSuffix = dto.Numbering.InvoiceSuffix;
        InvoicePadding = dto.Numbering.InvoicePadding.ToString(CultureInfo.InvariantCulture);

        CopyOriginalDefault = dto.Print.CopyOriginalDefault;
        CopyDuplicateDefault = dto.Print.CopyDuplicateDefault;
        CopyTriplicateDefault = dto.Print.CopyTriplicateDefault;

        PrintFontSize = dto.Print.PrintFontSize.ToString(CultureInfo.InvariantCulture);
        PrintTermsFontSize = dto.Print.PrintTermsFontSize.ToString(CultureInfo.InvariantCulture);

        SalesLedger = dto.Ledgers.SalesLedger;
        CashLedger = dto.Ledgers.CashLedger;
        CreditDebitLedger = dto.Ledgers.CreditDebitLedger;
        CgstLedger = dto.Ledgers.CgstLedger;
        SgstLedger = dto.Ledgers.SgstLedger;
        RoundOffLedger = dto.Ledgers.RoundOffLedger;
        DiscountLedger = dto.Ledgers.DiscountLedger;
        SalesVoucherType = dto.Ledgers.SalesVoucherType;

        ReplaceRows(ItemMasterRows, ParseItemMasterJson(dto.Masters.ItemMasterDataJson).Select(ItemMasterRowVm.FromEntry));
        ReplaceRows(KaratRows, ParseKaratJson(dto.Masters.KaratMappingDataJson).Select(KaratMasterRowVm.FromEntry));
    }

    /// <summary>
    /// Tolerant snapshot of just the print-affecting fields, safe to call during
    /// editing. Blank strings pass through as null; unparseable font sizes fall back
    /// to V1 defaults. Never throws. Use <see cref="TryBuildDto"/> for Save where
    /// strict validation is required.
    /// </summary>
    public PrintSettingsDto BuildPrintSettingsSnapshot()
    {
        var printFont = TryParseFont(PrintFontSize, PrintLayoutLimits.DefaultInvoiceFontSize);
        var termsFont = TryParseFont(PrintTermsFontSize, PrintLayoutLimits.DefaultTermsFontSize);

        return new PrintSettingsDto(
            CompanyName: string.IsNullOrWhiteSpace(PrintCompanyName) ? string.Empty : PrintCompanyName.Trim(),
            CompanyGstin: NullIfBlank(CompanyGstin),
            CompanyPhone: NullIfBlank(CompanyPhone),
            CompanyAddress: NullIfBlank(CompanyAddress),
            CompanyState: NullIfBlank(CompanyState),
            CompanyCountry: NullIfBlank(CompanyCountry),
            BankName: NullIfBlank(BankName),
            BankAccount: NullIfBlank(BankAccount),
            BankIfsc: NullIfBlank(BankIfsc),
            BankUpi: NullIfBlank(BankUpi),
            TermsAndConditions: NullIfBlank(TermsAndConditions),
            CopyOriginalDefault: CopyOriginalDefault,
            CopyDuplicateDefault: CopyDuplicateDefault,
            CopyTriplicateDefault: CopyTriplicateDefault,
            PrintFontSize: printFont,
            PrintTermsFontSize: termsFont);
    }

    private static int TryParseFont(string value, int fallback)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : fallback;

    public bool TryBuildDto(EffectiveCloudSettingsDto baseline, out EffectiveCloudSettingsDto dto, out string error)
    {
        dto = baseline;

        if (string.IsNullOrWhiteSpace(Host)) { error = "Tally host is required."; return false; }
        if (!int.TryParse(Port, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        { error = "Port must be an integer between 1 and 65535."; return false; }
        if (!int.TryParse(TimeoutSeconds, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timeout) || timeout < 1)
        { error = "Timeout must be a positive integer."; return false; }

        if (string.IsNullOrWhiteSpace(PrintCompanyName)) { error = "Print company name is required."; return false; }
        if (!int.TryParse(PrintFontSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var printFont) || printFont < 1)
        { error = "Print font size must be a positive integer."; return false; }
        if (!int.TryParse(PrintTermsFontSize, NumberStyles.Integer, CultureInfo.InvariantCulture, out var termsFont) || termsFont < 1)
        { error = "Terms font size must be a positive integer."; return false; }

        if (!int.TryParse(InvoicePadding, NumberStyles.Integer, CultureInfo.InvariantCulture, out var invoicePadding) || invoicePadding < 1 || invoicePadding > 10)
        { error = "Invoice number padding must be between 1 and 10."; return false; }

        if (string.IsNullOrWhiteSpace(SalesLedger)
            || string.IsNullOrWhiteSpace(CashLedger)
            || string.IsNullOrWhiteSpace(CreditDebitLedger)
            || string.IsNullOrWhiteSpace(CgstLedger)
            || string.IsNullOrWhiteSpace(SgstLedger)
            || string.IsNullOrWhiteSpace(RoundOffLedger)
            || string.IsNullOrWhiteSpace(DiscountLedger)
            || string.IsNullOrWhiteSpace(SalesVoucherType))
        { error = "All ledger mappings and the sales voucher type are required."; return false; }

        var itemEntries = new List<ItemMasterEntry>(ItemMasterRows.Count);
        foreach (var row in ItemMasterRows)
        {
            if (!row.TryBuildEntry(out var entry, out var rowError)) { error = rowError; return false; }
            itemEntries.Add(entry);
        }

        var karatEntries = new List<KaratMasterEntry>(KaratRows.Count);
        foreach (var row in KaratRows)
        {
            if (!row.TryBuildEntry(out var entry, out var rowError)) { error = rowError; return false; }
            karatEntries.Add(entry);
        }

        dto = new EffectiveCloudSettingsDto(
            Connection: new ConnectionSettingsDto(
                Host: Host.Trim(),
                Port: port,
                TimeoutSeconds: timeout,
                ActiveCompanyName: baseline.Connection.ActiveCompanyName),
            Numbering: new NumberingSettingsDto(
                InvoicePrefix: InvoicePrefix.Trim(),
                InvoiceSuffix: InvoiceSuffix.Trim(),
                InvoicePadding: invoicePadding),
            Print: new PrintSettingsDto(
                CompanyName: PrintCompanyName.Trim(),
                CompanyGstin: NullIfBlank(CompanyGstin),
                CompanyPhone: NullIfBlank(CompanyPhone),
                CompanyAddress: NullIfBlank(CompanyAddress),
                CompanyState: NullIfBlank(CompanyState),
                CompanyCountry: NullIfBlank(CompanyCountry),
                BankName: NullIfBlank(BankName),
                BankAccount: NullIfBlank(BankAccount),
                BankIfsc: NullIfBlank(BankIfsc),
                BankUpi: NullIfBlank(BankUpi),
                TermsAndConditions: NullIfBlank(TermsAndConditions),
                CopyOriginalDefault: CopyOriginalDefault,
                CopyDuplicateDefault: CopyDuplicateDefault,
                CopyTriplicateDefault: CopyTriplicateDefault,
                PrintFontSize: printFont,
                PrintTermsFontSize: termsFont),
            Ledgers: new LedgerMappingsDto(
                SalesLedger: SalesLedger.Trim(),
                CashLedger: CashLedger.Trim(),
                CreditDebitLedger: CreditDebitLedger.Trim(),
                CgstLedger: CgstLedger.Trim(),
                SgstLedger: SgstLedger.Trim(),
                RoundOffLedger: RoundOffLedger.Trim(),
                DiscountLedger: DiscountLedger.Trim(),
                SalesVoucherType: SalesVoucherType.Trim()),
            Masters: new MasterDataSettingsDto(
                ItemMasterDataJson: JsonSerializer.Serialize(itemEntries, JsonOptions),
                KaratMappingDataJson: JsonSerializer.Serialize(karatEntries, JsonOptions)));

        error = string.Empty;
        return true;
    }

    private static IReadOnlyList<ItemMasterEntry> ParseItemMasterJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ItemMasterEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<ItemMasterEntry>>(json, JsonOptions) ?? new List<ItemMasterEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<ItemMasterEntry>();
        }
    }

    private static IReadOnlyList<KaratMasterEntry> ParseKaratJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<KaratMasterEntry>();
        try
        {
            return JsonSerializer.Deserialize<List<KaratMasterEntry>>(json, JsonOptions) ?? new List<KaratMasterEntry>();
        }
        catch (JsonException)
        {
            return Array.Empty<KaratMasterEntry>();
        }
    }

    private void ReplaceRows<T>(ObservableCollection<T> target, IEnumerable<T> rows) where T : INotifyPropertyChanged
    {
        foreach (var row in target) row.PropertyChanged -= OnRowPropertyChanged;
        target.Clear();
        foreach (var row in rows)
        {
            row.PropertyChanged += OnRowPropertyChanged;
            target.Add(row);
        }
    }

    private void OnItemMasterRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => HandleRowsChanged(e);

    private void OnKaratRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => HandleRowsChanged(e);

    private void HandleRowsChanged(NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnRowPropertyChanged;
        if (e.OldItems is not null)
            foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnRowPropertyChanged;
        OnPropertyChanged(nameof(ItemMasterRows));
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(ItemMasterRows));

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
