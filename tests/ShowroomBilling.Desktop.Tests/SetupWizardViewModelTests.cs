using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Setup;

namespace ShowroomBilling.Desktop.Tests;

public sealed class SetupWizardViewModelTests
{
    [Fact]
    public async Task PrepareForStartupAsync_Opens_WhenSettingsRequireSetup()
    {
        var vm = BuildVm(settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: true)));

        var shouldOpen = await vm.PrepareForStartupAsync(null);

        Assert.True(shouldOpen);
        Assert.Equal("Development Company", vm.ActiveCompanyName);
        Assert.True(vm.HasStatusMessage);
    }

    [Fact]
    public async Task PrepareForStartupAsync_UsesTheSettingsItIsGiven_WithoutAskingAgain()
    {
        var api = new FakeSettingsApiClient(Settings(requiresInitialSetup: false));
        var vm = BuildVm(settings: api);

        var shouldOpen = await vm.PrepareForStartupAsync(Settings(requiresInitialSetup: true));

        Assert.True(shouldOpen);
        Assert.Equal(0, api.GetCount);
    }

    [Fact]
    public async Task PrepareForStartupAsync_DoesNotOpen_WhenSettingsAreReal()
    {
        var vm = BuildVm(settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: false)));

        var shouldOpen = await vm.PrepareForStartupAsync(null);

        Assert.False(shouldOpen);
    }

    [Fact]
    public async Task PrepareForStartupAsync_DoesNotOpen_OnceThisPcHasCompletedSetup()
    {
        var marker = new FakeSetupWizardCompletionStore();
        await marker.MarkCompleteAsync(TestContext.Current.CancellationToken);
        var vm = BuildVm(
            settings: new FakeSettingsApiClient(Settings(requiresInitialSetup: true)),
            marker: marker);

        var shouldOpen = await vm.PrepareForStartupAsync(null);

        Assert.False(shouldOpen);
    }

    [Fact]
    public async Task SaveBasicsCommand_SavesSettings_AndMarksComplete()
    {
        var settings = new FakeSettingsApiClient(Settings(requiresInitialSetup: true));
        var marker = new FakeSetupWizardCompletionStore();
        var vm = BuildVm(settings: settings, marker: marker);
        var completed = 0;
        vm.Completed += (_, _) => completed++;

        await vm.PrepareForStartupAsync(null);
        vm.ActiveCompanyName = "Showroom Alpha";
        vm.PrintCompanyName = "Alpha Jewellers";
        vm.InvoicePrefix = "SB-";
        vm.SalesLedger = "Alpha Sales";
        vm.CashLedger = "Alpha Cash";
        vm.CreditDebitLedger = "Alpha Card";
        vm.CgstLedger = "Alpha CGST";
        vm.SgstLedger = "Alpha SGST";
        vm.RoundOffLedger = "Alpha Round Off";
        vm.DiscountLedger = "Alpha Discount";
        vm.SalesVoucherType = "Alpha Sales Voucher";

        await vm.SaveBasicsCommand.ExecuteAsync(null);

        Assert.True(marker.Completed);
        Assert.Equal(1, completed);
        Assert.NotNull(settings.LastSaved);
        Assert.Equal("Showroom Alpha", settings.LastSaved!.Connection.ActiveCompanyName);
    }

    [Fact]
    public async Task SaveBasicsCommand_RefusesAMissingLedger_AndSavesNothing()
    {
        var settings = new FakeSettingsApiClient(Settings(requiresInitialSetup: true));
        var marker = new FakeSetupWizardCompletionStore();
        var vm = BuildVm(settings: settings, marker: marker);
        await vm.PrepareForStartupAsync(null);
        vm.CashLedger = " ";

        await vm.SaveBasicsCommand.ExecuteAsync(null);

        Assert.True(vm.HasErrorMessage);
        Assert.Null(settings.LastSaved);
        Assert.False(marker.Completed);
    }

    private static SetupWizardViewModel BuildVm(
        FakeSettingsApiClient? settings = null,
        FakeSetupWizardCompletionStore? marker = null)
    {
        return new SetupWizardViewModel(
            settings ?? new FakeSettingsApiClient(Settings(requiresInitialSetup: false)),
            marker ?? new FakeSetupWizardCompletionStore());
    }

    private static EffectiveSettingsResponse Settings(bool requiresInitialSetup) =>
        new(
            "cloud",
            "settings",
            new EffectiveCloudSettingsDto(
                new ConnectionSettingsDto("127.0.0.1", 9000, 30, "Development Company"),
                new NumberingSettingsDto("DEV-", string.Empty, 4),
                new PrintSettingsDto("Tally Wrapper", null, null, null, null, "India", null, null, null, null, null, true, false, false, 11, 9),
                new LedgerMappingsDto("Sales", "Cash", "Card / UPI", "CGST", "SGST", "Round Off", "Discount", "Sales"),
                new MasterDataSettingsDto("[]", "[]")),
            [],
            [],
            DateTimeOffset.UtcNow,
            requiresInitialSetup);

    private sealed class FakeSettingsApiClient(EffectiveSettingsResponse response) : ISettingsApiClient
    {
        public EffectiveCloudSettingsDto? LastSaved { get; private set; }

        public int GetCount { get; private set; }

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
        {
            GetCount++;
            return Task.FromResult(response);
        }

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default)
        {
            LastSaved = request.Settings;
            return Task.FromResult(new SettingsUpdateResponse("cloud", "saved", ["connection"], DateTimeOffset.UtcNow));
        }

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeSetupWizardCompletionStore : ISetupWizardCompletionStore
    {
        public bool Completed { get; private set; }

        public bool IsComplete() => Completed;

        public Task MarkCompleteAsync(CancellationToken cancellationToken = default)
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }
}
