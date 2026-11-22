using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Fluorite.Core.Net;

namespace Fluorite.Modding.Platforms;

/// <summary>
/// JSON API calls that walk mirror candidates (official ↔ MCIM), honour 429 rate limits and retry transient failures.
/// </summary>
internal sealed class ApiHttp(HttpClient http, MirrorTable mirrors, Action<Uri, HttpRequestMessage>? decorate = null)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan s_maxRateLimitWait = TimeSpan.FromSeconds(30);

    public async Task<T> GetAsync<T>(string url, JsonTypeInfo<T> info, CancellationToken ct)
    {
        var bytes = await SendAsync(uri => new HttpRequestMessage(HttpMethod.Get, uri), url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, info) ?? throw new InvalidDataException($"Empty response from {url}");
    }

    public async Task<TResponse> PostAsync<TRequest, TResponse>(string url, TRequest body, JsonTypeInfo<TRequest> requestInfo, JsonTypeInfo<TResponse> responseInfo, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(body, requestInfo);
        var bytes = await SendAsync(uri =>
        {
            var content = new ByteArrayContent(payload);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        }, url, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, responseInfo) ?? throw new InvalidDataException($"Empty response from {url}");
    }

    private async Task<byte[]> SendAsync(Func<Uri, HttpRequestMessage> create, string url, CancellationToken ct)
    {
        Exception? last = null;
        Exception? fromOrigin = null;
        var origin = new Uri(MirrorTable.Canonicalize(url));
        foreach (var uri in mirrors.Candidates(url))
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    using var request = create(uri);
                    decorate?.Invoke(uri, request);
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
                    {
                        await Task.Delay(RetryDelay(response), ct).ConfigureAwait(false);
                        continue;
                    }
                    if ((int)response.StatusCode >= 500 && attempt < MaxAttempts)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(400 * attempt), ct).ConfigureAwait(false);
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        last = new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} from {uri.Host}", null, response.StatusCode);
                        if (uri == origin)
                            fromOrigin = last;
                        break; // Next candidate (a mirror may have what the origin refused, and vice versa).
                    }
                    return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    last = ex;
                }
                catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
                {
                    last = ex; // Timeout.
                }
            }
        }
        // The origin's answer (e.g. a real 404) is more telling than a mirror's.
        var error = fromOrigin ?? last;
        throw error as HttpRequestException ?? new HttpRequestException($"Could not reach {url}", error);
    }

    private static TimeSpan RetryDelay(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
            return delta < s_maxRateLimitWait ? delta : s_maxRateLimitWait;
        // Modrinth: X-Ratelimit-Reset = seconds until the window resets.
        if (response.Headers.TryGetValues("X-Ratelimit-Reset", out var values) && int.TryParse(values.FirstOrDefault(), out var seconds))
            return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, (int)s_maxRateLimitWait.TotalSeconds));
        return TimeSpan.FromSeconds(2);
    }
}
