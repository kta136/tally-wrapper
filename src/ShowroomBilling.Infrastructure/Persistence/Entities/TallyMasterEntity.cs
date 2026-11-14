namespace ShowroomBilling.Infrastructure.Persistence.Entities;

/// <summary>
/// One list last read from Tally (companies, ledgers, stock items or voucher
/// types), kept whole: a refresh replaces the row, and nothing looks inside a list.
/// </summary>
public sealed class TallyMasterEntity
{
    /// <summary>One of the snapshot names in <c>Contracts.Masters.MasterTypes</c>; the key.</summary>
    public string MasterType { get; set; } = string.Empty;

    public DateTimeOffset FetchedAtUtc { get; set; }

    /// <summary>How many entries <see cref="ItemsJson"/> holds, so the freshness report need not read the lists.</summary>
    public int ItemCount { get; set; }

    /// <summary>The list, in name order, as a JSON array of the master's item record.</summary>
    public string ItemsJson { get; set; } = "[]";
}
