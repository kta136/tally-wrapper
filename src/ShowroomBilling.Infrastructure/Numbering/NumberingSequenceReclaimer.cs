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
    private const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";

    /// <summary>
    /// Reconcile the sequence row for one (showroom, fiscalYear) scope.
    /// </summary>
    /// <param name="trigger">
    /// Free-form audit label so the rollback event records why it ran
    /// (e.g. <c>"delete:{billId}"</c>, <c>"rename:{billId}"</c>,
    /// <c>"startup-self-heal"</c>).
    /// </param>
    /// <returns>True if NextValue moved; false otherwise.</returns>
    internal static async Task<bool> ReclaimAsync(
        ShowroomBillingDbContext dbContext,
        Guid showroomId,
        string fiscalYear,
        string trigger,
        CancellationToken cancellationToken)
    {
        const string documentType = INumberingService.DocumentTypeSalesInvoice;
        var isInMemory = string.Equals(
            dbContext.Database.ProviderName, InMemoryProviderName, StringComparison.Ordinal);

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

        if (sequence is null || sequence.NextValue <= 1L)
        {
            return false;
        }

        var remainingNumbers = await dbContext.Bills
            .AsNoTracking()
            .Where(b => b.ShowroomId == showroomId
                        && b.FiscalYear == fiscalYear
                        && b.InvoiceNumber != null)
            .Select(b => b.InvoiceNumber!)
            .ToListAsync(cancellationToken);

        long maxOccupiedCore = 0L;
        foreach (var number in remainingNumbers)
        {
            if (InvoiceNumberFormatter.TryParseTrailingCore(number) is { } core && core > maxOccupiedCore)
            {
                maxOccupiedCore = core;
            }
        }

        var startedAt = sequence.NextValue;
        var target = Math.Max(1L, maxOccupiedCore + 1L);
        if (target >= startedAt)
        {
            return false;
        }

        sequence.NextValue = target;
        sequence.UpdatedAtUtc = DateTimeOffset.UtcNow;

        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.CreateVersion7(),
            EntityType = "numbering",
            EntityId = $"{showroomId}|{fiscalYear}|{documentType}",
            EventType = $"numbering.{documentType}.rolled_back",
            ActorType = "system",
            PayloadJson = JsonSerializer.Serialize(new
            {
                fiscalYear,
                from = startedAt,
                to = sequence.NextValue,
                maxOccupiedCore,
                trigger
            }),
            CreatedAtUtc = sequence.UpdatedAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
