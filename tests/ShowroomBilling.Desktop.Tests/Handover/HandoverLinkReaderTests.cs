using System.Text.Json.Nodes;
using ShowroomBilling.Desktop.Services;
using ShowroomBilling.Desktop.Tests.TestSupport;

namespace ShowroomBilling.Desktop.Tests.Handover;

public sealed class HandoverLinkReaderTests
{
    private static readonly byte[] Key = HandoverTestLinks.VectorKeyBytes;
    private static readonly DateTimeOffset Now = HandoverTestLinks.VectorIssuedAt.AddMinutes(1);

    [Fact]
    public void The_contract_test_vector_signs_and_verifies()
    {
        Assert.Equal(HandoverTestLinks.VectorSignature, HandoverLinkReader.Sign(HandoverTestLinks.VectorData, Key));
        Assert.True(HandoverKeyStore.TryParse(HandoverTestLinks.VectorKey, out var parsed, out _));
        Assert.Equal(Key, parsed);

        var read = HandoverLinkReader.Read(
            HandoverTestLinks.Link(HandoverTestLinks.VectorData, HandoverTestLinks.VectorSignature), parsed, Now);

        Assert.Null(read.Problem);
        var bill = Assert.Single(read.Document!.Bills);
        Assert.Equal(Guid.Parse("0f8b6c1e-5a2d-4c7e-9b1a-3d4e5f60718a"), bill.SaveId);
        Assert.Equal("Buyer A", bill.PartyName);
        Assert.Equal(new DateOnly(2026, 10, 25), bill.BillDate);
        var line = Assert.Single(bill.Lines);
        Assert.Equal("Gold Ornaments", line.ItemName);
        Assert.Equal(72569.3m, line.LineTotal);
        Assert.Equal(7000m, line.Rate);
        Assert.Empty(read.Document.NotSent!);
    }

    [Theory]
    [InlineData("tallywrapper:handover?d={0}&s={1}")]
    [InlineData("tallywrapper://handover?d={0}&s={1}")]
    [InlineData("tallywrapper:handover/?d={0}&s={1}")]
    [InlineData("TallyWrapper:HANDOVER?d={0}&s={1}")]
    [InlineData("TALLYWRAPPER://Handover/?s={1}&d={0}")]
    [InlineData("  tallywrapper:handover?d={0}&s={1}  ")]
    public void Each_form_Windows_may_hand_over_is_read(string form)
    {
        var link = string.Format(System.Globalization.CultureInfo.InvariantCulture, form, HandoverTestLinks.VectorData, HandoverTestLinks.VectorSignature);

        Assert.True(HandoverLinkReader.Read(link, Key, Now).IsAccepted);
    }

    [Theory]
    [InlineData("")]
    [InlineData("tallywrapper:")]
    [InlineData("https://handover?d={0}&s={1}")]
    [InlineData("tallywrapper:other?d={0}&s={1}")]
    [InlineData("tallywrapper:handover/x?d={0}&s={1}")]
    [InlineData("tallywrapper:handover?d={0}")]
    [InlineData("tallywrapper:handover?s={1}")]
    [InlineData("tallywrapper:handover?d={0}&s={1}&x=1")]
    [InlineData("tallywrapper:handover?d={0}&d={0}&s={1}")]
    [InlineData("tallywrapper:handover?D={0}&S={1}")]
    [InlineData("tallywrapper:handover?d={0}&s={1}A")]
    [InlineData("tallywrapper:handover?d={0}=&s={1}")]
    [InlineData("tallywrapper:handover?d=&s={1}")]
    public void Any_other_link_is_refused_as_not_a_hand_over(string form)
    {
        var link = string.Format(System.Globalization.CultureInfo.InvariantCulture, form, HandoverTestLinks.VectorData, HandoverTestLinks.VectorSignature);

        var read = HandoverLinkReader.Read(link, Key, Now);

        Assert.Equal(HandoverLinkReader.NotAHandover, read.Problem);
        Assert.Null(read.Document);
    }

    [Fact]
    public void With_no_key_on_this_PC_the_link_is_refused_and_says_so()
    {
        var link = HandoverTestLinks.Link(HandoverTestLinks.VectorData, HandoverTestLinks.VectorSignature);

        Assert.Equal(HandoverLinkReader.NoKey, HandoverLinkReader.Read(link, null, Now).Problem);
        Assert.Equal(HandoverLinkReader.NoKey, HandoverLinkReader.Read(link, new byte[16], Now).Problem);
    }

    [Fact]
    public void A_changed_byte_anywhere_in_the_data_is_refused()
    {
        var data = HandoverTestLinks.VectorData;
        for (var index = 0; index < data.Length; index += 37)
        {
            var changed = data[..index] + (data[index] == 'A' ? 'B' : 'A') + data[(index + 1)..];

            var read = HandoverLinkReader.Read(HandoverTestLinks.Link(changed, HandoverTestLinks.VectorSignature), Key, Now);

            Assert.Equal(HandoverLinkReader.WrongSignature, read.Problem);
        }
    }

    [Fact]
    public void A_changed_signature_or_another_key_is_refused()
    {
        var signature = HandoverTestLinks.VectorSignature;
        var changed = (signature[0] == 'A' ? "B" : "A") + signature[1..];
        var otherKey = Key.Select(value => (byte)(value + 1)).ToArray();

        Assert.Equal(
            HandoverLinkReader.WrongSignature,
            HandoverLinkReader.Read(HandoverTestLinks.Link(HandoverTestLinks.VectorData, changed), Key, Now).Problem);
        Assert.Equal(
            HandoverLinkReader.WrongSignature,
            HandoverLinkReader.Read(HandoverTestLinks.Link(HandoverTestLinks.VectorData, signature), otherKey, Now).Problem);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(10 * 60, true)]
    [InlineData(10 * 60 + 1, false)]
    [InlineData(-5 * 60, true)]
    [InlineData(-5 * 60 - 1, false)]
    public void A_hand_over_older_than_ten_minutes_or_more_than_five_ahead_is_refused(int ageSeconds, bool accepted)
    {
        var link = HandoverTestLinks.Link(HandoverTestLinks.VectorData, HandoverTestLinks.VectorSignature);
        var now = HandoverTestLinks.VectorIssuedAt.AddSeconds(ageSeconds);

        var read = HandoverLinkReader.Read(link, Key, now);

        Assert.Equal(accepted, read.IsAccepted);
        if (!accepted)
        {
            Assert.Equal(ageSeconds > 0 ? HandoverLinkReader.TooOld : HandoverLinkReader.AheadOfClock, read.Problem);
        }
    }

    [Theory]
    [InlineData(2, HandoverLinkReader.NewerVersion)]
    [InlineData(0, HandoverLinkReader.Unreadable)]
    public void A_version_other_than_1_is_refused(int version, string problem)
    {
        var document = HandoverTestLinks.VectorDocument(Now);
        document["v"] = version;

        Assert.Equal(problem, HandoverLinkReader.Read(HandoverTestLinks.Signed(document), Key, Now).Problem);
    }

    public static TheoryData<string> BadShapes => new()
    {
        "missing v",
        "v as text",
        "missing issuedAt",
        "no bills",
        "bills not a list",
        "a bill that is null",
        "an empty save id",
        "a save id twice",
        "a blank party",
        "a party of 201 characters",
        "another payment",
        "a bad bill date",
        "no lines",
        "a missing line total",
        "a line total in tenths of paise",
        "a weight with four decimals",
        "a negative weight",
        "a wastage below -100",
        "pieces with a fraction",
        "a negative rate",
        "a blank item",
        "diamonds worth more than the line",
        "a diamond value in tenths of paise",
        "negative diamond carats",
        "a not-sent id that is also a bill",
        "a property twice",
        "not JSON",
        "JSON that is not an object",
    };

    [Theory]
    [MemberData(nameof(BadShapes))]
    public void A_document_of_the_wrong_shape_is_refused(string shape)
    {
        var json = BadDocument(shape);

        var read = HandoverLinkReader.Read(HandoverTestLinks.Signed(json), Key, Now);

        Assert.Equal(HandoverLinkReader.Unreadable, read.Problem);
    }

    [Fact]
    public void More_than_20_bills_or_100_lines_is_refused()
    {
        var bills = Enumerable.Range(0, 21).Select(_ => HandoverTestLinks.Bill(Guid.NewGuid(), "Buyer A", HandoverTestLinks.Line()));
        var tooManyBills = HandoverTestLinks.Document(Now, bills);
        var tooManyLines = HandoverTestLinks.Document(
            Now,
            [HandoverTestLinks.Bill(Guid.NewGuid(), "Buyer A", Enumerable.Range(0, 101).Select(_ => HandoverTestLinks.Line()).ToArray())]);
        var atTheLimits = HandoverTestLinks.Document(
            Now,
            Enumerable.Range(0, 20).Select(_ => HandoverTestLinks.Bill(Guid.NewGuid(), "Buyer A", Enumerable.Range(0, 100).Select(_ => HandoverTestLinks.Line()).ToArray())));

        Assert.Equal("This hand-over has more than 20 bills.", HandoverLinkReader.Read(HandoverTestLinks.Signed(tooManyBills), Key, Now).Problem);
        Assert.Equal("A bill in this hand-over has more than 100 lines.", HandoverLinkReader.Read(HandoverTestLinks.Signed(tooManyLines), Key, Now).Problem);
        Assert.True(HandoverLinkReader.Read(HandoverTestLinks.Signed(atTheLimits), Key, Now).IsAccepted);
    }

    [Fact]
    public void Optional_fields_may_be_left_out_and_a_negative_wastage_down_to_minus_100_is_read()
    {
        var document = HandoverTestLinks.VectorDocument(Now);
        var bill = document["bills"]![0]!.AsObject();
        bill.Remove("partyPhone");
        bill.Remove("partyAddress");
        document.Remove("notSent");
        var line = bill["lines"]![0]!.AsObject();
        line.Remove("karat");
        line["wastagePercent"] = -100;
        line["rate"] = null;

        var read = HandoverLinkReader.Read(HandoverTestLinks.Signed(document), Key, Now);

        Assert.Null(read.Problem);
        Assert.Empty(read.Document!.NotSent!);
        Assert.Null(read.Document.Bills[0].Lines[0].Karat);
        Assert.Null(read.Document.Bills[0].Lines[0].Rate);
        Assert.Equal(-100m, read.Document.Bills[0].Lines[0].WastagePercent);
        // A sender that does not give a line's diamonds sends none.
        Assert.Equal((0m, 0m), (read.Document.Bills[0].Lines[0].DiamondCarats, read.Document.Bills[0].Lines[0].DiamondValue));
    }

    [Fact]
    public void A_lines_diamonds_are_read()
    {
        var document = HandoverTestLinks.VectorDocument(Now);
        var line = document["bills"]![0]!["lines"]![0]!.AsObject();
        line["diamondCarats"] = 0.45m;
        line["diamondValue"] = 1000m;

        var read = HandoverLinkReader.Read(HandoverTestLinks.Signed(document), Key, Now);

        Assert.Null(read.Problem);
        Assert.Equal((0.45m, 1000m), (read.Document!.Bills[0].Lines[0].DiamondCarats, read.Document.Bills[0].Lines[0].DiamondValue));
    }

    [Fact]
    public void Only_an_argument_that_starts_with_the_scheme_is_taken_for_a_link()
    {
        Assert.True(HandoverLinkReader.IsHandoverArgument("tallywrapper:handover?d=x&s=y"));
        Assert.True(HandoverLinkReader.IsHandoverArgument("TALLYWRAPPER://handover"));
        Assert.False(HandoverLinkReader.IsHandoverArgument("--something"));
        Assert.False(HandoverLinkReader.IsHandoverArgument(null));
    }

    private static string BadDocument(string shape)
    {
        var document = HandoverTestLinks.VectorDocument(Now);
        var bill = document["bills"]![0]!.AsObject();
        var line = bill["lines"]![0]!.AsObject();
        switch (shape)
        {
            case "missing v": document.Remove("v"); break;
            case "v as text": document["v"] = "1"; break;
            case "missing issuedAt": document.Remove("issuedAt"); break;
            case "no bills": document["bills"] = new JsonArray(); break;
            case "bills not a list": document["bills"] = "none"; break;
            case "a bill that is null": document["bills"] = new JsonArray((JsonNode?)null); break;
            case "an empty save id": bill["saveId"] = Guid.Empty.ToString(); break;
            case "a save id twice": document["bills"]!.AsArray().Add(bill.DeepClone()); break;
            case "a blank party": bill["partyName"] = "  "; break;
            case "a party of 201 characters": bill["partyName"] = new string('B', 201); break;
            case "another payment": bill["payment"] = "UPI"; break;
            case "a bad bill date": bill["billDate"] = "25-10-2026"; break;
            case "no lines": bill["lines"] = new JsonArray(); break;
            case "a missing line total": line.Remove("lineTotal"); break;
            case "a line total in tenths of paise": line["lineTotal"] = 72569.305m; break;
            case "a weight with four decimals": line["netWeight"] = 10.3001m; break;
            case "a negative weight": line["lessWeight"] = -0.2m; break;
            case "a wastage below -100": line["wastagePercent"] = -100.5m; break;
            case "pieces with a fraction": line["pieces"] = 1.5m; break;
            case "a negative rate": line["rate"] = -1; break;
            case "a blank item": line["itemName"] = ""; break;
            case "diamonds worth more than the line": line["diamondCarats"] = 0.5m; line["diamondValue"] = 72569.31m; break;
            case "a diamond value in tenths of paise": line["diamondCarats"] = 0.5m; line["diamondValue"] = 1000.005m; break;
            case "negative diamond carats": line["diamondCarats"] = -0.5m; line["diamondValue"] = 1000m; break;
            case "a not-sent id that is also a bill": document["notSent"] = new JsonArray((JsonNode)bill["saveId"]!.GetValue<string>()); break;
            case "a property twice": return document.ToJsonString().Replace("\"payment\":\"Cash\"", "\"payment\":\"Cash\",\"payment\":\"Cash\"", StringComparison.Ordinal);
            case "not JSON": return "{v:1";
            case "JSON that is not an object": return "[1]";
            default: throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }

        return document.ToJsonString();
    }
}
