using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Application;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Numbering;

/// <summary>An invoice number taken from a fiscal year's sequence.</summary>
public sealed record ReservedNumber(string FiscalYear, long ReservedValue, string FormattedNumber);

public sealed class NumberingService(ShowroomBillingDbContext dbContext)
{
    public const string DocumentTypeSalesInvoice = "sales_invoice";

    private const int MaxReservationAttempts = 3;

    public async Task<NumberingPreviewResponse> GetPreviewAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = NormalizeDocumentType(documentType);
        var year = NormalizeFiscalYear(fiscalYear);
        var showroomId = Showrooms.DefaultId;

        var sequence = await dbContext.InvoiceSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ShowroomId == showroomId && x.FiscalYear == year && x.DocumentType == normalizedType,
                cancellationToken);

        var (prefix, suffix, padding) = await GetEffectivePrefixSuffixAsync(cancellationToken);
        var previewValue = await ComputeNextFreeCoreAsync(
            showroomId, year, sequence?.NextValue ?? 1L, CoreKeptForTally(sequence), cancellationToken);
        var formatted = InvoiceNumberFormatter.Format(prefix, suffix, previewValue, year, padding);

        return new NumberingPreviewResponse(
            ShowroomId: showroomId,
            FiscalYear: year,
            DocumentType: normalizedType,
            PreviewValue: previewValue,
            FormattedNumber: formatted,
            Prefix: prefix,
            Suffix: suffix);
    }

    /// <summary>
    /// Hands out the next free invoice number of the fiscal year (the current
    /// one when <paramref name="fiscalYear"/> is null) and moves the sequence
    /// past it. Inside a caller's transaction the number is only taken when
    /// that transaction commits, together with the bill it is for, and the
    /// caller must call <c>SaveChangesAsync</c>: the sequence is saved with
    /// its rows.
    /// </summary>
    public async Task<ReservedNumber> ReserveAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = NormalizeDocumentType(documentType);
        var year = NormalizeFiscalYear(fiscalYear);
        var showroomId = Showrooms.DefaultId;

        for (var attempt = 1; attempt <= MaxReservationAttempts; attempt++)
        {
            try
            {
                return await ReserveCoreAsync(normalizedType, year, showroomId, cancellationToken);
            }
            catch (DbUpdateException ex) when (IsRetryableReservationConflict(ex) && attempt < MaxReservationAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Invoice number reservation retry loop exited unexpectedly.");
    }

    private async Task<ReservedNumber> ReserveCoreAsync(
        string normalizedType,
        string year,
        Guid showroomId,
        CancellationToken cancellationToken)
    {
        var ambientTransaction = dbContext.Database.CurrentTransaction;

        // If the caller already opened a transaction, enlist in it and leave
        // commit responsibility to them. Otherwise own the txn here.
        await using var transaction = ambientTransaction is not null
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var sequence = await NumberingSequenceReclaimer.LoadSequenceForUpdateAsync(
            dbContext, showroomId, year, normalizedType, cancellationToken);

        var (prefix, suffix, padding) = await GetEffectivePrefixSuffixAsync(cancellationToken);

        // Forward-skip past any core a bill of this (showroom, fiscal year)
        // already holds, so the next number never collides with a bill whose
        // number was moved by hand via change-number.
        var startFrom = sequence?.NextValue ?? 1L;
        var reservedValue = await ComputeNextFreeCoreAsync(
            showroomId, year, startFrom, CoreKeptForTally(sequence), cancellationToken);

        if (sequence is null)
        {
            sequence = new InvoiceSequenceEntity
            {
                ShowroomId = showroomId,
                FiscalYear = year,
                DocumentType = normalizedType,
                NextValue = reservedValue + 1,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            dbContext.InvoiceSequences.Add(sequence);
        }
        else
        {
            sequence.NextValue = reservedValue + 1;
            sequence.UpdatedAtUtc = DateTimeOffset.UtcNow;
        }

        var formatted = InvoiceNumberFormatter.Format(prefix, suffix, reservedValue, year, padding);

        // Inside a caller-owned transaction the caller's SaveChanges persists
        // the sequence together with its own rows: one batched round trip to
        // the database instead of two. (A failed insert aborts a PostgreSQL
        // transaction anyway, so the retry loop above can only help when this
        // method owns the transaction.)
        if (ambientTransaction is null)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ReservedNumber(year, reservedValue, formatted);
    }

    private static bool IsRetryableReservationConflict(DbUpdateException exception) =>
        exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };

    /// <summary>
    /// The invoice prefix, suffix and padding a fresh reservation uses. Public
    /// so change-number can format a typed core value the same way.
    /// </summary>
    public async Task<(string? Prefix, string? Suffix, int Padding)> GetEffectivePrefixSuffixAsync(CancellationToken cancellationToken = default)
    {
        // Only these three columns: the settings row also carries the item
        // masters, karat mapping and print layout as large JSON, and this runs
        // on every Save Bill while the sequence row is locked.
        var settings = await dbContext.CloudSettings
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
            .Select(x => new { x.InvoicePrefix, x.InvoiceSuffix, x.InvoicePadding })
            .FirstOrDefaultAsync(cancellationToken);
        if (settings is null)
        {
            return (null, null, InvoiceNumberFormatter.DefaultPadding);
        }

        return (
            string.IsNullOrWhiteSpace(settings.InvoicePrefix) ? null : settings.InvoicePrefix.Trim(),
            string.IsNullOrWhiteSpace(settings.InvoiceSuffix) ? null : settings.InvoiceSuffix.Trim(),
            settings.InvoicePadding < 1 ? InvoiceNumberFormatter.DefaultPadding : settings.InvoicePadding);
    }

    /// <summary>
    /// Starting at <paramref name="startFrom"/>, advance until the candidate
    /// core does not collide with an existing bill in the same
    /// (showroom, fiscal-year) scope. Occupancy reads the stored
    /// <c>bills.InvoiceCore</c>, so a suffix (<c>-A</c>, <c>-A2</c>) or a
    /// change in padding never hides an occupied core. Only the cores inside
    /// the skip window are fetched, through the (showroom, year, core) index,
    /// instead of every invoice number in the year. Rows without a stored
    /// core (legacy custom numbers) fall back to trailing-digit parsing.
    /// Capped at 1000 iterations to avoid a pathological spin if the admin
    /// has jumped numbers far ahead and the entire skip range happens to be
    /// occupied.
    /// </summary>
    /// <param name="keptForTally">
    /// A core no bill holds here but a voucher in Tally does
    /// (<see cref="CoreKeptForTally"/>); it is passed over like an occupied one.
    /// </param>
    private async Task<long> ComputeNextFreeCoreAsync(
        Guid showroomId,
        string fiscalYear,
        long startFrom,
        long? keptForTally,
        CancellationToken cancellationToken)
    {
        const int SafetyCap = 1000;
        var candidate = startFrom < 1L ? 1L : startFrom;
        var windowEnd = candidate + SafetyCap;

        var rows = await dbContext.Bills
            .AsNoTracking()
            .Where(b => b.ShowroomId == showroomId
                        && b.FiscalYear == fiscalYear
                        && ((b.InvoiceCore >= candidate && b.InvoiceCore < windowEnd)
                            || (b.InvoiceCore == null && b.InvoiceNumber != null)))
            .Select(b => new { b.InvoiceCore, b.InvoiceNumber })
            .ToListAsync(cancellationToken);

        var occupied = new HashSet<long>();
        if (keptForTally is { } kept)
        {
            occupied.Add(kept);
        }
        foreach (var row in rows)
        {
            if ((row.InvoiceCore ?? InvoiceNumberFormatter.TryParseTrailingCore(row.InvoiceNumber)) is { } core)
            {
                occupied.Add(core);
            }
        }

        for (var i = 0; i < SafetyCap; i++)
        {
            if (!occupied.Contains(candidate))
            {
                return candidate;
            }
            candidate++;
        }
        throw new InvalidOperationException(
            $"Could not find a free invoice number within {SafetyCap} tries starting at {startFrom} for fiscal year {fiscalYear}.");
    }

    /// <summary>
    /// The floor of a sequence is one past the highest core that Tally holds a
    /// voucher for after its bill was deleted or renumbered. That core is
    /// normally behind the sequence, where the floor stops a roll-back from
    /// reaching it. It is ahead of the sequence when the bill had first been
    /// renumbered upwards, and then only the reservation can keep it unused.
    /// </summary>
    private static long? CoreKeptForTally(InvoiceSequenceEntity? sequence) =>
        sequence is { MinNextValue: > 1L } ? sequence.MinNextValue - 1L : null;

    private static string NormalizeDocumentType(string documentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        var normalized = documentType.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            DocumentTypeSalesInvoice => DocumentTypeSalesInvoice,
            _ => throw new RequestValidationException($"Unsupported document type '{documentType}'.")
        };
    }

    public static string NormalizeFiscalYear(string? fiscalYear)
    {
        if (!string.IsNullOrWhiteSpace(fiscalYear))
        {
            return fiscalYear.Trim();
        }
        return InvoiceNumberFormatter.ComputeFiscalYear(DateTimeOffset.UtcNow);
    }

}
