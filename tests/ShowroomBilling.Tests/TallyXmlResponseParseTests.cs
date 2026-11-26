using System.Xml;
using System.Xml.Linq;
using ShowroomBilling.Infrastructure.Tally;

namespace ShowroomBilling.Tests;

/// <summary>
/// How Tally's answers are read. Tally writes user-defined fields with a <c>UDF:</c> prefix it
/// never declares; a strict parser refused a whole year's voucher list for that
/// ("'UDF' is an undeclared prefix") on 26 November 2026.
/// </summary>
public sealed class TallyXmlResponseParseTests
{
    private const string VoucherWithUdfFields = """
        <ENVELOPE>
          <HEADER><VERSION>1</VERSION><STATUS>1</STATUS></HEADER>
          <BODY><DATA><COLLECTION>
            <VOUCHER REMOTEID="x" VCHTYPE="Sales">
              <DATE TYPE="Date">20260415</DATE>
              <MASTERID>4521</MASTERID>
              <VOUCHERNUMBER>SR/1</VOUCHERNUMBER>
              <UDF:PARTYREF.LIST DESC="`PartyRef`" ISLIST="YES" TYPE="String" INDEX="1001">
                <UDF:PARTYREF DESC="`PartyRef`">A-1</UDF:PARTYREF>
              </UDF:PARTYREF.LIST>
            </VOUCHER>
          </COLLECTION></DATA></BODY>
        </ENVELOPE>
        """;

    [Fact]
    public void Undeclared_UDF_fields_are_read_as_Tallys_own_namespace_and_the_rest_is_untouched()
    {
        var parsed = TallyXmlClient.ParseResponse(VoucherWithUdfFields);

        var voucher = parsed.Descendants("VOUCHER").Single();
        Assert.Equal("SR/1", voucher.Element("VOUCHERNUMBER")!.Value);
        Assert.Equal("4521", voucher.Element("MASTERID")!.Value);
        var udf = voucher.Elements().Single(e => e.Name.LocalName == "PARTYREF.LIST");
        Assert.Equal("TallyUDF", udf.Name.NamespaceName);
        Assert.Equal("A-1", udf.Value);
    }

    [Fact]
    public void Any_other_undeclared_prefix_on_an_element_or_an_attribute_is_read_too()
    {
        var parsed = TallyXmlClient.ParseResponse(
            """<ENVELOPE><ABC:FIELD XYZ:FLAG="Yes">1</ABC:FIELD><NAME>Ok</NAME></ENVELOPE>""");

        Assert.Equal("Ok", parsed.Element("NAME")!.Value);
        var field = parsed.Elements().Single(e => e.Name.LocalName == "FIELD");
        Assert.Equal("Yes", field.Attributes().Single(a => a.Name.LocalName == "FLAG").Value);
    }

    [Fact]
    public void An_answer_that_declares_the_prefix_itself_keeps_its_own_declaration()
    {
        var parsed = TallyXmlClient.ParseResponse(
            """<ENVELOPE xmlns:UDF="urn:declared"><UDF:FIELD>1</UDF:FIELD></ENVELOPE>""");

        Assert.Equal("urn:declared", parsed.Elements().Single().Name.NamespaceName);
    }

    [Fact]
    public void Whitespace_between_elements_is_left_out_as_before()
    {
        var parsed = TallyXmlClient.ParseResponse("<A>\n  <B>1</B>\n</A>");

        Assert.Single(parsed.Nodes());
    }

    [Fact]
    public void A_document_type_declaration_is_refused()
    {
        Assert.Throws<XmlException>(() => TallyXmlClient.ParseResponse(
            """<!DOCTYPE A [<!ENTITY e "x">]><A>&e;</A>"""));
    }

    [Fact]
    public void Text_that_merely_has_a_colon_in_it_is_left_alone()
    {
        var parsed = TallyXmlClient.ParseResponse("<A><NARRATION>Paid at 10:30 note:cash</NARRATION></A>");

        Assert.Equal("Paid at 10:30 note:cash", parsed.Element("NARRATION")!.Value);
    }
}
