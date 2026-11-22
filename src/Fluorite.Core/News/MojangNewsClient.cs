using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core.Json;

namespace Fluorite.Core.News;

public sealed class NewsImage
{
    public string? Title { get; set; }
    public string? Url { get; set; }
}

/// <summary>An entry of <c>javaPatchNotes.json</c>.</summary>
public sealed class PatchNoteEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string? Type { get; set; }
    public NewsImage? Image { get; set; }
    public string? ContentPath { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string? ShortText { get; set; }
}

public sealed class PatchNotesFeed
{
    public List<PatchNoteEntry> Entries { get; set; } = [];
}

public sealed class PatchNoteContent
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Version { get; set; }
    public string Body { get; set; } = "";
    public NewsImage? Image { get; set; }
}

/// <summary>An entry of <c>news.json</c>.</summary>
public sealed class NewsEntry
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Tag { get; set; }
    public string? Category { get; set; }
    public string? Date { get; set; }
    public string? Text { get; set; }
    public NewsImage? PlayPageImage { get; set; }
    public NewsImage? NewsPageImage { get; set; }
    public string? ReadMoreLink { get; set; }
    public List<string>? NewsType { get; set; }
}

public sealed class NewsFeed
{
    public List<NewsEntry> Entries { get; set; } = [];
}

/// <summary>
/// Reads the official launcher's content feeds (Java patch notes and news). Responses are cached on disk so the
/// home page has content offline.
/// </summary>
public sealed class MojangNewsClient(HttpClient http, string cacheDir)
{
    public const string BaseUrl = "https://launchercontent.mojang.com";

    public static Uri ImageUri(NewsImage? image) =>
        image?.Url is { Length: > 0 } url
            ? new Uri(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url)
            : new Uri(BaseUrl + "/");

    public async Task<IReadOnlyList<PatchNoteEntry>> GetPatchNotesAsync(CancellationToken ct = default) =>
        (await GetCachedAsync("/v2/javaPatchNotes.json", "patch-notes.json", FluoriteJsonContext.Default.PatchNotesFeed, ct).ConfigureAwait(false))?.Entries ?? [];

    /// <summary>Java-relevant news (entries tagged "Java").</summary>
    public async Task<IReadOnlyList<NewsEntry>> GetJavaNewsAsync(CancellationToken ct = default)
    {
        var feed = await GetCachedAsync("/v2/news.json", "news.json", FluoriteJsonContext.Default.NewsFeed, ct).ConfigureAwait(false);
        return feed?.Entries.Where(e => e.NewsType?.Contains("Java", StringComparer.OrdinalIgnoreCase) ?? false).ToList() ?? [];
    }

    public async Task<PatchNoteContent?> GetPatchNoteAsync(PatchNoteEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.ContentPath))
            return null;
        var safeName = "patch-" + string.Concat(entry.Id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')) + ".json";
        return await GetCachedAsync("/v2/" + entry.ContentPath.TrimStart('/'), safeName, FluoriteJsonContext.Default.PatchNoteContent, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetCachedAsync<T>(string path, string cacheName, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct)
        where T : class
    {
        var cachePath = Path.Combine(cacheDir, cacheName);
        try
        {
            var bytes = await http.GetByteArrayAsync(new Uri(BaseUrl + path), ct).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize(bytes, info);
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(cachePath, bytes, ct).ConfigureAwait(false);
            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            if (!File.Exists(cachePath))
                return null;
            await using var fs = File.OpenRead(cachePath);
            return await JsonSerializer.DeserializeAsync(fs, info, ct).ConfigureAwait(false);
        }
    }
}
