using ShowroomBilling.Application.Bills;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

public sealed class BillCalculatorDiamondTests
{
    [Fact]
    public void Diamond_effective_rate_uses_flat_diamond_rate()
    {
        var input = new BillCalculator.LineInputs(
            Rate24Kt: 72500m,
            PurityPercent: 91.6m,
            WastagePercent: 12m,
            LabourPerUnit: 500m,
            NetWeight: 2m,
            ExtraCharges: 0m,
            PricingMode: PricingModes.Both,
            IsDiamond: true,
            DiamondRate: 42000m);

        var rate = BillCalculator.ComputeEffectiveRate(input);

        Assert.Equal(42000m, rate);
    }

    [Fact]
    public void Diamond_line_total_uses_flat_rate_times_quantity_plus_extra()
    {
        var input = new BillCalculator.LineInputs(
            Rate24Kt: 0m,
            PurityPercent: 0m,
            WastagePercent: 0m,
            LabourPerUnit: 0m,
            NetWeight: 3.5m,
            ExtraCharges: 250m,
            PricingMode: PricingModes.Wastage,
            IsDiamond: true,
            DiamondRate: 42000m);

        var result = BillCalculator.ComputeLine(input);

        Assert.Equal(42000m, result.EffectiveRate);
        Assert.Equal(147250m, result.LineTotalInclusive);
    }
}
