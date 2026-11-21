using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Settings;

/// <summary>
/// The one row of shared settings: which row that is, and how the first one comes to be.
/// </summary>
internal static class CloudSettingsRow
{
    /// <summary>
    /// The settings rows with the one in use first. A database first used by a build from before
    /// 21 November 2026 can hold more than one (two requests that found none each added a row),
    /// so every read and write of the settings takes its row through this, and they agree on
    /// it: the newest, and of two that carry the same time the one with the higher id.
    /// </summary>
    internal static IOrderedQueryable<CloudSettingsEntity> InUse(this IQueryable<CloudSettingsEntity> rows) =>
        rows.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id);

    /// <summary>
    /// Adds the first settings row and the audit event that says so, unless a row is already
    /// stored under its id (<see cref="CloudSettingsEntity.FirstSettingsId"/>). Of two callers
    /// that both found no settings, the second waits for the first transaction to end and then
    /// adds neither. Returns whether this caller added them.
    /// <para>
    /// It is one statement, so the row and its event are stored together or not at all, in the
    /// caller's transaction when it has one, and nothing else the context holds is saved with
    /// them. EF has no insert that gives way to a row already there, so the statement is built
    /// from the model: a column added to either table is written without being named here.
    /// </para>
    /// </summary>
    internal static async Task<bool> AddFirstAsync(
        ShowroomBillingDbContext dbContext,
        CloudSettingsEntity row,
        AuditEventEntity seeded,
        CancellationToken cancellationToken)
    {
        var arguments = new List<object?>();
        var settings = InsertOf(dbContext, row, arguments);
        var audit = InsertOf(dbContext, seeded, arguments);

        // What is put into the text is the model's own table names, column names and column
        // types; every value travels as a parameter.
#pragma warning disable EF1002
        var added = await dbContext.Database.ExecuteSqlRawAsync(
            $"""
             WITH added AS (
                 INSERT INTO {settings.Table} ({settings.Columns})
                 VALUES ({settings.Values})
                 ON CONFLICT DO NOTHING
                 RETURNING 1)
             INSERT INTO {audit.Table} ({audit.Columns})
             SELECT {audit.Values} FROM added
             """,
            arguments!,
            cancellationToken);
#pragma warning restore EF1002
        return added > 0;
    }

    /// <summary>
    /// The table, the columns and the values of an insert of <paramref name="entity"/>, with its
    /// values added to <paramref name="arguments"/>. Each value is cast to its column's type: a
    /// parameter carries text for a jsonb column, and no type at all for a null.
    /// </summary>
    private static (string Table, string Columns, string Values) InsertOf(
        ShowroomBillingDbContext dbContext,
        object entity,
        List<object?> arguments)
    {
        var entityType = dbContext.Model.FindEntityType(entity.GetType())
            ?? throw new InvalidOperationException($"{entity.GetType().Name} is not part of the model.");
        var table = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)
            ?? throw new InvalidOperationException($"{entity.GetType().Name} is not mapped to a table.");
        var sql = dbContext.GetService<ISqlGenerationHelper>();

        var columns = new List<string>();
        var values = new List<string>();
        foreach (var property in entityType.GetProperties())
        {
            columns.Add(sql.DelimitIdentifier(property.GetColumnName(table)!));
            values.Add($"CAST({{{arguments.Count}}} AS {property.GetColumnType(table)})");
            arguments.Add(property.GetGetter().GetClrValue(entity));
        }

        return (sql.DelimitIdentifier(table.Name, table.Schema), string.Join(", ", columns), string.Join(", ", values));
    }
}
