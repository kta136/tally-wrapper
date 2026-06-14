using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Masters;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class MasterDataSettingsViewModelTests
{
    [Fact]
    public async Task LoadMissingSnapshotsAsync_PopulatesSnapshots_AndSelectsActiveCompany()
    {
        var shell = new FakeShell { ActiveCompanyNameValue = "DDA Jewels" };
        var mastersApi = new FakeMastersApiClient();
        var vm = new MasterDataSettingsViewModel(null, mastersApi, shell);

        await vm.LoadMissingSnapshotsAsync(CancellationToken.None);

        Assert.Equal(2, vm.Companies.Count);
        Assert.Equal("DDA Jewels", vm.SelectedCompany?.Name);
        Assert.Equal(2, vm.LedgerOptions.Count);
        Assert.Single(vm.VoucherTypeOptions);
        Assert.Equal(2, vm.StockItems.Count);
        Assert.Equal("fresh", vm.CompaniesFreshness);
        Assert.Equal("fresh", vm.LedgersFreshness);
        Assert.Equal("fresh", vm.StockItemsFreshness);
    }

    [Fact]
    public void RowCommands_FollowEditingState_AndMutateDraft()
    {
        var shell = new FakeShell { IsEditingValue = false };
        var vm = new MasterDataSettingsViewModel(null, null, shell);

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
        var shell = new FakeShell { ActiveCompanyNameValue = "DDA Jewels" };
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

        public Task<CompanySnapshotResponse> GetCompaniesAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null)
            => Task.FromResult(new CompanySnapshotResponse(
                Metadata("companies", 2),
                new[]
                {
                    new CompanySnapshotItem("Backup Jewels", false, null),
                    new CompanySnapshotItem("DDA Jewels", true, null),
                }));

        public Task<LedgerSnapshotResponse> GetLedgersAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null)
            => Task.FromResult(new LedgerSnapshotResponse(
                Metadata("ledgers", 2),
                new[]
                {
                    new LedgerSnapshotItem("Sales", null, "Sales Accounts", true, null, null),
                    new LedgerSnapshotItem("Cash", null, "Cash-in-Hand", false, null, null),
                }));

        public Task<VoucherTypeSnapshotResponse> GetVoucherTypesAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null)
            => Task.FromResult(new VoucherTypeSnapshotResponse(
                Metadata("voucher-types", 1),
                new[] { new VoucherTypeSnapshotItem("Sales", "Sales", false, null) }));

        public Task<StockItemSnapshotResponse> GetStockItemsAsync(
            CancellationToken cancellationToken = default,
            MasterSnapshotQuery? query = null)
            => Task.FromResult(new StockItemSnapshotResponse(
                Metadata("stock-items", 2),
                new[]
                {
                    new StockItemSnapshotItem("Gold 22K", null, "GMS", "7113", "22K", null),
                    new StockItemSnapshotItem("Diamond", null, "CTS", "7102", null, null),
                }));

        public Task<IReadOnlyList<TallyMasterRefreshResult>> RequestRefreshAsync(
            MasterRefreshRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<TallyMasterRefreshResult>>(
                [new TallyMasterRefreshResult(request.MasterType ?? "all", true, 1, "batch-1", null)]);

        private MasterSnapshotMetadata Metadata(string masterType, int count)
            => new(masterType, "batch-1", _fetchedAt, count, "fresh");
    }

    private sealed class FakeSettingsApiClient : ISettingsApiClient
    {
        public string? SelectedCompanyName { get; private set; }

        public Task<EffectiveSettingsResponse> GetEffectiveSettingsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SaveEffectiveSettingsAsync(
            UpdateEffectiveSettingsRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<SettingsUpdateResponse> SelectActiveCompanyAsync(
            SelectActiveCompanyRequest request,
            CancellationToken cancellationToken = default)
        {
            SelectedCompanyName = request.CompanyName;
            return Task.FromResult(new SettingsUpdateResponse("cloud", "saved", ["connection"], DateTimeOffset.UtcNow));
        }

        public Task<PrintLayoutResponse> GetPrintLayoutAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<PrintLayoutResponse> UpdatePrintLayoutAsync(
            UpdatePrintLayoutRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
