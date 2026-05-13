using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class InvoiceDiamondViewModelTests
{
    [Fact]
    public void Diamond_line_quantity_uses_gross_weight_and_ignores_less_weight()
    {
        var line = new BillLineViewModel
        {
            ItemMaster = DiamondMaster(),
            GrossWeight = 3.5m,
            LessWeight = 1.25m,
            DiamondRate = 42000m
        };

        Assert.True(line.IsDiamond);
        Assert.Equal(0m, line.LessWeight);
        Assert.Equal(3.5m, line.NetWeight);
        Assert.Equal(string.Empty, line.Karat);
        Assert.Equal(0m, line.WastagePercent);
        Assert.Equal(0m, line.LabourPerUnit);
    }

    [Fact]
    public async Task Diamond_only_bill_saves_without_24kt_rate()
    {
        var api = new FakeBillsApi();
        var vm = new InvoiceViewModel(api, null, null);
        var line = vm.Lines[0];
        line.ItemMaster = DiamondMaster(defaultStockMappingLabel: "Diamond Stock");
        line.GrossWeight = 2m;
        line.LessWeight = 1m;
        line.DiamondRate = 3500m;

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.NotNull(api.LastCreate);
        var savedLine = api.LastCreate!.Payload.Lines.Single();
        Assert.Equal(2m, savedLine.Quantity);
        Assert.Equal("diamond", savedLine.ItemCategory);
        Assert.Equal("wastage", savedLine.PricingMode);
        Assert.Equal("Diamond Stock", savedLine.StockName);
        Assert.Equal(3500m, savedLine.Rate);
        Assert.Equal(7000m, savedLine.LineTotal);
        Assert.False(vm.RateMissing);
    }

    [Fact]
    public async Task Gold_bill_requires_24kt_rate()
    {
        var api = new FakeBillsApi();
        var vm = new InvoiceViewModel(api, null, null);
        var line = vm.Lines[0];
        line.ItemMaster = GoldMaster();
        line.GrossWeight = 2m;
        line.Karat = "22K";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(api.LastCreate);
        Assert.True(vm.RateMissing);
        Assert.Contains("24kt", vm.SaveStatus, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diamond_bill_requires_diamond_rate()
    {
        var api = new FakeBillsApi();
        var vm = new InvoiceViewModel(api, null, null);
        var line = vm.Lines[0];
        line.ItemMaster = DiamondMaster();
        line.GrossWeight = 2m;

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(api.LastCreate);
        Assert.Contains("Diamond rate", vm.SaveStatus);
    }

    [Fact]
    public async Task Mixed_bill_requires_24kt_rate()
    {
        var api = new FakeBillsApi();
        var vm = new InvoiceViewModel(api, null, null);
        vm.Lines[0].ItemMaster = DiamondMaster();
        vm.Lines[0].GrossWeight = 2m;
        vm.Lines[0].DiamondRate = 3500m;
        vm.Lines[1].ItemMaster = GoldMaster();
        vm.Lines[1].GrossWeight = 1m;
        vm.Lines[1].Karat = "22K";

        await vm.SaveDraftCommand.ExecuteAsync(null);

        Assert.Null(api.LastCreate);
        Assert.True(vm.RateMissing);
    }

    [Fact]
    public void Typed_item_name_attaches_matching_master_after_master_name_changes()
    {
        var settings = new SettingsViewModel();
        var master = GoldMaster();
        settings.Draft.ItemMasterRows.Add(master);
        var vm = new InvoiceViewModel(null, null, settings);

        vm.Lines[0].ItemName = " 22k ring ";

        Assert.Same(master, vm.Lines[0].ItemMaster);

        master.Name = "Renamed Gold Ring";
        var nextLine = vm.Lines.Single(l => l.IsEmpty);

        nextLine.ItemName = "renamed gold ring";

        Assert.Same(master, nextLine.ItemMaster);
    }

    [Fact]
    public async Task LoadBillForEdit_preserves_stored_item_category_and_pricing_mode()
    {
        var billId = Guid.NewGuid();
        var payload = PayloadWithLine(new BillLineItemDto(
            ItemName: "Diamond Ring",
            HsnCode: "7113",
            Quantity: 2m,
            Unit: ItemUnits.Carat,
            Rate: 3500m,
            LineTotal: 7000m,
            Karat: "18K",
            RawJson: null,
            StockName: "Diamond Stock",
            GrossWeight: 2m,
            LessWeight: 0m,
            WastagePercent: 0m,
            LabourPerUnit: 0m,
            DiamondRate: 3500m,
            Extra: 0m,
            ItemCategory: ItemCategories.Diamond,
            PricingMode: PricingModes.Wastage));
        var api = new FakeBillsApi
        {
            GetResponse = BillResponseFor(billId, payload)
        };
        var vm = new InvoiceViewModel(api, null, null);

        await vm.LoadBillForEditAsync(billId);

        var loaded = vm.Lines.First(l => !l.IsEmpty);
        Assert.True(loaded.IsDiamond);
        Assert.Equal(ItemCategories.Diamond, loaded.ItemCategory);
        Assert.Equal(PricingModes.Wastage, loaded.PricingMode);
        Assert.Equal("Diamond Stock", loaded.StockName);
        Assert.Equal(2m, loaded.NetWeight);
    }

    private static ItemMasterRowVm DiamondMaster(string defaultStockMappingLabel = "") => new()
    {
        Name = "Diamond Ring",
        Unit = ItemUnits.Carat,
        ItemCategory = ItemCategories.Diamond,
        PricingMode = PricingModes.Wastage,
        WastagePercent = "12",
        DefaultLabourPerGram = "500",
        DefaultStockMappingLabel = defaultStockMappingLabel
    };

    private static ItemMasterRowVm GoldMaster() => new()
    {
        Name = "22K Ring",
        Unit = ItemUnits.Gram,
        ItemCategory = ItemCategories.GoldBased,
        PricingMode = PricingModes.Wastage,
        WastagePercent = "8",
        DefaultLabourPerGram = "0"
    };

    private static BillPayloadDto PayloadWithLine(BillLineItemDto line) => new(
        PartyName: "Customer",
        PartyGstin: null,
        PartyPhone: null,
        PartyAddress: null,
        BillDate: new DateOnly(2026, 4, 24),
        Lines: [line],
        Totals: new BillTotalsDto(line.LineTotal, 0m, 0m, 0m, line.LineTotal),
        Notes: null,
        Payment: "Cash",
        Rate24Kt: null);

    private static BillResponse BillResponseFor(Guid id, BillPayloadDto payload) => new(
        Id: id,
        ShowroomId: Guid.NewGuid(),
        CounterId: null,
        BillType: "sales",
        State: "pending",
        InvoiceNumber: "SR/25-26/0001",
        FiscalYear: "2025-2026",
        SupersededByBillId: null,
        CurrentRevisionId: Guid.NewGuid(),
        CreatedAtUtc: DateTimeOffset.UtcNow,
        UpdatedAtUtc: DateTimeOffset.UtcNow,
        VoidedAtUtc: null,
        CurrentRevision: new BillRevisionResponse(
            Id: Guid.NewGuid(),
            RevisionNo: 1,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            SubmittedAtUtc: null,
            FinalizedAtUtc: null,
            SupersedesRevisionId: null,
            Payload: payload));

    private sealed class FakeBillsApi : IBillsApiClient
    {
        public CreateBillDraftRequest? LastCreate { get; private set; }
        public BillResponse? GetResponse { get; init; }

        public Task<BillResponse> CreateDraftAsync(CreateBillDraftRequest request, CancellationToken cancellationToken = default)
        {
            LastCreate = request;
            return Task.FromResult(BillResponseFor(Guid.NewGuid(), request.Payload));
        }

        public Task<BillResponse?> GetAsync(Guid billId, CancellationToken cancellationToken = default)
            => Task.FromResult(GetResponse);

        public Task<BillResponse> UpdateDraftAsync(Guid billId, UpdateBillDraftRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> PushAsync(Guid billId, PushBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushSelectedAsync(PushSelectedBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchPushResponse> PushPendingAsync(PushPendingBillsRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillListResponse> SearchAsync(BillSearchFilter filter, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillBatchGetResponse> GetManyAsync(BillBatchGetRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillAuditResponse?> GetAuditAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse?> GetPostingStatusAsync(Guid billId, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse> RetryAsync(Guid billId, RetryBillPostingRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillPostingStatusResponse> RepostAsync(Guid billId, RepostBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> VoidAsync(Guid billId, VoidBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> ReviseAsync(Guid billId, ReviseBillRequest request, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<ChangeBillNumberResponse> ChangeInvoiceNumberAsync(Guid billId, ChangeBillNumberRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> MarkPostedAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<BillResponse> MarkPendingAsync(Guid billId, MarkBillStateRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DeleteBillResponse> DeleteAsync(Guid billId, DeleteBillRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<DeleteSelectedBillsResponse> DeleteSelectedAsync(DeleteSelectedBillsRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
        public Task<SyntheticBatchResponse> CreateSyntheticBatchAsync(SyntheticBatchRequest request, string adminToken, CancellationToken cancellationToken = default) => throw new NotImplementedException();
    }
}
