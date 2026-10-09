using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Bills;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.CompiledModels;

namespace ShowroomBilling.Tests.Postgres;

[Collection(PostgresCollection.Name)]
public sealed class CompiledModelPostgresTests(PostgresFixture fixture)
{
    /// <summary>
    /// DatabaseInitializationHostedService migrates through the DI context,
    /// which uses the precompiled model.
    /// </summary>
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task Migrate_WorksThroughAContextOnThePrecompiledModel()
    {
        await using var database = await fixture.CreateDatabaseAtMigrationAsync("20261009153701_BillRowVersion");
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>(database.Options)
            .UseModel(ShowroomBillingDbContextModel.Instance)
            .Options;
        await using var db = new ShowroomBillingDbContext(options);

        Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    /// <summary>The API runs on the precompiled model; exercise the bill workflow through it.</summary>
    [PostgresFact]
    [Trait("Category", "Postgres")]
    public async Task BillWorkflow_RunsOnThePrecompiledModel()
    {
        await using var database = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>(database.Options)
            .UseModel(ShowroomBillingDbContextModel.Instance)
            .Options;
        await using var db = new ShowroomBillingDbContext(options);
        var service = PostgresBillTestSupport.BuildService(db);

        var created = await service.CreateDraftAsync(new CreateBillDraftRequest(null, PostgresBillTestSupport.SamplePayload("Compiled", 100m), Guid.NewGuid()));
        var updated = await service.UpdateDraftAsync(created.Id, new UpdateBillDraftRequest(PostgresBillTestSupport.SamplePayload("Compiled", 150m), created.CurrentRevisionId));
        var pushed = await service.PushAsync(created.Id, new PushBillRequest("push"));
        var found = await service.SearchAsync(new BillSearchFilter(null, null, null, 0, 10, null, Search: "compiled"));

        Assert.Equal(2, updated.CurrentRevision!.RevisionNo);
        Assert.Equal(BillStates.Posted, pushed.State);
        Assert.Equal(created.Id, Assert.Single(found.Items).Id);
    }
}
