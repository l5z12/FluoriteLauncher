using System.Net.Http.Json;
using System.Text;

namespace Fluorite.Core.Diagnostics;

public sealed class MclogsResponse
{
    public bool Success { get; set; }
    public string? Id { get; set; }
    public string? Url { get; set; }
    public string? Error { get; set; }
}

public sealed class MclogsException(string message) : Exception(message);

/// <summary>Uploads a log to <see href="https://mclo.gs">mclo.gs</see>, the paste site Minecraft support channels ask for.</summary>
public sealed class MclogsClient(HttpClient http)
{
    private static readonly Uri s_endpoint = new("https://api.mclo.gs/1/log");

    /// <summary>mclo.gs keeps at most 25,000 lines and 10 MiB.</summary>
    public const int MaxLines = 25_000;
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>Uploads <paramref name="log"/> (already redacted) and returns the page's address.</summary>
    public async Task<Uri> UploadAsync(string log, CancellationToken ct = default)
    {
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("content", Fit(log))]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response = await http.PostAsync(s_endpoint, content, timeout.Token).ConfigureAwait(false);
        MclogsResponse? result;
        try
        {
            result = await response.Content.ReadFromJsonAsync(Json.FluoriteJsonContext.Default.MclogsResponse, timeout.Token).ConfigureAwait(false);
        }
        catch (System.Text.Json.JsonException)
        {
            throw new MclogsException($"mclo.gs answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }
        if (result is not { Success: true, Url: { } url } || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new MclogsException(result?.Error ?? $"mclo.gs answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return uri;
    }

    /// <summary>
    /// Shortens a log that's over mclo.gs's limits: keeps the start (versions, mods) and the end (the crash), with a
    /// note where lines were left out.
    /// </summary>
    public static string Fit(string log)
    {
        var lines = log.Split('\n');
        if (lines.Length <= MaxLines && Encoding.UTF8.GetByteCount(log) <= MaxBytes)
            return log;
        const int head = 4_000;
        var tail = MaxLines - head - 1;
        var kept = lines.Length <= MaxLines ? lines.ToList() : [.. lines[..head], $"[… {lines.Length - head - tail} lines left out …]", .. lines[^tail..]];
        var text = string.Join('\n', kept);
        // Still too big (very long lines): keep the last 10 MiB worth.
        while (Encoding.UTF8.GetByteCount(text) > MaxBytes)
            text = text[(text.Length / 10)..];
        return text;
    }
}
