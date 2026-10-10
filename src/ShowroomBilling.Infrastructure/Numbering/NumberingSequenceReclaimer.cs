using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Numbering;

/// <summary>
/// Recomputes <c>InvoiceSequences.NextValue</c> to
/// <c>min(currentNextValue, max(parsed-trailing-digits across remaining bills in scope) + 1)</c>
/// for one (showroom, fiscalYear, documentType) scope.
///
/// Reclaims trailing freed cores so the next reservation reuses them. Used by:
/// - <c>BillAdminWorkflow.DeleteAsync</c> — after a bill delete.
/// - <c>BillAdminWorkflow.ChangeInvoiceNumberAsync</c> — after a rename
///   that moves the trailing bill down (e.g. 94 → 92).
/// - <c>SequenceSelfHealHostedService</c> — one-shot at API startup so a
///   sequence that fell out of sync under older code self-heals on the next
///   boot.
///
/// Locks the sequence row (FOR UPDATE on Postgres) so a concurrent reservation
/// cannot allocate a core the rollback is about to free. Format-tolerant: it
/// parses trailing digits of <c>bills.InvoiceNumber</c> rather than comparing
/// to a formatted string, so historical mixed-format scopes (legacy <c>/49</c>
/// vs newer <c>/0049</c>) collapse to the same core.
///
/// Caller is responsible for transaction scope: if no ambient transaction is
/// open the helper writes inside <see cref="DbContext"/>'s implicit txn on
/// SaveChangesAsync. If the caller wants atomicity with other work, open one
/// before calling.
/// </summary>
internal static class NumberingSequenceReclaimer
{

    /// <summary>
    /// Reconcile the sequence row for one (showroom, fiscalYear) scope.
    /// </summary>
    /// <param name="trigger">
    /// Free-form audit label so the rollback event records why it ran
    /// (e.g. <c>"delete:{billId}"</c>, <c>"rename:{billId}"</c>,
    /// <c>"startup-self-heal"</c>).
    /// </param>
    /// <param name="keepCoreReserved">
    /// A core Tally still holds (the bill being deleted or renumbered was
    /// pushed). The sequence floor is raised past it so neither this rollback
    /// nor any later one hands that number out again.
    /// </param>
    /// <returns>True if NextValue moved; false otherwise.</returns>
    internal static async Task<bool> ReclaimAsync(
        ShowroomBillingDbContext dbContext,
        Guid showroomId,
        string fiscalYear,
        string trigger,
        CancellationToken cancellationToken,
        long? keepCoreReserved = null)
    {
        const string documentType = INumberingService.DocumentTypeSalesInvoice;
        var isInMemory = dbContext.UsesInMemoryProvider();

        InvoiceSequenceEntity? sequence;
        if (isInMemory)
        {
            sequence = await dbContext.InvoiceSequences.FirstOrDefaultAsync(
                x => x.ShowroomId == showroomId
                     && x.FiscalYear == fiscalYear
                     && x.DocumentType == documentType,
                cancellationToken);
        }
        else
        {
            var locked = await dbContext.InvoiceSequences
                .FromSqlInterpolated($@"
SELECT * FROM public.invoice_sequences
WHERE ""ShowroomId"" = {showroomId}
  AND ""FiscalYear"" = {fiscalYear}
  AND ""DocumentType"" = {documentType}
FOR UPDATE")
                .ToListAsync(cancellationToken);
            sequence = locked.FirstOrDefault();
        }

        if (sequence is null)
        {
            return false;
        }

        if (keepCoreReserved is { } reservedCore && reservedCore + 1L > sequence.MinNextValue)
        {
            sequence.MinNextValue = reservedCore + 1L;
            sequence.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        if (sequence.NextValue <= 1L)
        {
            return false;
        }

        var scope = dbContext.Bills
            .AsNoTracking()
            .Where(b => b.ShowroomId == showroomId && b.FiscalYear == fiscalYear);
        long maxOccupiedCore = await scope
            .Where(b => b.InvoiceCore != null)
            .MaxAsync(b => (long?)b.InvoiceCore, cancellationToken) ?? 0L;

        // Legacy rows without a stored core: parse their display text.
        var legacyNumbers = await scope
            .Where(b => b.InvoiceCore == null && b.InvoiceNumber != null)
            .Select(b => b.InvoiceNumber!)
            .ToListAsync(cancellationToken);
        foreach (var number in legacyNumbers)
        {
            if (InvoiceNumberFormatter.TryParseTrailingCore(number) is { } core && core > maxOccupiedCore)
            {
                maxOccupiedCore = core;
            }
        }

        var startedAt = sequence.NextValue;
        var target = Math.Max(Math.Max(1L, maxOccupiedCore + 1L), sequence.MinNextValue);
        if (target >= startedAt)
        {
            return false;
        }

        sequence.NextValue = target;
        sequence.UpdatedAtUtc = DateTimeOffset.UtcNow;

        dbContext.AuditEvents.Add(AuditRow.Create(
            "numbering",
            $"{showroomId}|{fiscalYear}|{documentType}",
            $"numbering.{documentType}.rolled_back",
            AuditRow.System,
            sequence.UpdatedAtUtc,
            new
            {
                fiscalYear,
                from = startedAt,
                to = sequence.NextValue,
                maxOccupiedCore,
                trigger
            }));

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
