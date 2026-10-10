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
        // Everything before SendWriteAsync happens before any byte reaches
        // Tally, so a failure here is a definite failure, never an ambiguous
        // outcome that needs reconciliation.
        XElement xml;
        string requestExcerpt;
        try
        {
            var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
            var ledgers = settings.Settings.Ledgers;
            var company = settings.Settings.Connection.ActiveCompanyName;
            var print = settings.Settings.Print;

            xml = TallyXmlVoucherBuilder.Build(request, ledgers, company, print.CompanyState, print.CompanyCountry);
            var requestXml = xml.ToString(SaveOptions.DisableFormatting);
            requestExcerpt = Truncate(requestXml, ExcerptLimit);
        }
        catch (VoucherBuildException ex)
        {
            logger.LogWarning(ex, "Voucher build failed for bill {BillId}: {Code}.", request.BillId, ex.ErrorCode);
            return TallyPostResponse.Failed(
                ex.ErrorCode,
                ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Voucher could not be prepared for bill {BillId}; nothing was sent to Tally.", request.BillId);
            return TallyPostResponse.Failed(
                "TALLY_PREPARE_FAILED",
                $"The voucher could not be prepared, so nothing was sent to Tally: {Truncate(ex.Message, 900)}");
        }

        XElement response;
        try
        {
            response = await xmlClient.SendWriteAsync(xml, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Tally HTTP call failed for bill {BillId}.", request.BillId);
            return TallyPostResponse.Unknown(
                "TALLY_TRANSPORT_UNKNOWN",
                $"Tally may have processed the voucher before the transport failed: {Truncate(ex.Message, 900)}",
                requestExcerpt: requestExcerpt);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Outer caller didn't cancel us — this came from the TimeoutSeconds
            // budget inside TallyXmlClient. Voucher writes are not retried. If the
            // caller DID cancel, let the exception propagate so cancellation stays fair.
            logger.LogWarning(ex, "Tally call timed out for bill {BillId}.", request.BillId);
            return TallyPostResponse.Unknown(
                "TALLY_TIMEOUT",
                "Tally did not respond before the configured timeout.",
                requestExcerpt: requestExcerpt);
        }
        catch (TallyConfigurationException ex)
        {
            // Configuration was rejected before any voucher request was sent.
            logger.LogWarning(ex, "Tally call rejected for bill {BillId}.", request.BillId);
            return TallyPostResponse.Failed(
                "TALLY_NOT_CONFIGURED",
                Truncate(ex.Message, 1024),
                requestExcerpt: requestExcerpt);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Xml.XmlException)
        {
            logger.LogWarning(ex, "Tally returned an unusable response for bill {BillId}.", request.BillId);
            return TallyPostResponse.Unknown(
                "TALLY_RESPONSE_UNKNOWN",
                "Tally may have processed the voucher, but its response could not be verified.",
                requestExcerpt: requestExcerpt);
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
        var errors = ParseInt(FindFirst(response, "ERRORS"));
        var exceptions = ParseInt(FindFirst(response, "EXCEPTIONS"));
        var created = ParseInt(FindFirst(response, "CREATED"));
        var altered = ParseInt(FindFirst(response, "ALTERED"));
        var createdCount = created ?? 0;
        var alteredCount = altered ?? 0;
        var reportedProblem = !string.IsNullOrWhiteSpace(lineError) || (errors ?? 0) > 0 || (exceptions ?? 0) > 0;

        // Tally changed something AND reported a problem. A voucher may exist,
        // so this is not a definite failure: Retry/Repost from `failed` would
        // duplicate it. Park the bill for reconciliation instead.
        if (reportedProblem && createdCount + alteredCount > 0)
        {
            return TallyPostResponse.Unknown(
                "TALLY_PARTIAL_EFFECT",
                Truncate(
                    $"Tally reported created={createdCount}, altered={alteredCount} together with errors={errors ?? 0}, exceptions={exceptions ?? 0}"
                    + (string.IsNullOrWhiteSpace(lineError) ? "." : $": {lineError}"),
                    1024),
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        if (!string.IsNullOrWhiteSpace(lineError))
        {
            return TallyPostResponse.Failed(
                "TALLY_LINEERROR",
                Truncate(lineError!, 1024),
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        if ((errors ?? 0) > 0 || (exceptions ?? 0) > 0)
        {
            return TallyPostResponse.Failed(
                "TALLY_ERRORS",
                $"Tally reported errors={errors ?? 0}, exceptions={exceptions ?? 0}.",
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Alter && createdCount > 0)
        {
            // A voucher now exists that the alter did not intend; needs a human.
            return TallyPostResponse.Unknown(
                "TALLY_UNEXPECTED_CREATE_ON_ALTER",
                "Tally created a voucher while processing an alter request.",
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Alter && alteredCount <= 0)
        {
            return TallyPostResponse.Failed(
                "TALLY_NO_EFFECT",
                "Tally response reported no altered records for the voucher update.",
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        if (request.Operation == TallyPostOperation.Create && createdCount + alteredCount <= 0)
        {
            return TallyPostResponse.Failed(
                "TALLY_NO_EFFECT",
                "Tally response reported no created or altered records.",
                requestExcerpt: requestExcerpt,
                responseExcerpt: responseExcerpt);
        }

        var lastVoucherId = FindFirst(response, "LASTVCHID");
        var lastMasterId = FindFirst(response, "LASTMID");
        var tallyMasterId = TallyIds.NormalizePositiveInteger(lastVoucherId)
            ?? (request.Operation == TallyPostOperation.Alter
                ? TallyIds.NormalizePositiveInteger(request.TargetTagValue)
                : null);
        var remoteId = lastVoucherId
            ?? lastMasterId
            ?? (request.Operation == TallyPostOperation.Alter ? request.TargetTagValue : request.IdempotencyKey);

        return TallyPostResponse.Posted(
            remoteId,
            requestExcerpt: requestExcerpt,
            responseExcerpt: responseExcerpt,
            tallyMasterId: tallyMasterId);
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


    private static string Truncate(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
