using System.Text.Json;
using ShowroomBilling.Contracts.Settings;

namespace ShowroomBilling.Tests;

public sealed class MasterDataJsonTests
{
    private const string Karats = """[{"label":"22K","purity_percent":91.6,"tally_item":"Gold 22K"}]""";

    [Fact]
    public void Karats_are_read_by_their_stored_property_names()
    {
        var karat = Assert.Single(MasterDataJson.ParseKarats(Karats));

        Assert.Equal("22K", karat.Label);
        Assert.Equal(91.6m, karat.PurityPercent);
    }

    [Fact]
    public void Numbers_saved_as_text_and_differently_cased_names_still_parse()
    {
        var karat = Assert.Single(MasterDataJson.ParseKarats("""[{"Label":"18K","PURITY_PERCENT":"75"}]"""));

        Assert.Equal("18K", karat.Label);
        Assert.Equal(75m, karat.PurityPercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("null")]
    public void Blank_text_is_an_empty_list(string? json)
    {
        Assert.Empty(MasterDataJson.ParseItems(json));
        Assert.Empty(MasterDataJson.ParseKarats(json));
    }

    [Fact]
    public void Malformed_json_throws_unless_the_caller_asks_for_empty()
    {
        Assert.Throws<JsonException>(() => MasterDataJson.ParseItems("[{"));
        Assert.Empty(MasterDataJson.ParseItemsOrEmpty("[{"));
        Assert.Empty(MasterDataJson.ParseKaratsOrEmpty("not json"));
    }
}
