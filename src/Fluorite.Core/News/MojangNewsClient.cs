using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core.Json;
using Fluorite.Core.Logging;
using Fluorite.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
public sealed class MojangNewsClient(HttpClient http, string cacheDir, ILogger<MojangNewsClient>? logger = null)
{
    public const string BaseUrl = "https://launchercontent.mojang.com";

    private readonly ILogger _logger = logger ?? NullLogger<MojangNewsClient>.Instance;

    /// <summary>
    /// Null for an entry without a picture (not the site's front page, which isn't one), or with an address that
    /// isn't one ("http://"): that card goes without, and the others still show.
    /// </summary>
    public static Uri? ImageUri(NewsImage? image) =>
        image?.Url is { Length: > 0 } url
        && Uri.TryCreate(url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : BaseUrl + url, UriKind.Absolute, out var uri)
            ? uri
            : null;

    // A feed whose "entries" is null, or lists a null, has nothing (more) to show rather than failing: thrown, it would
    // take the other feed down with it on Home. Nor is it saved over the copy kept for offline (HasEntries).

    public async Task<IReadOnlyList<PatchNoteEntry>> GetPatchNotesAsync(CancellationToken ct = default) =>
        (await GetCachedAsync("/v2/javaPatchNotes.json", "patch-notes.json", FluoriteJsonContext.Default.PatchNotesFeed, f => HasEntries(f.Entries), ct).ConfigureAwait(false))
            ?.Entries?.Where(e => e is not null).ToList() ?? [];

    /// <summary>Java-relevant news (entries tagged "Java").</summary>
    public async Task<IReadOnlyList<NewsEntry>> GetJavaNewsAsync(CancellationToken ct = default)
    {
        var feed = await GetCachedAsync("/v2/news.json", "news.json", FluoriteJsonContext.Default.NewsFeed, f => HasEntries(f.Entries), ct).ConfigureAwait(false);
        return feed?.Entries?.Where(e => e?.NewsType?.Contains("Java", StringComparer.OrdinalIgnoreCase) ?? false).ToList() ?? [];
    }

    public async Task<PatchNoteContent?> GetPatchNoteAsync(PatchNoteEntry entry, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(entry.ContentPath))
            return null;
        var safeName = "patch-" + string.Concat(entry.Id.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_')) + ".json";
        return await GetCachedAsync("/v2/" + entry.ContentPath.TrimStart('/'), safeName, FluoriteJsonContext.Default.PatchNoteContent,
            c => !string.IsNullOrEmpty(c.Body), ct).ConfigureAwait(false);
    }

    private static bool HasEntries<T>(List<T>? entries) where T : class => entries?.Any(e => e is not null) == true;

    /// <param name="usable">
    /// Whether an answer has something to show. One that doesn't ("{}", an empty list, served with 200) isn't saved
    /// over the copy kept for offline; that copy is used instead, as when offline.
    /// </param>
    private async Task<T?> GetCachedAsync<T>(string path, string cacheName, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info,
        Func<T, bool> usable, CancellationToken ct)
        where T : class
    {
        var cachePath = Path.Combine(cacheDir, cacheName);
        try
        {
            var bytes = await http.GetByteArrayAsync(new Uri(BaseUrl + path), ct).ConfigureAwait(false);
            var value = JsonSerializer.Deserialize(bytes, info) is { } v && usable(v) ? v : throw new InvalidDataException($"{path} has nothing to show.");
            if (await CacheFile.TrySaveAsync(cachePath, bytes).ConfigureAwait(false) is { } notSaved)
                CoreLog.CacheNotSaved(_logger, notSaved, cachePath);
            return value;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException && !ct.IsCancellationRequested)
        {
            if (!File.Exists(cachePath))
                return null;
            await using var fs = File.OpenRead(cachePath);
            return await JsonSerializer.DeserializeAsync(fs, info, ct).ConfigureAwait(false);
        }
    }
}
