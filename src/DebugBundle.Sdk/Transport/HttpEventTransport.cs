using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DebugBundle.Transport;

public sealed class HttpEventTransport : IEventTransport, IDisposable
{
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromMinutes(5);
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly bool _ownsClient;

    public HttpEventTransport(Uri endpoint, TimeSpan timeout, HttpClient? httpClient = null)
    {
        _endpoint = endpoint;
        _httpClient = httpClient ?? new HttpClient();
        _ownsClient = httpClient == null;
        _httpClient.Timeout = timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(5) : timeout;
    }

    public async Task<EventTransportResult> SendAsync(EventTransportRequest request, CancellationToken cancellationToken = default)
    {
        var body = JsonSerializer.Serialize(new { events = request.Events }, JsonSerialization.Options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", request.ProjectToken);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
        return new EventTransportResult
        {
            StatusCode = (int)response.StatusCode,
            RetryAfter = BoundedRetryAfter(response)
        };
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static TimeSpan? BoundedRetryAfter(HttpResponseMessage response)
    {
        var value = response.Headers.RetryAfter;
        TimeSpan? retryAfter = null;
        if (value?.Delta != null)
        {
            retryAfter = value.Delta.Value;
        }
        else if (value?.Date != null)
        {
            retryAfter = value.Date.Value - DateTimeOffset.UtcNow;
        }
        else if (response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.FirstOrDefault(), out var seconds))
        {
            retryAfter = TimeSpan.FromSeconds(seconds);
        }

        if (retryAfter == null || retryAfter <= TimeSpan.Zero)
        {
            return null;
        }

        return retryAfter > MaxRetryAfter ? MaxRetryAfter : retryAfter;
    }
}

public static class JsonSerialization
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
