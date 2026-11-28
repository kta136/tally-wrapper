using ShowroomBilling.Application.Settings;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests.TestSupport;

internal class FakeCloudSettingsService : ICloudSettingsService
{
    public string ActiveCompany { get; init; } = "Acme Jewellers";
    public string? CompanyState { get; init; }
    public string? CompanyCountry { get; init; }
    public string SalesVoucherType { get; init; } = "Sales";
    public Exception? ReadFailure { get; init; }
    public MasterDataSettingsDto Masters { get; init; } = new("[]", "[]");

    public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
    {
        if (ReadFailure is not null)
        {
            throw ReadFailure;
        }

        return Task.FromResult(new EffectiveSettingsResponse(
            SettingsSource: "cloud",
            Summary: "test",
            Settings: new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("127.0.0.1", 9000, 30, ActiveCompany),
                new NumberingSettingsDto("DEV-", "", 4),
                new PrintSettingsDto("Acme", null, null, null, CompanyState, CompanyCountry, null, null, null, null, null, true, false, false, 11, 9),
                new LedgerMappingsDto("Sales", "Cash", "Card", "CGST", "SGST", "Round Off", "Discount", SalesVoucherType),
                Masters),
            CloudOwnedCategories: [],
            LocalOnlyCategories: [],
            UpdatedAtUtc: DateTimeOffset.UtcNow));
    }

    public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
        UpdateEffectiveSettingsRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
        string companyName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
        UpdatePrintLayoutRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual Task<BatchHoursResponse> GetBatchHoursAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<BatchHoursResponse> UpdateBatchHoursAsync(
        UpdateBatchHoursRequest request,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
