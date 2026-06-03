using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using ShowroomBilling.Application.Settings;
using ShowroomBilling.Application.Tally;
using ShowroomBilling.Contracts.Tally;

namespace ShowroomBilling.Infrastructure.Tally;

public sealed class TallyPoster(
    ITallyXmlClient xmlClient,
    ICloudSettingsService cloudSettings,
    ILogger<TallyPoster> logger) : ITallyPoster
{
    private const int ExcerptLimit = 4000;

    public async Task<TallyPostResponse> PostAsync(TallyPostRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
        var ledgers = settings.Settings.Ledgers;
        var company = settings.Settings.Connection.ActiveCompanyName;
        var print = settings.Settings.Print;

        XElement xml;
        try
        {
            xml = TallyXmlVoucherBuilder.Build(request, ledgers, company, print.CompanyState, print.CompanyCountry);
        }
        catch (VoucherBuildException ex)
        {
            logger.LogWarning(ex, "Voucher build failed for bill {BillId}: {Code}.", request.BillId, ex.ErrorCode);
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: ex.ErrorCode,
                ErrorMessage: ex.Message,
                XmlShape: "voucher-import-v1",
                RequestExcerpt: null,
                ResponseExcerpt: null);
        }

        var requestXml = xml.ToString(SaveOptions.DisableFormatting);
        var requestExcerpt = Truncate(requestXml, ExcerptLimit);

        XElement response;
        try
        {
            response = await xmlClient.SendAsync(xml, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tally HTTP call failed for bill {BillId}.", request.BillId);
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_HTTP",
                ErrorMessage: Truncate(ex.Message, 1024),
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: null);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Outer caller didn't cancel us — so this came from the inner CTS
            // timer in TallyXmlClient (TimeoutSeconds elapsed) or from a Polly
            // retry that exceeded that same budget. If the caller DID cancel,
            // we let the exception propagate so cancellation stays fair.
            logger.LogWarning(ex, "Tally call timed out for bill {BillId}.", request.BillId);
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_TIMEOUT",
                ErrorMessage: "Tally did not respond before the configured timeout.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: null);
        }
        catch (InvalidOperationException ex)
        {
            // Config missing (Host/Port not set) or Tally returned empty body
            logger.LogWarning(ex, "Tally call rejected for bill {BillId}.", request.BillId);
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_NOT_CONFIGURED",
                ErrorMessage: Truncate(ex.Message, 1024),
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: null);
        }

        var responseXml = response.ToString(SaveOptions.DisableFormatting);
        var responseExcerpt = Truncate(responseXml, ExcerptLimit);

        return ClassifyResponse(response, request, requestExcerpt, responseExcerpt);
    }

    private static TallyPostResponse ClassifyResponse(
        XElement response,
        TallyPostRequest request,
        string? requestExcerpt,
        string? responseExcerpt)
    {
        var lineError = FindFirst(response, "LINEERROR");
        if (!string.IsNullOrWhiteSpace(lineError))
        {
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_LINEERROR",
                ErrorMessage: Truncate(lineError!, 1024),
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: responseExcerpt);
        }

        var errors = ParseInt(FindFirst(response, "ERRORS"));
        var exceptions = ParseInt(FindFirst(response, "EXCEPTIONS"));
        var created = ParseInt(FindFirst(response, "CREATED"));
        var altered = ParseInt(FindFirst(response, "ALTERED"));
        var createdCount = created ?? 0;
        var alteredCount = altered ?? 0;

        if ((errors ?? 0) > 0 || (exceptions ?? 0) > 0)
        {
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_ERRORS",
                ErrorMessage: $"Tally reported errors={errors ?? 0}, exceptions={exceptions ?? 0}.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Alter && createdCount > 0)
        {
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_UNEXPECTED_CREATE_ON_ALTER",
                ErrorMessage: "Tally created a voucher while processing an alter request.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Alter && alteredCount <= 0)
        {
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_NO_EFFECT",
                ErrorMessage: "Tally response reported no altered records for the voucher update.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Create && createdCount + alteredCount <= 0)
        {
            return new TallyPostResponse(
                Outcome: TallyPostOutcome.Failed,
                RemoteId: null,
                ErrorCode: "TALLY_NO_EFFECT",
                ErrorMessage: "Tally response reported no created or altered records.",
                XmlShape: "voucher-import-v1",
                RequestExcerpt: requestExcerpt,
                ResponseExcerpt: responseExcerpt);
        }

        var lastVoucherId = FindFirst(response, "LASTVCHID");
        var lastMasterId = FindFirst(response, "LASTMID");
        var tallyMasterId = NormalizePositiveInteger(lastVoucherId)
            ?? (request.Operation == TallyPostOperation.Alter
                ? NormalizePositiveInteger(request.TargetTagValue)
                : null);
        var remoteId = lastVoucherId
            ?? lastMasterId
            ?? (request.Operation == TallyPostOperation.Alter ? request.TargetTagValue : request.IdempotencyKey);

        return new TallyPostResponse(
            Outcome: TallyPostOutcome.Posted,
            RemoteId: remoteId,
            ErrorCode: null,
            ErrorMessage: null,
            XmlShape: "voucher-import-v1",
            RequestExcerpt: requestExcerpt,
            ResponseExcerpt: responseExcerpt,
            TallyMasterId: tallyMasterId);
    }

    private static string? FindFirst(XElement response, string localName)
    {
        var match = response.DescendantsAndSelf()
            .FirstOrDefault(x => string.Equals(x.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));
        var value = match?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static int? ParseInt(string? value) =>
        int.TryParse(value, out var n) ? n : null;

    private static string? NormalizePositiveInteger(string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;
        return long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
            ? trimmed
            : null;
    }

    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
