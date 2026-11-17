using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ShowroomBilling.Infrastructure.Persistence;

namespace ShowroomBilling.Infrastructure.Health;

/// <summary>What one check of the database found.</summary>
/// <param name="Reachable">Whether PostgreSQL answered.</param>
/// <param name="Identity">The database's own <c>environment</c> marker; null when it did not answer or has none.</param>
/// <param name="PendingMigrationCount">Migrations of this build the database has not had; the last known count when it could not be asked.</param>
public sealed record DatabaseHealthReading(
    bool Reachable,
    string? Identity,
    int? PendingMigrationCount);

/// <summary>
/// The health of the database this process runs against: does it answer, which database is it, and
/// has it had this build's migrations. One instance per API process.
/// <para>
/// <b>Every answer is a real check</b>, kept for <see cref="TimeToLive"/>. Every counter and the server
/// tray ask every 15 seconds; with this they share one check, and callers that arrive while one is
/// out wait for that one and do not start their own.
/// </para>
/// <para>
/// <b>"Reachable" alone is not health.</b> A build on a database that is behind answers every bill and
/// numbering request with an error while PostgreSQL is perfectly reachable. So the number of pending
/// migrations is part of each check, is recorded by the start-up migration too
/// (<see cref="RecordPendingMigrations"/>), and the last count found is what is reported while the
/// database does not answer and what the stuck-posting sweep looks at before it reads a bill.
/// </para>
/// <para>
/// The check is plain SQL on a connection of its own. Through EF each statement would be written to
/// the log at Information, which at this rate is megabytes a day of the same two lines.
/// </para>
/// </summary>
public sealed class DatabaseHealth(
    NpgsqlDataSource dataSource,
    ILogger<DatabaseHealth> logger,
    TimeProvider? timeProvider = null)
{
    // How long the database gets to answer. The wait is ended here and not left to Npgsql: on a
    // connection that has gone silent (the link dropped, so nothing refuses and nothing resets)
    // Npgsql answers a timeout by sending a cancel request on a second connection and waiting for
    // the server to act on it, for as long as the connection's command timeout. Measured: 32 seconds
    // for a 2-second timeout, longer than a Desktop waits for health at all.
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TimeToLive = TimeSpan.FromSeconds(5);
    private const int Unknown = -1;

    // This build's migrations, read from the assembly. No database is asked.
    private static readonly Lazy<string[]> ThisBuildsMigrations = new(static () =>
    {
        var options = new DbContextOptionsBuilder<ShowroomBillingDbContext>()
            .UseNpgsql(npgsql => npgsql.SetPostgresVersion(18, 0))
            .Options;
        using var db = new ShowroomBillingDbContext(options);
        return db.Database.GetMigrations().ToArray();
    });

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private Task<DatabaseHealthReading>? _check;
    private DateTimeOffset _validUntilUtc;
    private bool? _answeredLastTime;
    private int _pending = Unknown;

    /// <summary>
    /// How many of this build's migrations the database had not had when it was last asked.
    /// Null: not asked yet. Zero: up to date. In memory only.
    /// </summary>
    public int? PendingMigrationCount =>
        Volatile.Read(ref _pending) is var pending && pending == Unknown ? null : pending;

    /// <summary>For the start-up migration, which learns the count before any health check has run.</summary>
    public void RecordPendingMigrations(int pendingMigrationCount) =>
        Volatile.Write(ref _pending, Math.Max(0, pendingMigrationCount));

    /// <summary>The last check if it is still fresh, else a new one, shared with whoever else is asking.</summary>
    public Task<DatabaseHealthReading> GetAsync(CancellationToken cancellationToken = default)
    {
        Task<DatabaseHealthReading> check;
        lock (_gate)
        {
            if (_check is null || (_check.IsCompleted && _timeProvider.GetUtcNow() >= _validUntilUtc))
            {
                // Not tied to this caller: one that gives up must not fail the check for the others.
                _check = Task.Run(CheckAndKeepAsync);
            }

            check = _check;
        }

        return check.WaitAsync(cancellationToken);
    }

    /// <summary>One real check, not kept. <see cref="GetAsync"/> is what callers share.</summary>
    public async Task<DatabaseHealthReading> CheckAsync() => (await ProbeAsync()).Reading;

    private async Task<DatabaseHealthReading> CheckAndKeepAsync()
    {
        var (reading, problem, cause) = await ProbeAsync();

        bool? answeredBefore;
        lock (_gate)
        {
            answeredBefore = _answeredLastTime;
            _answeredLastTime = reading.Reachable;
            _validUntilUtc = _timeProvider.GetUtcNow().Add(TimeToLive);
        }

        // One line when the database stops answering and one when it answers again, not one per
        // check: during an outage that would be an error every few seconds.
        if (answeredBefore != reading.Reachable)
        {
            if (!reading.Reachable)
            {
                logger.LogWarning(cause, "The database is not answering. {Description}", problem);
            }
            else if (answeredBefore is not null)
            {
                logger.LogWarning("The database is answering again.");
            }
        }

        return reading;
    }

    private async Task<(DatabaseHealthReading Reading, string? Problem, Exception? Cause)> ProbeAsync()
    {
        // On a thread of its own, so that the budget below covers all of it. Called directly, the
        // attempt runs on this thread until it first has to wait, and with a pooled connection that
        // can be far in: a connection whose keep-alive is waiting on a silent line makes the next
        // command on it wait too, without yielding. Measured on a freshly started server: the first
        // two checks of a silence took more than 12 seconds, and health with them.
        var attempt = Task.Run(AskAsync);
        try
        {
            var (identity, applied) = await attempt.WaitAsync(Budget);
            if (applied is not null)
            {
                RecordPendingMigrations(ThisBuildsMigrations.Value.Except(applied, StringComparer.Ordinal).Count());
            }

            return (new DatabaseHealthReading(true, identity, PendingMigrationCount), null, null);
        }
        catch (Exception exception)
        {
            // An attempt left behind (too slow) ends when Npgsql gives up on it. Nobody is waiting
            // for that failure.
            _ = attempt.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);

            var problem = exception is TimeoutException
                ? $"PostgreSQL did not answer within {Budget.TotalSeconds:0} seconds."
                : "PostgreSQL is unreachable.";
            return (new DatabaseHealthReading(false, null, PendingMigrationCount), problem, exception);
        }
    }

    /// <summary>
    /// Opens a connection and asks the two questions. An error PostgreSQL itself sends back is an
    /// answer: the database is reachable, and only that question has none.
    /// </summary>
    /// <returns>
    /// The identity marker, and the migrations the database has had: empty when it has no history
    /// table (a database nothing has been applied to), null when it would not say.
    /// </returns>
    private async Task<(string? Identity, string[]? Applied)> AskAsync()
    {
        using var cts = new CancellationTokenSource(Budget);
        await using var connection = await dataSource.OpenConnectionAsync(cts.Token);

        string[]? applied;
        try
        {
            applied = await ListAsync(connection, """SELECT "MigrationId" FROM "__EFMigrationsHistory" """, cts.Token);
        }
        catch (PostgresException exception)
        {
            applied = exception.SqlState == PostgresErrorCodes.UndefinedTable ? [] : null;
        }

        string? identity;
        try
        {
            identity = (await ListAsync(
                connection, "SELECT value FROM public.database_identity WHERE key = 'environment'", cts.Token)).FirstOrDefault();
        }
        catch (PostgresException)
        {
            identity = null;
        }

        return (identity, applied);
    }

    private static async Task<string[]> ListAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.CommandTimeout = (int)Budget.TotalSeconds;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }
}
