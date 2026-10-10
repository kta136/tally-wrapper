using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Auditing;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Settings;

public sealed class CloudSettingsService(
    ShowroomBillingDbContext dbContext,
    IAuditActorContext? actorContext = null,
    EffectiveSettingsCache? cache = null) : ICloudSettingsService
{
    public async Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (cache is not null && cache.TryGet(out var cached))
        {
            return cached;
        }

        var entity = await GetOrCreateAsync(cancellationToken);
        var response = MapResponse(entity);
        cache?.Set(response);
        return response;
    }

    public async Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CloudSettingsValidator.Validate(request);

        var entity = await GetOrCreateAsync(cancellationToken);

        entity.Host = request.Settings.Connection.Host.Trim();
        entity.Port = request.Settings.Connection.Port;
        entity.TimeoutSeconds = request.Settings.Connection.TimeoutSeconds;
        entity.ActiveCompanyName = request.Settings.Connection.ActiveCompanyName.Trim();
        entity.PrintCompanyName = request.Settings.Print.CompanyName.Trim();
        entity.CompanyGstin = Normalize(request.Settings.Print.CompanyGstin);
        entity.CompanyPhone = Normalize(request.Settings.Print.CompanyPhone);
        entity.CompanyAddress = Normalize(request.Settings.Print.CompanyAddress);
        entity.CompanyState = Normalize(request.Settings.Print.CompanyState);
        entity.CompanyCountry = Normalize(request.Settings.Print.CompanyCountry);
        entity.BankName = Normalize(request.Settings.Print.BankName);
        entity.BankAccount = Normalize(request.Settings.Print.BankAccount);
        entity.BankIfsc = Normalize(request.Settings.Print.BankIfsc);
        entity.BankUpi = Normalize(request.Settings.Print.BankUpi);
        entity.TermsAndConditions = Normalize(request.Settings.Print.TermsAndConditions);
        entity.InvoicePrefix = request.Settings.Numbering.InvoicePrefix.Trim();
        entity.InvoiceSuffix = request.Settings.Numbering.InvoiceSuffix.Trim();
        entity.InvoicePadding = Math.Clamp(request.Settings.Numbering.InvoicePadding, 1, 10);
        entity.CopyOriginalDefault = request.Settings.Print.CopyOriginalDefault;
        entity.CopyDuplicateDefault = request.Settings.Print.CopyDuplicateDefault;
        entity.CopyTriplicateDefault = request.Settings.Print.CopyTriplicateDefault;
        entity.PrintFontSize = request.Settings.Print.PrintFontSize;
        entity.PrintTermsFontSize = request.Settings.Print.PrintTermsFontSize;
        entity.SalesLedger = request.Settings.Ledgers.SalesLedger.Trim();
        entity.CashLedger = request.Settings.Ledgers.CashLedger.Trim();
        entity.CreditDebitLedger = request.Settings.Ledgers.CreditDebitLedger.Trim();
        entity.CgstLedger = request.Settings.Ledgers.CgstLedger.Trim();
        entity.SgstLedger = request.Settings.Ledgers.SgstLedger.Trim();
        entity.RoundOffLedger = request.Settings.Ledgers.RoundOffLedger.Trim();
        entity.DiscountLedger = request.Settings.Ledgers.DiscountLedger.Trim();
        entity.SalesVoucherType = request.Settings.Ledgers.SalesVoucherType.Trim();
        entity.ItemMasterDataJson = NormalizeJson(request.Settings.Masters.ItemMasterDataJson);
        entity.KaratMappingDataJson = NormalizeJson(request.Settings.Masters.KaratMappingDataJson);
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        WriteAuditEvent("settings.updated", entity.Id.ToString());
        await dbContext.SaveChangesAsync(cancellationToken);
        cache?.Clear();

        return new SettingsUpdateResponse(
            SettingsSource: "cloud",
            Summary: "Shared settings were saved to PostgreSQL. Desktop should reload effective settings from the API.",
            SavedSections:
            [
                "connection",
                "numbering",
                "print",
                "ledgers",
                "masters"
            ],
            UpdatedAtUtc: entity.UpdatedAtUtc);
    }

    public async Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        string companyName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(companyName);
        CloudSettingsValidator.ValidateCompanyName(companyName);

        var entity = await GetOrCreateAsync(cancellationToken);
        entity.ActiveCompanyName = companyName.Trim();
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        WriteAuditEvent("settings.company_selected", entity.Id.ToString());
        await dbContext.SaveChangesAsync(cancellationToken);
        cache?.Clear();

        return new SettingsUpdateResponse(
            SettingsSource: "cloud",
            Summary: $"Active company updated to '{entity.ActiveCompanyName}'.",
            SavedSections: ["connection"],
            UpdatedAtUtc: entity.UpdatedAtUtc);
    }

    public async Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
    {
        var entity = await GetOrCreateAsync(cancellationToken);
        var layout = DeserializePrintLayout(entity.PrintLayoutJson);
        return new PrintLayoutResponse(layout, entity.UpdatedAtUtc);
    }

    public async Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        CloudSettingsValidator.ValidatePrintLayout(request);

        var entity = await GetOrCreateAsync(cancellationToken);
        entity.PrintLayoutJson = JsonSerializer.Serialize(request.Layout, PrintLayoutJsonOptions);
        entity.UpdatedAtUtc = DateTimeOffset.UtcNow;

        WriteAuditEvent("settings.print_layout_updated", entity.Id.ToString());
        await dbContext.SaveChangesAsync(cancellationToken);
        cache?.Clear();

        return new PrintLayoutResponse(request.Layout, entity.UpdatedAtUtc);
    }

    private static readonly PrintLayoutSettings DefaultPrintLayout = new(
        LeftMarginCm: PrintLayoutDefaults.MarginCm,
        RightMarginCm: PrintLayoutDefaults.MarginCm,
        TopMarginCm: PrintLayoutDefaults.MarginCm,
        BottomMarginCm: PrintLayoutDefaults.MarginCm,
        Logo: null,
        Signature: null,
        Watermark: null,
        PageLayout: PrintLayoutDefaults.CreatePageLayout());

    private static readonly JsonSerializerOptions PrintLayoutJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static PrintLayoutSettings DeserializePrintLayout(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return DefaultPrintLayout;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<PrintLayoutSettings>(json, PrintLayoutJsonOptions);
            return parsed is null
                ? DefaultPrintLayout
                : parsed with { PageLayout = parsed.PageLayout ?? PrintLayoutDefaults.CreatePageLayout() };
        }
        catch (JsonException)
        {
            return DefaultPrintLayout;
        }
    }

    private async Task<CloudSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var entity = await dbContext.CloudSettings
            .OrderByDescending(x => x.UpdatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (entity is not null)
        {
            return entity;
        }

        entity = CloudSettingsDefaults.Create();
        dbContext.CloudSettings.Add(entity);
        WriteAuditEvent("settings.seeded", entity.Id.ToString());
        await dbContext.SaveChangesAsync(cancellationToken);
        cache?.Clear();
        return entity;
    }

    private EffectiveSettingsResponse MapResponse(CloudSettingsEntity entity)
    {
        return new EffectiveSettingsResponse(
            SettingsSource: "cloud",
            Summary: "Shared business settings are persisted in PostgreSQL and served to the desktop through the API.",
            Settings: new EffectiveCloudSettingsDto(
                Connection: new ConnectionSettingsDto(entity.Host, entity.Port, entity.TimeoutSeconds, entity.ActiveCompanyName),
                Numbering: new NumberingSettingsDto(entity.InvoicePrefix, entity.InvoiceSuffix, entity.InvoicePadding <= 0 ? 4 : entity.InvoicePadding),
                Print: new PrintSettingsDto(
                    entity.PrintCompanyName,
                    entity.CompanyGstin,
                    entity.CompanyPhone,
                    entity.CompanyAddress,
                    entity.CompanyState,
                    entity.CompanyCountry,
                    entity.BankName,
                    entity.BankAccount,
                    entity.BankIfsc,
                    entity.BankUpi,
                    entity.TermsAndConditions,
                    entity.CopyOriginalDefault,
                    entity.CopyDuplicateDefault,
                    entity.CopyTriplicateDefault,
                    entity.PrintFontSize,
                    entity.PrintTermsFontSize),
                Ledgers: new LedgerMappingsDto(
                    entity.SalesLedger,
                    entity.CashLedger,
                    entity.CreditDebitLedger,
                    entity.CgstLedger,
                    entity.SgstLedger,
                    entity.RoundOffLedger,
                    entity.DiscountLedger,
                    entity.SalesVoucherType),
                Masters: new MasterDataSettingsDto(entity.ItemMasterDataJson, entity.KaratMappingDataJson)),
            CloudOwnedCategories:
            [
                "Connection settings",
                "Active company",
                "Numbering",
                "Print settings",
                "Ledger mappings",
                "Voucher type settings",
                "Item master data",
                "Karat/stock mappings",
                "Admin/shared operational settings"
            ],
            LocalOnlyCategories:
            [
                "Bootstrap API endpoint",
                "Protected local secrets/tokens",
                "Logs",
                "Workstation-local UX preferences"
            ],
            UpdatedAtUtc: entity.UpdatedAtUtc,
            RequiresInitialSetup: RequiresInitialSetup(entity));
    }

    /// <summary>
    /// True while the settings still carry a placeholder nobody would keep:
    /// the seeded company names, the seeded invoice prefix, or no items or
    /// karats at all. The ledger and voucher-type defaults are not placeholders
    /// in that sense: "Sales" and "Cash" are what those are really called in
    /// most Tally companies, so matching them says nothing.
    /// </summary>
    private static bool RequiresInitialSetup(CloudSettingsEntity entity) =>
        IsPlaceholder(entity.ActiveCompanyName, CloudSettingsDefaults.ActiveCompanyName)
        || IsPlaceholder(entity.PrintCompanyName, CloudSettingsDefaults.PrintCompanyName)
        || IsPlaceholder(entity.PrintCompanyName, CloudSettingsDefaults.LegacyPrintCompanyName)
        || IsPlaceholder(entity.InvoicePrefix, CloudSettingsDefaults.InvoicePrefix)
        || IsEmptyJsonArray(entity.ItemMasterDataJson)
        || IsEmptyJsonArray(entity.KaratMappingDataJson);

    private static bool IsPlaceholder(string? value, string placeholder) =>
        string.Equals(value, placeholder, StringComparison.OrdinalIgnoreCase);

    private static bool IsEmptyJsonArray(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return string.Equals(value.Trim(), "[]", StringComparison.Ordinal);
    }

    private void WriteAuditEvent(string eventType, string entityId)
    {
        var actor = actorContext?.Current ?? AuditRow.System;
        dbContext.AuditEvents.Add(AuditRow.Create("settings", entityId, eventType, actor, DateTimeOffset.UtcNow));
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeJson(string value) =>
        string.IsNullOrWhiteSpace(value) ? "[]" : value.Trim();

}
