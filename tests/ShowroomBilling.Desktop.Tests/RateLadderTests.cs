using ShowroomBilling.Desktop.ViewModels.Invoice;
using ShowroomBilling.Desktop.ViewModels.Settings;

namespace ShowroomBilling.Desktop.Tests;

public sealed class RateLadderTests
{
    [Fact]
    public void Without_a_24K_rate_there_is_no_ladder()
    {
        Assert.Null(RateLadder.For(null, null));
        Assert.Null(RateLadder.For(0m, null));
    }

    [Fact]
    public void With_no_karat_masters_the_standard_purities_are_used()
    {
        var ladder = RateLadder.For(7780m, null);

        Assert.Equal(new RateLadder("7,780", "7,126", "5,835"), ladder);
    }

    [Fact]
    public void The_karat_master_purity_wins_when_its_label_names_the_karat()
    {
        var masters = new[]
        {
            new KaratMasterRowVm { Label = "22KT", PurityPercent = "91.7", TallyItem = "x" },
            new KaratMasterRowVm { Label = "18kt", PurityPercent = "75.0", TallyItem = "y" },
            new KaratMasterRowVm { Label = "14KT", PurityPercent = "58.5", TallyItem = "z" },
        };

        var ladder = RateLadder.For(8000m, masters);

        Assert.Equal(new RateLadder("8,000", "7,336", "6,000"), ladder);
    }

    [Fact]
    public void An_unreadable_or_out_of_range_purity_falls_back_to_the_standard_one()
    {
        var masters = new[]
        {
            new KaratMasterRowVm { Label = "22KT", PurityPercent = "abc", TallyItem = "x" },
            new KaratMasterRowVm { Label = "18KT", PurityPercent = "150", TallyItem = "y" },
        };

        var ladder = RateLadder.For(10000m, masters);

        Assert.Equal(new RateLadder("10,000", "9,160", "7,500"), ladder);
    }

    [Fact]
    public void Amounts_group_in_lakhs()
    {
        Assert.Equal("1,25,000", RateLadder.For(125000m, null)!.Rate24Kt);
    }
}
