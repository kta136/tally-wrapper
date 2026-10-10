using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// Centralized non-2xx handling for typed API clients. Parses RFC 7807
/// ProblemDetails (the API's <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/>
/// shape emitted by <c>DomainExceptionHandler</c>), then falls back to raw body.
/// Throws <see cref="ApiException"/> —
/// a subtype of <see cref="HttpRequestException"/>, so existing
/// <c>catch (HttpRequestException)</c> blocks keep working but now see
/// the server's <c>detail</c> string in <c>ex.Message</c>.
/// </summary>
internal static class ApiResponseReader
{
    // The same options ReadFromJsonAsync uses when it is given none.
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public static async Task EnsureSuccessOrThrowAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = response.Content is null
            ? null
            : await response.Content.ReadAsStringAsync(cancellationToken);

        var (title, detail) = ParseProblemDetails(rawBody);
        throw new ApiException(response.StatusCode, title, detail, rawBody);
    }

    public static async Task<T> ReadOrThrowAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken = default)
    {
        await EnsureSuccessOrThrowAsync(response, cancellationToken);
        var body = await response.Content.ReadFromJsonAsync<T>(JsonOpts, cancellationToken);
        return body ?? throw new InvalidOperationException(
            $"API returned a successful status ({(int)response.StatusCode}) but an empty body.");
    }

    /// <summary>
    /// Renders a short user-facing string from a failed call, so a view model
    /// needs one <c>catch (Exception ex)</c>:
    /// <c>StatusMessage = $"Save failed: {ApiResponseReader.FormatError(ex)}";</c>
    /// An <see cref="ApiException"/> gives the server's <c>detail</c>/<c>title</c>;
    /// a request that never got an answer gives its status code or
    /// <c>"network error"</c>; anything else gives its own message.
    /// </summary>
    public static string FormatError(Exception ex) => ex switch
    {
        ApiException => ex.Message,
        HttpRequestException http => http.StatusCode?.ToString() ?? "network error",
        _ => ex.Message
    };

    private static (string? Title, string? Detail) ParseProblemDetails(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? title = null;
            string? detail = null;

            if (doc.RootElement.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String)
            {
                title = t.GetString();
            }
            if (doc.RootElement.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String)
            {
                detail = d.GetString();
            }
            return (title, detail);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}

/// <summary>
/// Thrown by <see cref="ApiResponseReader"/> on a non-2xx response. Inherits
/// <see cref="HttpRequestException"/> so existing catch blocks continue to
/// work; <c>Message</c> is the server's <c>detail</c> (or <c>title</c>, or
/// <c>"HTTP {status}"</c>) so operators see something actionable.
/// </summary>
public sealed class ApiException : HttpRequestException
{
    public string? Title { get; }
    public string? Detail { get; }
    public string? RawBody { get; }

    public ApiException(HttpStatusCode statusCode, string? title, string? detail, string? rawBody)
        : base(BuildMessage(statusCode, title, detail), inner: null, statusCode: statusCode)
    {
        Title = title;
        Detail = detail;
        RawBody = rawBody;
    }

    private static string BuildMessage(HttpStatusCode statusCode, string? title, string? detail)
    {
        if (!string.IsNullOrWhiteSpace(detail))
        {
            return detail!;
        }
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title!;
        }
        return $"HTTP {(int)statusCode} {statusCode}";
    }
}
