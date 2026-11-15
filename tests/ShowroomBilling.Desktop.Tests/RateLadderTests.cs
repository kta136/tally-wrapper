using ShowroomBilling.Contracts.Settings;
using ShowroomBilling.Desktop.ViewModels.Invoice;

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
            new KaratMasterEntry("22KT", 91.7m, "x"),
            new KaratMasterEntry("18kt", 75.0m, "y"),
            new KaratMasterEntry("14KT", 58.5m, "z"),
        };

        var ladder = RateLadder.For(8000m, masters);

        Assert.Equal(new RateLadder("8,000", "7,336", "6,000"), ladder);
    }

    [Fact]
    public void A_purity_of_nothing_or_over_a_hundred_falls_back_to_the_standard_one()
    {
        var masters = new[]
        {
            new KaratMasterEntry("22KT", 0m, "x"),
            new KaratMasterEntry("18KT", 150m, "y"),
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
