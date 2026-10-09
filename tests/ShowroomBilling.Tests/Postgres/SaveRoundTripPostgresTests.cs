using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class SaveRoundTripPostgresTests(PostgresFixture fixture)
{
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task CreateDraft_SavesInOneBatchedWriteAfterItsLookups()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        await using (var warm = database.CreateContext())
        {
            await PostgresBillTestSupport.BuildService(warm)
                .CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("warm", 100m), Guid.NewGuid()));
        }

        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>(database.Options)
            .AddInterceptors(counter)
            .Options;
        await using var db = new ShowroomBillingDbContext(options);
        await PostgresBillTestSupport.BuildService(db)
            .CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("A", 100m), Guid.NewGuid()));

        // op-id lookup, reservation-key lookup, sequence FOR UPDATE, numbering
        // settings, occupancy window, one batched INSERT. The commit is extra.
        Assert.True(
            counter.Commands.Count <= 6,
            $"Save used {counter.Commands.Count} commands: " + string.Join(" | ", counter.Commands.Select(c => c.Trim().Split((char)10)[0])));
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
