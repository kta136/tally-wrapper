using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ShowroomBilling.Application.Settings;

namespace ShowroomBilling.Infrastructure.Tally;

/// <summary>
/// Low-level transport for Tally's XML-over-HTTP interface. Endpoint and timeout
/// come from cloud settings (Connection section) at call time — no background
/// worker, no DI-cached configuration, so a settings change takes effect on the
/// very next call.
/// </summary>
public interface ITallyXmlClient
{
    Task<XElement> SendAsync(XElement request, CancellationToken cancellationToken = default);
    string EndpointDescription { get; }
}

public sealed class TallyXmlClient(
    HttpClient httpClient,
    ICloudSettingsService cloudSettings) : ITallyXmlClient
{
    private static readonly Regex ControlChars = new(
        @"[\x00-\x08\x0B\x0C\x0E-\x1F]",
        RegexOptions.Compiled);

    private static readonly Regex NumericCharRefs = new(
        @"&#(x[0-9a-fA-F]+|[0-9]+);",
        RegexOptions.Compiled);

    public string EndpointDescription => "Tally XML endpoint resolved from cloud settings.";

    public async Task<XElement> SendAsync(XElement request, CancellationToken cancellationToken = default)
    {
        var settings = await cloudSettings.GetEffectiveSettingsAsync(cancellationToken);
        var conn = settings.Settings.Connection;
        if (string.IsNullOrWhiteSpace(conn.Host) || conn.Port <= 0)
        {
            throw new InvalidOperationException(
                "Tally connection is not configured. Set Connection.Host and Connection.Port in Settings.");
        }

        var endpoint = new Uri($"http://{conn.Host}:{conn.Port}");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(conn.TimeoutSeconds, 5, 300));

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var xml = request.ToString(SaveOptions.DisableFormatting);
        using var content = new StringContent(xml, Encoding.UTF8, "text/xml");
        using var response = await httpClient.PostAsync(endpoint, content, cts.Token);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(cts.Token);
        if (string.IsNullOrWhiteSpace(body))
        {
            throw new InvalidOperationException("Tally returned an empty XML response.");
        }
        return XElement.Parse(StripInvalidXmlControlChars(body));
    }

    public static string StripInvalidXmlControlChars(string input)
    {
        var stripped = ControlChars.Replace(input, string.Empty);
        return NumericCharRefs.Replace(stripped, match =>
        {
            var token = match.Groups[1].Value;
            var isHex = token.StartsWith("x", StringComparison.OrdinalIgnoreCase);
            var digits = isHex ? token[1..] : token;
            if (!int.TryParse(
                    digits,
                    isHex ? NumberStyles.HexNumber : NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var codePoint))
            {
                return match.Value;
            }
            return IsValidXmlCodePoint(codePoint) ? match.Value : string.Empty;
        });
    }

    private static bool IsValidXmlCodePoint(int codePoint)
    {
        if (codePoint == 0x09 || codePoint == 0x0A || codePoint == 0x0D) return true;
        if (codePoint < 0x20) return false;
        if (codePoint >= 0x7F && codePoint <= 0x9F && codePoint != 0x85) return false;
        return codePoint <= 0x10FFFF;
    }
}
