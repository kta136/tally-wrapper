using System.Net;
using System.Net.Http;
using ShowroomBilling.ServerTray;
using ShowroomBilling.ServerTray.Ui;

namespace ShowroomBilling.Desktop.Tests.ServerTray;

/// <summary>What the server dashboard tells the operator after a maintenance call.</summary>
public sealed class StatusFormReportTests
{
    [Fact]
    public void A_success_answer_shows_its_message()
    {
        var report = Report(HttpStatusCode.OK, """{"success":true,"message":"Connection succeeded. Database identity: PROD."}""");

        Assert.Equal(Tone.Ok, report.Tone);
        Assert.StartsWith("Test database succeeded.", report.Title);
        Assert.EndsWith("Connection succeeded. Database identity: PROD.", report.Details);
    }

    [Fact]
    public void An_answer_that_says_it_failed_is_an_error_even_with_a_200()
    {
        var report = Report(HttpStatusCode.OK, """{"success":false,"message":"Connection failed: timeout"}""");

        Assert.Equal(Tone.Err, report.Tone);
        Assert.StartsWith("Test database failed.", report.Title);
        Assert.EndsWith("Connection failed: timeout", report.Details);
    }

    [Theory]
    [InlineData("""{"title":"Bad Request","detail":"PostgreSQL connection string is required.","status":400}""", "Bad Request: PostgreSQL connection string is required.")]
    [InlineData("""{"detail":"Only detail."}""", "Only detail.")]
    [InlineData("""{"title":"Only title."}""", "Only title.")]
    [InlineData("""{"message":"Only message."}""", "Only message.")]
    [InlineData("<html>bad gateway</html>", "<html>bad gateway</html>")]
    [InlineData("", "")]
    public void A_refusal_shows_the_servers_words_or_the_raw_body(string body, string expected)
    {
        var report = Report(HttpStatusCode.BadRequest, body);

        Assert.Equal(Tone.Err, report.Tone);
        Assert.Equal("Test database failed: HTTP 400", report.Title);
        Assert.Equal(expected, report.Details);
    }

    [Fact]
    public void A_saved_connection_shows_what_was_stored_with_its_password_hidden_and_what_happens_next()
    {
        var report = Report(
            HttpStatusCode.OK,
            """{"provider":"PostgreSQL","maskedConnectionString":"Host=db;Database=sample_billing;Password=***","requiresApiRestart":true}""");

        Assert.Equal(Tone.Ok, report.Tone);
        Assert.Equal(
            "Stored: Host=db;Database=sample_billing;Password=***"
            + "\r\n\r\nThe server starts using it when the Windows service restarts.",
            report.Details);
    }

    [Fact]
    public void A_success_with_some_other_body_shows_the_body()
    {
        var report = Report(HttpStatusCode.OK, """{"provider":"PostgreSQL"}""");

        Assert.Equal(Tone.Ok, report.Tone);
        Assert.EndsWith("""{"provider":"PostgreSQL"}""", report.Details);
    }

    private static TrayMessage Report(HttpStatusCode status, string body)
    {
        using var response = new HttpResponseMessage(status);
        return StatusForm.BuildOperationReport("Test database", response, body);
    }
}
