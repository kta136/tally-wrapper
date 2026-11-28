using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class MasterDataSettingsViewModelTests
{
    [Fact]
    public async Task LoadMissingSnapshotsAsync_PopulatesSnapshots_AndSelectsActiveCompany()
    {
        var shell = new FakeShell { ActiveCompanyNameValue = "Example Jewellers" };
        var mastersApi = new FakeMastersApiClient();
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), mastersApi, shell);

        await vm.LoadMissingSnapshotsAsync(CancellationToken.None);

        Assert.Equal(2, vm.Companies.Count);
        Assert.Equal("Example Jewellers", vm.SelectedCompany?.Name);
        Assert.Equal(2, vm.LedgerOptions.Count);
        Assert.Single(vm.VoucherTypeOptions);
        Assert.Equal(2, vm.StockItems.Count);
        Assert.Equal("fresh", vm.CompaniesFreshness);
        Assert.Equal("fresh", vm.LedgersFreshness);
        Assert.Equal("fresh", vm.StockItemsFreshness);
    }

    [Fact]
    public async Task The_lists_read_when_settings_opens_leave_the_status_line_alone()
    {
        // Nobody asked for them, and the line is shared by every section: their summary used to
        // sit under Print layout.
        var shell = new FakeShell { StatusMessage = "Changes discarded." };
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), new FakeMastersApiClient(), shell);

        await vm.LoadMissingSnapshotsAsync(CancellationToken.None);

        Assert.Equal(2, vm.LedgerOptions.Count);
        Assert.Equal("Changes discarded.", shell.StatusMessage);
    }

    [Fact]
    public async Task A_list_that_cannot_be_read_when_settings_opens_is_still_reported()
    {
        var shell = new FakeShell();
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), new OfflineMastersApi(), shell);

        await vm.LoadMissingSnapshotsAsync(CancellationToken.None);

        Assert.Contains("failed", shell.StatusMessage);
    }

    [Fact]
    public async Task A_refresh_says_what_was_loaded()
    {
        var shell = new FakeShell();
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), new FakeMastersApiClient(), shell);

        await vm.RequestLedgerRefreshCommand.ExecuteAsync(null);

        Assert.Equal("2 ledger(s) · 1 voucher type(s) · fresh", shell.StatusMessage);
    }

    [Fact]
    public void RowCommands_FollowEditingState_AndMutateDraft()
    {
        var shell = new FakeShell { IsEditingValue = false };
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), new OfflineMastersApi(), shell);

        Assert.False(vm.AddItemMasterRowCommand.CanExecute(null));
        Assert.False(vm.AddKaratRowCommand.CanExecute(null));

        shell.IsEditingValue = true;
        vm.NotifyShellStateChanged();

        Assert.True(vm.AddItemMasterRowCommand.CanExecute(null));
        Assert.True(vm.AddKaratRowCommand.CanExecute(null));

        vm.AddItemMasterRowCommand.Execute(null);
        vm.AddKaratRowCommand.Execute(null);

        Assert.Single(shell.Draft.ItemMasterRows);
        Assert.Single(shell.Draft.KaratRows);

        vm.RemoveItemMasterRowCommand.Execute(shell.Draft.ItemMasterRows[0]);
        vm.RemoveKaratRowCommand.Execute(shell.Draft.KaratRows[0]);

        Assert.Empty(shell.Draft.ItemMasterRows);
        Assert.Empty(shell.Draft.KaratRows);
    }

    [Fact]
    public async Task SetActiveCompany_DelegatesToSettingsApi_AndReloadsShell()
    {
        var shell = new FakeShell { ActiveCompanyNameValue = "Example Jewellers" };
        var settingsApi = new FakeSettingsApiClient();
        var mastersApi = new FakeMastersApiClient();
        var vm = new MasterDataSettingsViewModel(settingsApi, mastersApi, shell);

        await vm.LoadMissingSnapshotsAsync(CancellationToken.None);
        vm.SelectedCompany = vm.Companies.Single(c => c.Name == "Backup Jewels");

        await vm.SetActiveCompanyCommand.ExecuteAsync(null);

        Assert.Equal("Backup Jewels", settingsApi.SelectedCompanyName);
        Assert.Equal(1, shell.LoadCount);
        Assert.Equal("cloud", shell.SettingsSource);
        Assert.Equal("saved", shell.Summary);
        Assert.Contains("Active company set to Backup Jewels", shell.StatusMessage);
    }

    [Fact]
    public async Task Refreshing_ledgers_asks_tally_for_ledgers_and_voucher_types_only()
    {
        var mastersApi = new FakeMastersApiClient();
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), mastersApi, new FakeShell());

        await vm.RequestLedgerRefreshCommand.ExecuteAsync(null);

        // No master type at all would refresh all four; companies and stock items were not asked for.
        Assert.Equal([MasterTypes.Ledgers, MasterTypes.VoucherTypes], mastersApi.RefreshedMasterTypes);
        Assert.Equal(2, vm.LedgerOptions.Count);
        Assert.Single(vm.VoucherTypeOptions);
        Assert.False(vm.IsFetchingLedgers);
    }

    [Fact]
    public async Task Each_refresh_button_asks_for_its_own_master()
    {
        var mastersApi = new FakeMastersApiClient();
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), mastersApi, new FakeShell());

        await vm.RequestCompanyRefreshCommand.ExecuteAsync(null);
        await vm.RequestStockItemRefreshCommand.ExecuteAsync(null);

        Assert.Equal([MasterTypes.Companies, MasterTypes.StockItems], mastersApi.RefreshedMasterTypes);
        Assert.Equal(2, vm.Companies.Count);
        Assert.Equal(2, vm.StockItems.Count);
    }

    [Fact]
    public async Task A_refresh_tally_refused_reports_it_and_leaves_the_cached_list_alone()
    {
        var shell = new FakeShell();
        var mastersApi = new FakeMastersApiClient { RefreshError = "Tally is not running." };
        var vm = new MasterDataSettingsViewModel(new OfflineSettingsApi(), mastersApi, shell);

        await vm.RequestStockItemRefreshCommand.ExecuteAsync(null);

        Assert.Equal($"Refresh failed for {MasterTypes.StockItems}: Tally is not running.", shell.StatusMessage);
        Assert.Empty(vm.StockItems);
        Assert.False(vm.IsFetchingStockItems);
    }

    private sealed class FakeShell : IMasterDataSettingsShell
    {
        public SettingsDraft Draft { get; } = new();
        public bool IsEditingValue { get; set; }
        public bool IsEditing => IsEditingValue;
        public string ActiveCompanyNameValue { get; set; } = string.Empty;
        public string ActiveCompanyName => ActiveCompanyNameValue;
        public string StatusMessage { get; set; } = string.Empty;
        public string SettingsSource { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public DateTimeOffset? UpdatedAtUtc { get; set; }
        public int LoadCount { get; private set; }

        public Task LoadAsync(CancellationToken cancellationToken = default)
        {
            LoadCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeMastersApiClient : IMastersApiClient
    {
        private readonly DateTimeOffset _fetchedAt = new(2026, 6, 14, 10, 0, 0, TimeSpan.Zero);

        public Task<CompanySnapshotResponse> GetCompaniesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new CompanySnapshotResponse(
                Metadata("companies", 2),
                new[]
                {
                    new CompanySnapshotItem("Backup Jewels", false),
                    new CompanySnapshotItem("Example Jewellers", true),
                }));

        public Task<LedgerSnapshotResponse> GetLedgersAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new LedgerSnapshotResponse(
                Metadata("ledgers", 2),
                new[]
                {
                    new LedgerSnapshotItem("Sales"),
                    new LedgerSnapshotItem("Cash"),
                }));

        public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new VoucherTypeSnapshotResponse(
                Metadata("voucher-types", 1),
                new[] { new VoucherTypeSnapshotItem("Sales") }));

        public Task<StockItemSnapshotResponse> GetStockItemsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new StockItemSnapshotResponse(
                Metadata("stock-items", 2),
                new[]
                {
                    new StockItemSnapshotItem("Gold 22K", "GMS", "7113"),
                    new StockItemSnapshotItem("Diamond", "CTS", "7102"),
                }));

        public List<string?> RefreshedMasterTypes { get; } = [];
        public string? RefreshError { get; init; }

        public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
            MasterRefreshRequest request,
            CancellationToken cancellationToken = default)
        {
            RefreshedMasterTypes.Add(request.MasterType);
            return Task.FromResult<IReadOnlyList<TallyMasterRefreshResult>>(
            [
                new TallyMasterRefreshResult(
                    request.MasterType ?? "all", RefreshError is null, 1, RefreshError)
            ]);
        }

        private MasterSnapshotMetadata Metadata(string masterType, int count)
            => new(masterType, _fetchedAt, count, "fresh");
    }

    private sealed class FakeSettingsApiClient : SettingsApiStub
    {
        public string? SelectedCompanyName { get; private set; }

        public override Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default)
        {
            SelectedCompanyName = request.CompanyName;
            return Task.FromResult(new SettingsUpdateResponse("cloud", "saved", ["connection"], DateTimeOffset.UtcNow));
        }
    }
}
