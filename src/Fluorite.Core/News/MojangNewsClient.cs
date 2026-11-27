using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
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

/// <summary>An article as minecraft.net's own search lists it.</summary>
public sealed class ArticleListing
{
    public string? Title { get; set; }
    public string? Url { get; set; }
}

public sealed class ArticleSearchResult
{
    public List<ArticleListing>? Results { get; set; }
}

public sealed class ArticleSearch
{
    public ArticleSearchResult? Result { get; set; }
}

/// <summary>Where Mojang's web services are, as its discovery service tells minecraft.net.</summary>
public sealed class ServiceDiscovery
{
    public ServiceDiscoveryResult? Result { get; set; }
}

public sealed class ServiceDiscoveryResult
{
    /// <summary>By service ("net"), then environment ("prod").</summary>
    public Dictionary<string, Dictionary<string, DiscoveredService>>? ServiceEnvironments { get; set; }
}

public sealed class DiscoveredService
{
    public string? ServiceUri { get; set; }
}

/// <summary>
/// Reads the official launcher's content feeds (Java patch notes and news). Responses are cached on disk so the
/// home page has content offline.
/// </summary>
public sealed partial class MojangNewsClient(HttpClient http, string cacheDir, ILogger<MojangNewsClient>? logger = null)
{
    public const string BaseUrl = "https://launchercontent.mojang.com";

    private readonly ILogger _logger = logger ?? NullLogger<MojangNewsClient>.Instance;

    /// <summary>Where minecraft.net asks for the addresses of Mojang's web services.</summary>
    public const string DiscoveryUrl = "https://client.discovery.minecraft-services.net/api/v1.0/discovery/web/builds/1.0.0.0";

    /// <summary>What discovery says for minecraft.net's own service ("net") as of 2026-11: used until it has answered once.</summary>
    public const string DefaultNetService = "https://net-secondary.web.minecraft-services.net";

    /// <summary>
    /// minecraft.net's own search (what its news pages list), for the newest Java articles: 24, the most it gives at
    /// once, reach about two months back. The patch-notes feed has no addresses; this has them.
    /// </summary>
    private const string JavaArticlesPath = "/api/v1.0/en-us/search?pageSize=24&sortType=Recent&tag=minecraft:games/minecraft-java";

    /// <summary>How long discovery's answer is used before asking again. A move shows sooner: the search fails.</summary>
    private static readonly TimeSpan DiscoveryMaxAge = TimeSpan.FromDays(7);

    private Uri? _netService;

    /// <summary>
    /// The newest Java articles on minecraft.net, from the service discovery points to. Not kept for offline:
    /// <see cref="ArticleUri(PatchNoteEntry)"/> is.
    /// </summary>
    public async Task<IReadOnlyList<ArticleListing>> GetJavaArticlesAsync(CancellationToken ct = default)
    {
        var service = await GetNetServiceAsync(rediscover: false, ct).ConfigureAwait(false);
        try
        {
            return await SearchJavaArticlesAsync(service, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            // Maybe moved since discovery was last asked: asked again, and tried there if it's somewhere else.
            var again = await GetNetServiceAsync(rediscover: true, ct).ConfigureAwait(false);
            if (again == service)
                throw;
            return await SearchJavaArticlesAsync(again, ct).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ArticleListing>> SearchJavaArticlesAsync(Uri service, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync(new Uri(service, JavaArticlesPath), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, FluoriteJsonContext.Default.ArticleSearch)?.Result?.Results?.Where(a => a is not null).ToList() ?? [];
    }

    /// <summary>
    /// Where minecraft.net's own service is: discovery's answer, kept on disk for <see cref="DiscoveryMaxAge"/> (or
    /// asked now with <paramref name="rediscover"/>). Discovery out of reach, the answer kept from before however old,
    /// else <see cref="DefaultNetService"/>.
    /// </summary>
    private async Task<Uri> GetNetServiceAsync(bool rediscover, CancellationToken ct)
    {
        if (!rediscover && _netService is { } known)
            return known;
        var cachePath = Path.Combine(cacheDir, "discovery.json");
        if (!rediscover && File.Exists(cachePath) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath) < DiscoveryMaxAge
            && NetServiceIn(await TryReadAsync(cachePath, ct).ConfigureAwait(false)) is { } cached)
            return _netService = cached;
        try
        {
            var bytes = await http.GetByteArrayAsync(new Uri(DiscoveryUrl), ct).ConfigureAwait(false);
            var service = NetServiceIn(bytes) ?? throw new InvalidDataException("Discovery has no address for minecraft.net's service.");
            if (await CacheFile.TrySaveAsync(cachePath, bytes).ConfigureAwait(false) is { } notSaved)
                CoreLog.CacheNotSaved(_logger, notSaved, cachePath);
            return _netService = service;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException && !ct.IsCancellationRequested)
        {
            return _netService = NetServiceIn(await TryReadAsync(cachePath, ct).ConfigureAwait(false)) ?? new Uri(DefaultNetService);
        }
    }

    private static async Task<byte[]?> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // Not there, or held by another launcher sharing the folder.
        }
    }

    /// <summary>The "net" service's address in a discovery answer: an https one on Mojang's services, or null.</summary>
    private static Uri? NetServiceIn(byte[]? discovery)
    {
        if (discovery is null)
            return null;
        try
        {
            var services = JsonSerializer.Deserialize(discovery, FluoriteJsonContext.Default.ServiceDiscovery)?.Result?.ServiceEnvironments;
            return services?.GetValueOrDefault("net")?.GetValueOrDefault("prod")?.ServiceUri is { } url
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
                && uri.Host.EndsWith(".minecraft-services.net", StringComparison.OrdinalIgnoreCase)
                    ? uri
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The minecraft.net article about a patch note: the one titled like it among <paramref name="articles"/>, else
    /// <see cref="ArticleUri(PatchNoteEntry)"/>. A hotfix snapshot takes over the article of the one before (26.4
    /// Snapshot 10's is ".../minecraft-26-4-snapshot-9", retitled), which only the listing tells.
    /// </summary>
    public static Uri? ArticleUri(PatchNoteEntry entry, IEnumerable<ArticleListing> articles)
    {
        var title = TitleKey(entry.Title);
        foreach (var article in articles)
        {
            if (title.Length > 0 && TitleKey(article.Title) == title && SiteArticle(article.Url) is { } uri)
                return uri;
        }
        return ArticleUri(entry);
    }

    /// <summary>"Minecraft: Java Edition 26.3" → "minecraft java edition 26 3": case and punctuation aside.</summary>
    private static string TitleKey(string? title) =>
        string.Join(' ', NonWord().Split((title ?? "").ToLowerInvariant()).Where(w => w.Length > 0));

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    /// <summary>An article on minecraft.net itself (the search lists other sites too), without its locale.</summary>
    private static Uri? SiteArticle(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host is not ("www.minecraft.net" or "minecraft.net"))
            return null;
        // ".../en-us/article/x" → ".../article/x": the site picks the reader's own, as the news feed's links do.
        return new Uri("https://www.minecraft.net" + Locale().Replace(uri.AbsolutePath, "/", 1));
    }

    [GeneratedRegex(@"^/[a-z]{2}-[a-z]{2}/", RegexOptions.IgnoreCase)]
    private static partial Regex Locale();

    /// <summary>
    /// The minecraft.net article about a patch note, worked out from its version: the feed has no address. Every
    /// recent one follows the version, except a hotfix that has no article of its own (a "b" snapshot, a snapshot that
    /// only fixed a crash): that address leads to "page not found", so <see cref="ArticleUri(PatchNoteEntry, IEnumerable{ArticleListing})"/>
    /// comes first when the listing is in. Older versions (1.20 and before) often have other addresses too. Null for
    /// a version it can't make one of.
    /// </summary>
    public static Uri? ArticleUri(PatchNoteEntry entry)
    {
        var v = entry.Version?.ToLowerInvariant() ?? "";
        string? slug = null;
        if (ReleaseVersion().IsMatch(v))
        {
            slug = "minecraft-java-edition-" + v.Replace('.', '-');
        }
        else if (TestVersion().Match(v) is { Success: true } m)
        {
            var kind = m.Groups["kind"].Value switch { "pre" => "pre-release", "rc" => "release-candidate", var k => k };
            slug = $"minecraft-{m.Groups["release"].Value.Replace('.', '-')}-{kind}-{m.Groups["n"].Value}";
        }
        else if (WeeklySnapshot().IsMatch(v))
        {
            slug = "minecraft-snapshot-" + v;
        }
        // No locale in the address: the site picks the reader's own, as the news feed's links do.
        return slug is null ? null : new Uri("https://www.minecraft.net/article/" + slug);
    }

    /// <summary>The version's page on the Minecraft Wiki, or the Chinese one: every version has one, hotfixes too.</summary>
    public static Uri? WikiUri(PatchNoteEntry entry, bool chinese = false) =>
        entry.Version is { Length: > 0 } v
            ? new Uri((chinese ? "https://zh.minecraft.wiki/w/Java%E7%89%88" : "https://minecraft.wiki/w/Java_Edition_")
                + Uri.EscapeDataString(v.Replace(' ', '_')))
            : null;

    [GeneratedRegex(@"^\d+(\.\d+)+$")]
    private static partial Regex ReleaseVersion();

    /// <summary>"26.4-snapshot-9", "26.4-pre-1", "26.3-rc-2"; before 26.1, "1.21.4-pre1", "1.21.4-rc1".</summary>
    [GeneratedRegex(@"^(?<release>\d+(\.\d+)+)-(?<kind>snapshot|pre|rc)-?(?<n>\d+)$")]
    private static partial Regex TestVersion();

    /// <summary>"24w44a".</summary>
    [GeneratedRegex(@"^\d\dw\d\d[a-z]$")]
    private static partial Regex WeeklySnapshot();

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
