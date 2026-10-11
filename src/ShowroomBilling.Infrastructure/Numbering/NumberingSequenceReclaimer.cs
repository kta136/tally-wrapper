using Microsoft.EntityFrameworkCore;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Numbering;

/// <summary>
/// Rolls <c>InvoiceSequences.NextValue</c> back to one past the highest core a
/// bill of the scope still holds, never below <c>MinNextValue</c>, so a number
/// freed at the end of the series is issued again. Called after a bill is
/// deleted (<c>BillAdminWorkflow.DeleteAsync</c>) and after one is renumbered
/// (<c>BillAdminWorkflow.ChangeInvoiceNumberAsync</c>, e.g. 94 → 92).
///
/// Occupancy is read from the stored <c>bills.InvoiceCore</c>; rows without
/// one (legacy custom numbers) are read by their trailing digits.
///
/// The sequence row is locked <c>FOR UPDATE</c>, which only holds inside a
/// transaction: the caller opens one around its own save and this call, so a
/// concurrent reservation cannot take a core the roll-back is about to free.
/// </summary>
internal static class NumberingSequenceReclaimer
{
    /// <param name="trigger">
    /// Why it ran, for the audit row: <c>"delete:{billId}"</c> or
    /// <c>"rename:{billId}"</c>.
    /// </param>
    /// <param name="keepCoreReserved">
    /// A core Tally still holds (the bill being deleted or renumbered was
    /// pushed). The sequence floor is raised past it: no roll-back goes below
    /// the floor, and a reservation passes over the core just under it.
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
        const string documentType = NumberingService.DocumentTypeSalesInvoice;
        var sequence = await LoadSequenceForUpdateAsync(
            dbContext, showroomId, fiscalYear, documentType, cancellationToken);

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

    /// <summary>
    /// Loads the sequence row for a scope and locks it with <c>FOR UPDATE</c>
    /// until the surrounding transaction ends, so two reservations (or a
    /// reservation and a rollback) cannot read the same <c>NextValue</c>.
    /// </summary>
    internal static async Task<InvoiceSequenceEntity?> LoadSequenceForUpdateAsync(
        ShowroomBillingDbContext dbContext,
        Guid showroomId,
        string fiscalYear,
        string documentType,
        CancellationToken cancellationToken)
    {
        var locked = await dbContext.InvoiceSequences
            .FromSqlInterpolated($@"
SELECT * FROM public.invoice_sequences
WHERE ""ShowroomId"" = {showroomId}
  AND ""FiscalYear"" = {fiscalYear}
  AND ""DocumentType"" = {documentType}
FOR UPDATE")
            .ToListAsync(cancellationToken);
        return locked.FirstOrDefault();
    }
}
