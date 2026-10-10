namespace ShowroomBilling.Contracts.Masters;

/// <summary>
/// Names of the four Tally masters, as they appear on routes, in a refresh
/// request and in each refresh result.
/// </summary>
public static class MasterTypes
{
    public const string Companies = "companies";
    public const string Ledgers = "ledgers";
    public const string StockItems = "stock-items";
    public const string VoucherTypes = "voucher-types";

    /// <summary>
    /// Stored snapshot batches and the freshness report spell these two with
    /// an underscore. The values are persisted, so they stay as they are.
    /// </summary>
    public const string StockItemsSnapshot = "stock_items";

    /// <inheritdoc cref="StockItemsSnapshot"/>
    public const string VoucherTypesSnapshot = "voucher_types";
}
