using System.Text.Json;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.ViewModels.Bills;

namespace ShowroomBilling.Desktop.Tests;

public sealed class BillAuditTrailMapperTests
{
    public static TheoryData<string> ServerEvents => [.. BillAuditEvents.All];

    [Theory]
    [MemberData(nameof(ServerEvents))]
    public void Every_event_the_server_writes_has_a_title(string eventType)
    {
        Assert.False(string.IsNullOrWhiteSpace(BillAuditTrailMapper.TitleFor(eventType)));
    }

    [Fact]
    public void The_shared_list_names_every_event_constant()
    {
        var constants = typeof(BillAuditEvents)
            .GetFields()
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(name => name, StringComparer.Ordinal);

        Assert.Equal(constants, BillAuditEvents.All.OrderBy(name => name, StringComparer.Ordinal));
    }

    [Fact]
    public void An_event_from_a_newer_server_still_gets_a_readable_title()
    {
        var item = BillAuditTrailMapper.Map(Event("bill.something.new", """{"state":"pending"}"""));

        Assert.Equal("Bill Something New", item.Title);
    }

    [Fact]
    public void A_tally_rejection_shows_its_code_and_message()
    {
        var item = BillAuditTrailMapper.Map(Event(
            BillAuditEvents.TallyFailed,
            """{"state":"failed","details":{"errorCode":"LINEERROR","errorMessage":"Ledger missing"}}"""));

        Assert.Equal("Tally rejected the voucher", item.Title);
        Assert.Equal("LINEERROR: Ledger missing", item.Detail);
        Assert.Equal("err", item.Tone);
    }

    [Theory]
    [InlineData("""{"state":"reconciliation_required","details":{"reason":"API stopped mid-post"}}""")]
    [InlineData("""{"reason":"API stopped mid-post"}""")]
    public void A_recovery_shows_its_reason_in_both_payload_shapes(string payload)
    {
        var item = BillAuditTrailMapper.Map(Event(BillAuditEvents.PostingRecovered, payload));

        Assert.Equal("Stuck push recovered", item.Title);
        Assert.Equal("API stopped mid-post", item.Detail);
        Assert.Equal("warn", item.Tone);
    }

    private static BillAuditEventDto Event(string eventType, string payloadJson)
    {
        using var document = JsonDocument.Parse(payloadJson);
        return new BillAuditEventDto(
            Guid.NewGuid(),
            eventType,
            "system",
            null,
            "bill",
            Guid.NewGuid().ToString(),
            document.RootElement.Clone(),
            DateTimeOffset.UtcNow);
    }
}
