using ShowroomBilling.Desktop.ViewModels.Invoice;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillLineViewModelTests
{
    [Fact]
    public void Editing_gross_or_less_weight_recalculates_net_weight()
    {
        var changedProperties = new List<string?>();
        var line = new BillLineViewModel();
        line.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        line.GrossWeight = 12.5m;

        Assert.Equal(12.5m, line.NetWeight);
        Assert.Contains(nameof(BillLineViewModel.NetWeight), changedProperties);

        changedProperties.Clear();
        line.LessWeight = 2.25m;

        Assert.Equal(10.25m, line.NetWeight);
        Assert.Contains(nameof(BillLineViewModel.NetWeight), changedProperties);
    }

    [Fact]
    public void Editing_net_weight_recalculates_less_weight()
    {
        var changedProperties = new List<string?>();
        var line = new BillLineViewModel { GrossWeight = 12.5m };
        line.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        line.NetWeight = 10.25m;

        Assert.Equal(2.25m, line.LessWeight);
        Assert.Equal(10.25m, line.NetWeight);
        Assert.Contains(nameof(BillLineViewModel.LessWeight), changedProperties);
    }

    [Fact]
    public void Weight_calculations_do_not_produce_negative_values()
    {
        var line = new BillLineViewModel { GrossWeight = 5m };

        line.NetWeight = 6m;

        Assert.Equal(0m, line.LessWeight);
        Assert.Equal(5m, line.NetWeight);

        line.LessWeight = 6m;

        Assert.Equal(0m, line.NetWeight);
    }
}
