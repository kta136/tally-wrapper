using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.CompiledModels;

namespace ShowroomBilling.Tests;

public sealed class CompiledModelTests
{
    /// <summary>
    /// The API runs on the precompiled model. If OnModelCreating changes and
    /// `dotnet ef dbcontext optimize` is not re-run, this fails.
    /// </summary>
    [Fact]
    public void Compiled_model_matches_the_model_built_from_OnModelCreating()
    {
        using var built = new ShowroomBillingDbContext(Options(compiled: false));
        using var compiled = new ShowroomBillingDbContext(Options(compiled: true));

        Assert.Equal(Describe(built.Model), Describe(compiled.Model));
    }

    private static DbContextOptions<ShowroomBillingDbContext> Options(bool compiled)
    {
        var builder = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseNpgsql("Host=localhost;Database=compiled_model_check", npgsql => npgsql.SetPostgresVersion(18, 0));
        if (compiled)
        {
            builder.UseModel(ShowroomBillingDbContextModel.Instance);
        }
        return builder.Options;
    }

    private static string[] Describe(IModel model) =>
        model.GetEntityTypes()
            .SelectMany(entity =>
            {
                var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
                return entity.GetProperties()
                    .Select(p => $"{entity.Name} column {p.GetColumnName(table)} {p.GetColumnType()} null={p.IsColumnNullable(table)} token={p.IsConcurrencyToken}")
                    .Concat(entity.GetIndexes().Select(i => $"{entity.Name} index {i.GetDatabaseName(table)} unique={i.IsUnique} filter={i.GetFilter()}"))
                    .Concat(entity.GetKeys().Select(k => $"{entity.Name} key {k.GetName(table)}"));
            })
            .Order(StringComparer.Ordinal)
            .ToArray();
}
