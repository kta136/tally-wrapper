using System.Net.Http;
using ShowroomBilling.Desktop.Tests.TestSupport;
using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The number shown on a new bill before it is saved: the next one the server
/// would give, or the "Assigned on save" hint when the server cannot be asked.
/// </summary>
public sealed class InvoiceNextNumberTests
{
    [Fact]
    public async Task The_next_number_is_shown_in_place_of_the_hint()
    {
        var numbering = new FakeNumberingApi();
        var vm = new InvoiceViewModel(billsApi: new FakeBillsApi(), numberingApi: numbering, dialogs: new FakeDialogs(), settings: null);
        Assert.False(vm.IsInvoiceNumberAssigned);

        await vm.RefreshNextNumberAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, numbering.PreviewCallCount);
        Assert.Equal("SR/26-27/0001", vm.InvoiceNumber);
        Assert.True(vm.IsInvoiceNumberAssigned);
    }

    [Fact]
    public async Task When_the_server_cannot_be_asked_the_hint_stays()
    {
        var numbering = new FakeNumberingApi { Failure = new HttpRequestException("No connection could be made") };
        var vm = new InvoiceViewModel(billsApi: new FakeBillsApi(), numberingApi: numbering, dialogs: new FakeDialogs(), settings: null);
        var hint = vm.InvoiceNumber;

        await vm.RefreshNextNumberAsync(TestContext.Current.CancellationToken);

        Assert.Equal(hint, vm.InvoiceNumber);
        Assert.False(vm.IsInvoiceNumberAssigned);
    }
}
