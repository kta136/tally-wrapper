using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShowroomBilling.Application.Numbering;
using ShowroomBilling.Contracts.Numbering;
using ShowroomBilling.Infrastructure.Persistence;
using ShowroomBilling.Infrastructure.Persistence.Entities;

namespace ShowroomBilling.Infrastructure.Numbering;

public sealed class NumberingService(ShowroomBillingDbContext dbContext) : INumberingService
{
    private const string DefaultShowroomCode = "default";
    private const string InMemoryProviderName = "Microsoft.EntityFrameworkCore.InMemory";
    private const int MaxReservationAttempts = 3;

    public async Task<NumberingPreviewResponse> GetPreviewAsync(
        string documentType,
        string? fiscalYear,
        CancellationToken cancellationToken = default)
    {
        var normalizedType = NormalizeDocumentType(documentType);
        var year = NormalizeFiscalYear(fiscalYear);
        var showroomId = ResolveShowroomId(DefaultShowroomCode);

        var sequence = await dbContext.InvoiceSequences
            .AsNoTracking()
            .FirstOrDefaultAsync(
                x => x.ShowroomId == showroomId && x.FiscalYear == year && x.DocumentType == normalizedType,
                cancellationToken);

        var (prefix, suffix, padding) = await GetPrefixSuffixAsync(cancellationToken);
        var previewValue = await ComputeNextFreeCoreAsync(
            showroomId, year, prefix, suffix, sequence?.NextValue ?? 1L, cancellationToken);
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

    public async Task<ReserveNumberResponse> ReserveAsync(
        ReserveNumberRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.IdempotencyKey);

        var idempotencyKey = request.IdempotencyKey.Trim();
        var normalizedType = NormalizeDocumentType(request.DocumentType);
        var year = NormalizeFiscalYear(request.FiscalYear);
        var showroomId = ResolveShowroomId(DefaultShowroomCode);

        for (var attempt = 1; attempt <= MaxReservationAttempts; attempt++)
        {
            try
            {
                return await ReserveCoreAsync(
                    request,
                    idempotencyKey,
                    normalizedType,
                    year,
                    showroomId,
                    cancellationToken);
            }
            catch (DbUpdateException ex) when (IsRetryableReservationConflict(ex) && attempt < MaxReservationAttempts)
            {
                dbContext.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Invoice number reservation retry loop exited unexpectedly.");
    }

    private async Task<ReserveNumberResponse> ReserveCoreAsync(
        ReserveNumberRequest request,
        string idempotencyKey,
        string normalizedType,
        string year,
        Guid showroomId,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.InvoiceNumberReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.IdempotencyKey == idempotencyKey, cancellationToken);
        if (existing is not null)
        {
            return new ReserveNumberResponse(
                ReservationId: existing.Id,
                IdempotencyKey: existing.IdempotencyKey,
                ShowroomId: existing.ShowroomId,
                FiscalYear: existing.FiscalYear,
                DocumentType: existing.DocumentType,
                ReservedValue: existing.ReservedValue,
                FormattedNumber: existing.FormattedNumber,
                ReservedAtUtc: existing.ReservedAtUtc,
                AlreadyExisted: true);
        }

        var providerName = dbContext.Database.ProviderName ?? string.Empty;
        var isInMemory = providerName.Equals(InMemoryProviderName, StringComparison.Ordinal);
        var ambientTransaction = dbContext.Database.CurrentTransaction;

        // If the caller already opened a transaction, enlist in it and leave
        // commit responsibility to them. Otherwise own the txn here.
        await using var transaction = isInMemory || ambientTransaction is not null
            ? null
            : await dbContext.Database.BeginTransactionAsync(cancellationToken);

        InvoiceSequenceEntity? sequence;
        if (isInMemory)
        {
            sequence = await dbContext.InvoiceSequences.FirstOrDefaultAsync(
                x => x.ShowroomId == showroomId && x.FiscalYear == year && x.DocumentType == normalizedType,
                cancellationToken);
        }
        else
        {
            var locked = await dbContext.InvoiceSequences
                .FromSqlInterpolated($@"
SELECT * FROM public.invoice_sequences
WHERE ""ShowroomId"" = {showroomId}
  AND ""FiscalYear"" = {year}
  AND ""DocumentType"" = {normalizedType}
FOR UPDATE")
                .ToListAsync(cancellationToken);
            sequence = locked.FirstOrDefault();
        }

        var (prefix, suffix, padding) = await GetPrefixSuffixAsync(cancellationToken);

        // Forward-skip past any core value whose formatted form already lives in
        // `bills.InvoiceNumber` for this (showroom, fiscal year). This keeps the
        // next reservation from colliding with a bill whose number was manually
        // moved via change-number. Orphaned reservations don't block reuse —
        // that was the pre-existing design intent (see docs/04).
        var startFrom = sequence?.NextValue ?? 1L;
        var reservedValue = await ComputeNextFreeCoreAsync(
            showroomId, year, prefix, suffix, startFrom, cancellationToken);

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

        var reservation = new InvoiceNumberReservationEntity
        {
            Id = Guid.NewGuid(),
            IdempotencyKey = idempotencyKey,
            ShowroomId = showroomId,
            FiscalYear = year,
            DocumentType = normalizedType,
            ReservedValue = reservedValue,
            FormattedNumber = formatted,
            ReservedForReference = string.IsNullOrWhiteSpace(request.ReservedForReference)
                ? null
                : request.ReservedForReference.Trim(),
            ReservedAtUtc = DateTimeOffset.UtcNow
        };
        dbContext.InvoiceNumberReservations.Add(reservation);

        dbContext.AuditEvents.Add(new AuditEventEntity
        {
            Id = Guid.NewGuid(),
            EntityType = "numbering",
            EntityId = reservation.Id.ToString(),
            EventType = $"numbering.{normalizedType}.reserved",
            ActorType = "system",
            PayloadJson = JsonSerializer.Serialize(new
            {
                fiscalYear = year,
                reservedValue,
                formatted
            }),
            CreatedAtUtc = reservation.ReservedAtUtc
        });

        await dbContext.SaveChangesAsync(cancellationToken);
        if (transaction is not null)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ReserveNumberResponse(
            ReservationId: reservation.Id,
            IdempotencyKey: reservation.IdempotencyKey,
            ShowroomId: reservation.ShowroomId,
            FiscalYear: reservation.FiscalYear,
            DocumentType: reservation.DocumentType,
            ReservedValue: reservation.ReservedValue,
            FormattedNumber: reservation.FormattedNumber,
            ReservedAtUtc: reservation.ReservedAtUtc,
            AlreadyExisted: false);
    }

    private bool IsRetryableReservationConflict(DbUpdateException exception)
    {
        if (UsesInMemoryProvider())
        {
            return false;
        }

        return exception.GetBaseException() is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation
        };
    }

    public async Task<NumberingScopesResponse> GetScopesAsync(CancellationToken cancellationToken = default)
    {
        var (prefix, suffix, _) = await GetPrefixSuffixAsync(cancellationToken);
        var currentYear = NormalizeFiscalYear(null);

        var rows = await dbContext.InvoiceSequences
            .AsNoTracking()
            .OrderBy(x => x.ShowroomId)
            .ThenBy(x => x.FiscalYear)
            .ThenBy(x => x.DocumentType)
            .ToListAsync(cancellationToken);

        var scopes = rows
            .Select(r => new NumberingScopeItem(r.ShowroomId, r.FiscalYear, r.DocumentType, r.NextValue, r.UpdatedAtUtc))
            .ToArray();

        return new NumberingScopesResponse(currentYear, prefix, suffix, scopes);
    }

    /// <summary>
    /// Exposed via <see cref="INumberingService.GetEffectivePrefixSuffixAsync"/>
    /// so other services (e.g. <c>BillService.ChangeInvoiceNumberAsync</c>) can
    /// format a user-typed core value the same way a fresh reservation would.
    /// </summary>
    internal async Task<(string? Prefix, string? Suffix, int Padding)> GetPrefixSuffixAsync(CancellationToken cancellationToken)
    {
        var settings = await dbContext.CloudSettings
            .AsNoTracking()
            .OrderByDescending(x => x.UpdatedAtUtc)
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

    public Task<(string? Prefix, string? Suffix, int Padding)> GetEffectivePrefixSuffixAsync(CancellationToken cancellationToken = default)
        => GetPrefixSuffixAsync(cancellationToken);

    /// <summary>
    /// Starting at <paramref name="startFrom"/>, advance until the candidate
    /// core does not collide with an existing bill in the same
    /// (showroom, fiscal-year) scope. Occupancy is computed by parsing the
    /// trailing digits of <c>bills.InvoiceNumber</c> rather than comparing to
    /// <see cref="InvoiceNumberFormatter.Format"/>'s output, so a scope with
    /// historical mixed formatting (e.g. legacy <c>/49</c> alongside newer
    /// <c>/0049</c>) doesn't double-allocate the same semantic core.
    /// Capped at 1000 iterations to avoid a pathological spin if the admin
    /// has jumped numbers far ahead and the entire skip range happens to be
    /// occupied.
    /// </summary>
    private async Task<long> ComputeNextFreeCoreAsync(
        Guid showroomId,
        string fiscalYear,
        string? prefix,
        string? suffix,
        long startFrom,
        CancellationToken cancellationToken)
    {
        _ = prefix;
        _ = suffix;

        var occupiedCores = await dbContext.Bills
            .AsNoTracking()
            .Where(b => b.ShowroomId == showroomId
                        && b.FiscalYear == fiscalYear
                        && b.InvoiceNumber != null)
            .Select(b => b.InvoiceNumber!)
            .ToListAsync(cancellationToken);

        var occupied = new HashSet<long>();
        foreach (var number in occupiedCores)
        {
            if (InvoiceNumberFormatter.TryParseTrailingCore(number) is { } core)
            {
                occupied.Add(core);
            }
        }

        const int SafetyCap = 1000;
        var candidate = startFrom < 1L ? 1L : startFrom;
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

    private static string NormalizeDocumentType(string documentType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentType);
        var normalized = documentType.Trim().ToLowerInvariant().Replace('-', '_');
        return normalized switch
        {
            INumberingService.DocumentTypeSalesInvoice => INumberingService.DocumentTypeSalesInvoice,
            _ => throw new ArgumentException($"Unsupported document type '{documentType}'.", nameof(documentType))
        };
    }

    public static string NormalizeFiscalYear(string? fiscalYear)
    {
        if (!string.IsNullOrWhiteSpace(fiscalYear))
        {
            return fiscalYear.Trim();
        }
        return ComputeCurrentFiscalYear(DateTimeOffset.UtcNow);
    }

    public static string ComputeCurrentFiscalYear(DateTimeOffset nowUtc)
    {
        var year = nowUtc.Year;
        var startYear = nowUtc.Month >= 4 ? year : year - 1;
        var endYear = startYear + 1;
        return $"{startYear}-{endYear % 100:D2}";
    }

    private static Guid ResolveShowroomId(string showroomCode)
    {
        var bytes = Encoding.UTF8.GetBytes(showroomCode.Trim().ToLowerInvariant());
        var hash = MD5.HashData(bytes);
        return new Guid(hash);
    }

    private bool UsesInMemoryProvider() =>
        string.Equals(dbContext.Database.ProviderName, InMemoryProviderName, StringComparison.Ordinal);
}
