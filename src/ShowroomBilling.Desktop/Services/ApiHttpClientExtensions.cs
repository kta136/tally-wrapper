using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using ShowroomBilling.Contracts.Admin;

namespace ShowroomBilling.Desktop.Services;

/// <summary>
/// One request shape for every typed API client: send, turn a non-2xx answer
/// into an <see cref="ApiException"/> carrying the server's message, and read
/// the JSON body. Pass <c>adminToken</c> for an admin-gated endpoint.
/// </summary>
internal static class ApiHttpClientExtensions
{
    public static Task<T> GetApiAsync<T>(
        this HttpClient http, string url, CancellationToken cancellationToken, string? adminToken = null)
        => http.SendApiAsync<T>(HttpMethod.Get, url, body: null, cancellationToken, adminToken);

    /// <summary>Like <see cref="GetApiAsync{T}"/>, but "not found" and "no content" are an absent value, not an error.</summary>
    public static async Task<T?> GetApiOrNullAsync<T>(
        this HttpClient http, string url, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.NoContent)
        {
            return null;
        }

        return await ApiResponseReader.ReadOrThrowAsync<T>(response, cancellationToken);
    }

    public static Task<T> PostApiAsync<T>(
        this HttpClient http, string url, object body, CancellationToken cancellationToken, string? adminToken = null)
        => http.SendApiAsync<T>(HttpMethod.Post, url, body, cancellationToken, adminToken);

    public static Task<T> PutApiAsync<T>(
        this HttpClient http, string url, object body, CancellationToken cancellationToken, string? adminToken = null)
        => http.SendApiAsync<T>(HttpMethod.Put, url, body, cancellationToken, adminToken);

    public static async Task<T> SendApiAsync<T>(
        this HttpClient http,
        HttpMethod method,
        string url,
        object? body,
        CancellationToken cancellationToken,
        string? adminToken = null)
    {
        using var request = CreateRequest(method, url, body, adminToken);
        using var response = await http.SendAsync(request, cancellationToken);
        return await ApiResponseReader.ReadOrThrowAsync<T>(response, cancellationToken);
    }

    /// <summary>Builds a request; the caller sends it when it has to look at the status itself.</summary>
    public static HttpRequestMessage CreateRequest(HttpMethod method, string url, object? body, string? adminToken = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        if (!string.IsNullOrWhiteSpace(adminToken))
        {
            request.Headers.Add(AdminTokenConstants.HeaderName, adminToken);
        }

        return request;
    }

    /// <summary>
    /// Registers a typed API client against the resolved API address, with the
    /// correlation-id and device-token handlers every call carries.
    /// </summary>
    public static IServiceCollection AddApiClient<TClient, TImplementation>(
        this IServiceCollection services, TimeSpan? timeout = null)
        where TClient : class
        where TImplementation : class, TClient
    {
        services.AddHttpClient<TClient, TImplementation>((provider, client) =>
            {
                client.BaseAddress = new Uri(provider.GetRequiredService<IApiEndpointResolver>().BaseUrl);
                if (timeout is { } value)
                {
                    client.Timeout = value;
                }
            })
            .AddHttpMessageHandler<CorrelationIdHandler>()
            .AddHttpMessageHandler<DeviceTokenHandler>();
        return services;
    }
}
