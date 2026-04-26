using ShowroomBilling.Printing;

namespace ShowroomBilling.Desktop.Services;

public interface IPrintLayoutOptionsProvider
{
    /// <summary>Most recently resolved layout (clamped, with asset bytes loaded).</summary>
    PrintLayoutOptions Current { get; }

    /// <summary>Fetch layout + assets from the API and refresh <see cref="Current"/>.</summary>
    Task<PrintLayoutOptions> RefreshAsync(CancellationToken cancellationToken = default);

    /// <summary>Update the invoice/terms font sizes without re-fetching assets.</summary>
    void ApplyFontSizes(int invoiceFontSize, int termsFontSize);
}

public sealed class PrintLayoutOptionsProvider : IPrintLayoutOptionsProvider
{
    private readonly ISettingsApiClient _settings;
    private readonly IPrintAssetApiClient _assets;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);

    public PrintLayoutOptionsProvider(ISettingsApiClient settings, IPrintAssetApiClient assets)
    {
        _settings = settings;
        _assets = assets;
    }

    public PrintLayoutOptions Current { get; private set; } = PrintLayoutOptions.Default;

    public async Task<PrintLayoutOptions> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshGate.WaitAsync(cancellationToken);
        try
        {
            var layout = (await _settings.GetPrintLayoutAsync(cancellationToken)).Layout;

            // Only honour assets explicitly attached to the placement. Falling back to
            // "first asset of kind X" would resurrect stale uploads (delete does not
            // clear the placement), so an operator who thinks they cleared a logo would
            // still see it on print.
            byte[]? logoBytes = null;
            byte[]? signatureBytes = null;
            if (layout.Logo?.AssetId is { } logoId)
            {
                logoBytes = await _assets.DownloadAsync(logoId, cancellationToken);
            }
            if (layout.Signature?.AssetId is { } sigId)
            {
                signatureBytes = await _assets.DownloadAsync(sigId, cancellationToken);
            }

            Current = PrintProfileMapping.ToPrintLayoutOptions(
                layout, logoBytes, signatureBytes, Current.InvoiceFontSize, Current.TermsFontSize);
            return Current;
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void ApplyFontSizes(int invoiceFontSize, int termsFontSize)
    {
        Current = (Current with
        {
            InvoiceFontSize = invoiceFontSize,
            TermsFontSize = termsFontSize,
        }).Clamped();
    }

}
