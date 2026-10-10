using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Api.Middleware;
using ShowroomBilling.Application;
using ShowroomBilling.Application.Admin;
using ShowroomBilling.Application.Bills;

namespace ShowroomBilling.Tests;

public sealed class DomainExceptionHandlerTests
{
    public static TheoryData<Exception, int, string> KnownExceptions => new()
    {
        { new BillNotFoundException(Guid.Empty), StatusCodes.Status404NotFound, "Bill not found" },
        { new BillStateConflictException("busy"), StatusCodes.Status409Conflict, "Bill state conflict" },
        { new BillValidationException(["bad total"]), StatusCodes.Status400BadRequest, "Bill payload invalid" },
        { new TallyPreflightUnavailableException("down"), StatusCodes.Status503ServiceUnavailable, "Tally unavailable" },
        { new DbUpdateConcurrencyException("stale"), StatusCodes.Status409Conflict, "Concurrent change" },
        { new AdminPasscodeInvalidException(), StatusCodes.Status401Unauthorized, "Admin passcode invalid" },
        { new RequestValidationException("Port must be between 1 and 65535."), StatusCodes.Status400BadRequest, "Bad request" },
        { new ArgumentException("bad", "request"), StatusCodes.Status400BadRequest, "Bad request" },
    };

    [Theory]
    [MemberData(nameof(KnownExceptions))]
    public async Task Known_exceptions_map_to_their_status_and_title(Exception exception, int status, string title)
    {
        var (handled, context, problem) = await HandleAsync(exception);

        Assert.True(handled);
        Assert.Equal(status, context.Response.StatusCode);
        Assert.Equal(status, problem!.Status);
        Assert.Equal(title, problem.Title);
    }

    [Fact]
    public async Task A_validation_message_reaches_the_operator_unchanged()
    {
        var (_, _, problem) = await HandleAsync(new RequestValidationException("Print.PrintFontSize must be between 8 and 18."));

        Assert.Equal("Print.PrintFontSize must be between 8 and 18.", problem!.Detail);
    }

    [Fact]
    public async Task The_parameter_name_dotnet_appends_is_not_shown_to_the_operator()
    {
        var (_, _, problem) = await HandleAsync(new ArgumentException("Layout is required.", "request"));

        Assert.Equal("Layout is required.", problem!.Detail);
    }

    [Fact]
    public async Task A_concurrency_conflict_gets_an_explanation_instead_of_the_provider_message()
    {
        var (_, _, problem) = await HandleAsync(new DbUpdateConcurrencyException("The database operation was expected to affect 1 row(s)"));

        Assert.StartsWith("This record was changed by another counter", problem!.Detail);
    }

    [Fact]
    public async Task A_throttled_unlock_tells_the_caller_when_to_retry()
    {
        var (_, context, problem) = await HandleAsync(new AdminUnlockThrottledException(TimeSpan.FromSeconds(3.2)));

        Assert.Equal(StatusCodes.Status429TooManyRequests, problem!.Status);
        Assert.Equal("4", context.Response.Headers["Retry-After"].ToString());
    }

    /// <summary>
    /// 10 October 2026: a new build ran against a database that had not had its
    /// migrations. Every bill request ended in a bare 500 and nobody could tell why.
    /// </summary>
    [Theory]
    [InlineData("42703")] // undefined column
    [InlineData("42P01")] // undefined table
    public async Task Sql_that_names_a_missing_column_or_table_says_the_database_needs_its_migrations(string sqlState)
    {
        var rejected = new Npgsql.PostgresException("column b.InvoiceCore does not exist", "ERROR", "ERROR", sqlState);

        var (handled, context, problem) = await HandleAsync(rejected);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, context.Response.StatusCode);
        Assert.Equal("Database upgrade needed", problem!.Title);
        Assert.Equal(DomainExceptionHandler.SchemaBehindDetail, problem.Detail);
        // The operator gets what to do, not PostgreSQL's wording.
        Assert.DoesNotContain("InvoiceCore", problem.Detail);
    }

    [Fact]
    public async Task The_same_holds_when_a_save_wraps_it()
    {
        var rejected = new DbUpdateException(
            "An error occurred while saving the entity changes.",
            new Npgsql.PostgresException("column \"TotalsJson\" does not exist", "ERROR", "ERROR", "42703"));

        var (handled, _, problem) = await HandleAsync(rejected);

        Assert.True(handled);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem!.Status);
    }

    [Fact]
    public async Task Other_database_errors_are_not_called_a_missing_upgrade()
    {
        var (handled, _, _) = await HandleAsync(
            new Npgsql.PostgresException("duplicate key value violates unique constraint", "ERROR", "ERROR", "23505"));

        Assert.False(handled);
    }

    [Fact]
    public async Task An_unknown_exception_is_left_for_the_default_handler()
    {
        var (handled, _, problem) = await HandleAsync(new InvalidOperationException("bug"));

        Assert.False(handled);
        Assert.Null(problem);
    }

    private static async Task<(bool Handled, HttpContext Context, ProblemDetails? Problem)> HandleAsync(Exception exception)
    {
        var writer = new CapturingProblemDetailsService();
        var handler = new DomainExceptionHandler(writer);
        var context = new DefaultHttpContext();

        var handled = await handler.TryHandleAsync(context, exception, CancellationToken.None);

        return (handled, context, writer.Written);
    }

    private sealed class CapturingProblemDetailsService : IProblemDetailsService
    {
        public ProblemDetails? Written { get; private set; }

        public ValueTask WriteAsync(ProblemDetailsContext context)
        {
            Written = context.ProblemDetails;
            return ValueTask.CompletedTask;
        }

        public ValueTask<bool> TryWriteAsync(ProblemDetailsContext context)
        {
            Written = context.ProblemDetails;
            return ValueTask.FromResult(true);
        }
    }
}
