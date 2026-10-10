using System.Net;
using System.Net.Http;
using System.Text;
using ShowroomBilling.Contracts.Admin;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Desktop.Services;

namespace ShowroomBilling.Desktop.Tests;

/// <summary>
/// The request shape every typed API client shares: how a call is sent, how a
/// failure becomes a message, and what a view model shows for it.
/// </summary>
public sealed class ApiClientTests
{
    [Fact]
    public async Task A_post_sends_the_body_as_camel_case_json_and_reads_the_answer()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"status":"posted","message":"ok"}""");
        using var http = NewClient(handler);

        var result = await http.PostApiAsync<Answer>("/api/thing", new { BillNumber = 7 }, CancellationToken.None);

        Assert.Equal(new Answer("posted", "ok"), result);
        Assert.Equal(HttpMethod.Post, handler.Method);
        Assert.Equal("""{"billNumber":7}""", handler.Body);
        Assert.False(handler.Headers!.Contains(AdminTokenConstants.HeaderName));
    }

    [Fact]
    public async Task An_admin_call_carries_the_admin_token()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"status":"deleted","message":null}""");
        using var http = NewClient(handler);

        await http.SendApiAsync<Answer>(HttpMethod.Delete, "/api/thing/1", new { Reason = "dup" }, CancellationToken.None, "token-1");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal(["token-1"], handler.Headers!.GetValues(AdminTokenConstants.HeaderName));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NoContent)]
    public async Task An_optional_read_treats_a_missing_value_as_null(HttpStatusCode status)
    {
        using var http = NewClient(new RecordingHandler(status, ""));

        Assert.Null(await http.GetApiOrNullAsync<Answer>("/api/thing/1", CancellationToken.None));
    }

    [Fact]
    public async Task A_refusal_carries_the_servers_own_words()
    {
        var problem = """{"title":"Conflict","detail":"Bill is already posted.","status":409}""";
        using var http = NewClient(new RecordingHandler(HttpStatusCode.Conflict, problem));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => http.GetApiAsync<Answer>("/api/thing/1", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Equal("Bill is already posted.", ex.Message);
        Assert.Equal("Bill is already posted.", ApiResponseReader.FormatError(ex));
    }

    [Fact]
    public async Task A_refusal_without_a_readable_body_falls_back_to_the_status()
    {
        using var http = NewClient(new RecordingHandler(HttpStatusCode.BadGateway, "<html>gateway</html>"));

        var ex = await Assert.ThrowsAsync<ApiException>(
            () => http.GetApiAsync<Answer>("/api/thing/1", CancellationToken.None));

        Assert.Equal("HTTP 502 BadGateway", ex.Message);
    }

    [Fact]
    public async Task A_success_with_an_empty_body_is_an_error_not_a_null()
    {
        using var http = NewClient(new RecordingHandler(HttpStatusCode.OK, "null"));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => http.GetApiAsync<Answer>("/api/thing/1", CancellationToken.None));
    }

    [Fact]
    public void A_view_model_gets_one_message_for_any_failure()
    {
        Assert.Equal("network error", ApiResponseReader.FormatError(new HttpRequestException("No such host is known.")));
        Assert.Equal(
            "ServiceUnavailable",
            ApiResponseReader.FormatError(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("Rate is required.", ApiResponseReader.FormatError(new InvalidOperationException("Rate is required.")));
    }

    [Fact]
    public async Task The_bills_client_reads_a_missing_bill_as_null()
    {
        var handler = new RecordingHandler(HttpStatusCode.NotFound, "");
        using var http = NewClient(handler);
        var billId = Guid.NewGuid();

        Assert.Null(await new BillsApiClient(http).GetAsync(billId));
        Assert.Equal($"/api/bills/{billId}", handler.Path);
    }

    [Fact]
    public async Task The_bills_client_sends_a_delete_with_its_reason_and_the_admin_token()
    {
        var billId = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""{"billId":"{{billId}}","deleted":true}""");
        using var http = NewClient(handler);

        await new BillsApiClient(http).DeleteAsync(billId, new DeleteBillRequest("entered twice"), "token-2");

        Assert.Equal(HttpMethod.Delete, handler.Method);
        Assert.Equal($"/api/bills/{billId}", handler.Path);
        Assert.Contains("entered twice", handler.Body);
        Assert.Equal(["token-2"], handler.Headers!.GetValues(AdminTokenConstants.HeaderName));
    }

    private static HttpClient NewClient(HttpMessageHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost:5107") };

    private sealed record Answer(string Status, string? Message);

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public string? Path { get; private set; }
        public string? Body { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? Headers { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Path = request.RequestUri!.AbsolutePath;
            Headers = request.Headers;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
