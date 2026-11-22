using System.Globalization;
using System.Text;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Platforms.Modrinth;

/// <summary>Modrinth API v2 (https://docs.modrinth.com/api). The shared HttpClient carries the required User-Agent.</summary>
public sealed class ModrinthClient(HttpClient http, MirrorTable mirrors) : IContentPlatform
{
    public const string ApiBase = "https://api.modrinth.com/v2";
    private const int BatchSize = 100;

    private readonly ApiHttp _api = new(http, mirrors);
    private readonly Dictionary<ContentKind, IReadOnlyList<RemoteCategory>> _categories = [];

    public ContentPlatform Platform => ContentPlatform.Modrinth;
    public TextFormat TextFormat => TextFormat.Markdown;

    public static string ProjectType(ContentKind kind) => kind switch
    {
        ContentKind.Mod => "mod",
        ContentKind.ResourcePack => "resourcepack",
        ContentKind.ShaderPack => "shader",
        ContentKind.DataPack => "datapack",
        ContentKind.Modpack => "modpack",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ContentKind KindOf(string? projectType) => projectType switch
    {
        "resourcepack" => ContentKind.ResourcePack,
        "shader" => ContentKind.ShaderPack,
        "datapack" => ContentKind.DataPack,
        "modpack" => ContentKind.Modpack,
        _ => ContentKind.Mod,
    };

    public async Task<ContentSearchResult> SearchAsync(ContentSearchQuery query, CancellationToken ct = default)
    {
        var t = query.Target;
        List<string[]> facets = [[$"project_type:{ProjectType(t.Kind)}"]];
        if (t.MinecraftVersion is { Length: > 0 } mc)
            facets.Add([$"versions:{mc}"]);
        if (t.Kind is ContentKind.Mod or ContentKind.Modpack && t.Loaders.Count > 0)
            facets.Add([.. t.Loaders.Select(l => $"categories:{l}")]);
        if (query.Category is { Length: > 0 } category)
            facets.Add([$"categories:{category}"]);

        var index = query.Sort switch
        {
            ContentSort.Downloads => "downloads",
            ContentSort.Follows => "follows",
            ContentSort.Newest => "newest",
            ContentSort.Updated => "updated",
            _ => "relevance",
        };
        var url = new StringBuilder($"{ApiBase}/search?limit={Math.Clamp(query.Limit, 1, 100)}&offset={query.Offset}&index={index}");
        url.Append("&facets=").Append(Uri.EscapeDataString(FacetsJson(facets)));
        if (!string.IsNullOrWhiteSpace(query.Text))
            url.Append("&query=").Append(Uri.EscapeDataString(query.Text.Trim()));

        var r = await _api.GetAsync(url.ToString(), ModrinthJsonContext.Default.MrSearchResponse, ct).ConfigureAwait(false);
        var hits = r.Hits.Select(h => new RemoteProject(ContentPlatform.Modrinth, h.ProjectId, h.Slug, h.Title)
        {
            Summary = h.Description,
            Author = h.Author,
            IconUrl = NullIfEmpty(h.IconUrl),
            Downloads = h.Downloads,
            Follows = h.Follows,
            Updated = h.DateModified,
            Kind = KindOf(h.ProjectType),
            Categories = h.DisplayCategories ?? h.Categories ?? [],
            WebUrl = $"https://modrinth.com/{h.ProjectType ?? "mod"}/{h.Slug}",
        }).ToList();
        return new ContentSearchResult(hits, r.TotalHits);
    }

    private static string FacetsJson(List<string[]> facets)
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < facets.Count; i++)
        {
            if (i > 0)
                sb.Append(',');
            sb.Append('[').Append(string.Join(',', facets[i].Select(Quote))).Append(']');
        }
        return sb.Append(']').ToString();
    }

    private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string JsonArray(IEnumerable<string> items) => "[" + string.Join(',', items.Select(Quote)) + "]";

    public async Task<RemoteProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var p = await _api.GetAsync($"{ApiBase}/project/{Uri.EscapeDataString(projectId)}", ModrinthJsonContext.Default.MrProject, ct).ConfigureAwait(false);
        return Map(p);
    }

    public async Task<IReadOnlyList<RemoteProject>> GetProjectsAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
    {
        var result = new List<RemoteProject>();
        foreach (var batch in projectIds.Distinct().Chunk(BatchSize))
        {
            var list = await _api.GetAsync($"{ApiBase}/projects?ids={Uri.EscapeDataString(JsonArray(batch))}", ModrinthJsonContext.Default.ListMrProject, ct).ConfigureAwait(false);
            result.AddRange(list.Select(Map));
        }
        return result;
    }

    private static RemoteProject Map(MrProject p) => new(ContentPlatform.Modrinth, p.Id, p.Slug, p.Title)
    {
        Summary = p.Description,
        Body = p.Body,
        IconUrl = NullIfEmpty(p.IconUrl),
        Downloads = p.Downloads,
        Follows = p.Followers,
        Updated = p.Updated,
        Kind = KindOf(p.ProjectType),
        Categories = p.Categories ?? [],
        WebUrl = $"https://modrinth.com/{p.ProjectType ?? "mod"}/{p.Slug}",
        SourceUrl = NullIfEmpty(p.SourceUrl),
        IssuesUrl = NullIfEmpty(p.IssuesUrl),
        WikiUrl = NullIfEmpty(p.WikiUrl),
        GalleryUrls = p.Gallery?.OrderByDescending(g => g.Featured).Select(g => g.Url).ToList() ?? [],
    };

    public async Task<string?> GetDescriptionAsync(RemoteProject project, CancellationToken ct = default) =>
        project.Body ?? (await GetProjectAsync(project.Id, ct).ConfigureAwait(false)).Body;

    public async Task<IReadOnlyList<RemoteVersion>> GetVersionsAsync(string projectId, ContentTarget? target, CancellationToken ct = default)
    {
        var url = new StringBuilder($"{ApiBase}/project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false");
        if (target?.MinecraftVersion is { Length: > 0 } mc)
            url.Append("&game_versions=").Append(Uri.EscapeDataString(JsonArray([mc])));
        if (target is { Loaders.Count: > 0 })
            url.Append("&loaders=").Append(Uri.EscapeDataString(JsonArray(target.Loaders)));
        var list = await _api.GetAsync(url.ToString(), ModrinthJsonContext.Default.ListMrVersion, ct).ConfigureAwait(false);
        return list.Select(Map).OrderByDescending(v => v.Published).ToList();
    }

    public async Task<RemoteVersion> GetVersionAsync(string projectId, string versionId, CancellationToken ct = default) =>
        Map(await _api.GetAsync($"{ApiBase}/version/{Uri.EscapeDataString(versionId)}", ModrinthJsonContext.Default.MrVersion, ct).ConfigureAwait(false));

    public async Task<string?> GetChangelogAsync(RemoteVersion version, CancellationToken ct = default) =>
        version.Changelog ?? (await GetVersionAsync(version.ProjectId, version.Id, ct).ConfigureAwait(false)).Changelog;

    internal static RemoteVersion Map(MrVersion v) => new(ContentPlatform.Modrinth, v.ProjectId, v.Id, v.Name, v.VersionNumber)
    {
        Channel = v.VersionType switch
        {
            "beta" => ReleaseChannel.Beta,
            "alpha" => ReleaseChannel.Alpha,
            _ => ReleaseChannel.Release,
        },
        Published = v.DatePublished,
        GameVersions = v.GameVersions ?? [],
        Loaders = v.Loaders ?? [],
        Downloads = v.Downloads,
        Changelog = v.Changelog,
        Files = v.Files.Select(f => new RemoteFile(f.Filename, f.Size)
        {
            Url = f.Url,
            Sha1 = f.Hashes?.GetValueOrDefault("sha1"),
            Sha512 = f.Hashes?.GetValueOrDefault("sha512"),
            Primary = f.Primary,
        }).ToList(),
        Dependencies = (v.Dependencies ?? []).Select(d => new RemoteDependency(
            d.DependencyType switch
            {
                "optional" => RemoteDependencyKind.Optional,
                "incompatible" => RemoteDependencyKind.Incompatible,
                "embedded" => RemoteDependencyKind.Embedded,
                _ => RemoteDependencyKind.Required,
            },
            d.ProjectId, d.VersionId) { FileName = d.FileName }).ToList(),
    };

    public async Task<IReadOnlyList<RemoteCategory>> GetCategoriesAsync(ContentKind kind, CancellationToken ct = default)
    {
        lock (_categories)
        {
            if (_categories.TryGetValue(kind, out var cached))
                return cached;
        }
        var all = await _api.GetAsync($"{ApiBase}/tag/category", ModrinthJsonContext.Default.ListMrCategory, ct).ConfigureAwait(false);
        var type = ProjectType(kind);
        var list = all
            .Where(c => c.ProjectType == type && c.Header is not "resolutions" and not "performance impact")
            .Select(c => new RemoteCategory(c.Name, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(c.Name.Replace('-', ' ')), null))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        lock (_categories)
            _categories[kind] = list;
        return list;
    }

    public async Task<IReadOnlyList<IdentifiedFile>> IdentifyAsync(IReadOnlyCollection<ContentHashes> files, CancellationToken ct = default)
    {
        var result = new List<IdentifiedFile>();
        var bySha1 = files.GroupBy(f => f.Sha1).ToDictionary(g => g.Key, g => g.First());
        foreach (var batch in bySha1.Keys.Chunk(500))
        {
            var map = await _api.PostAsync($"{ApiBase}/version_files", new MrHashesRequest { Hashes = [.. batch] },
                ModrinthJsonContext.Default.MrHashesRequest, ModrinthJsonContext.Default.DictionaryStringMrVersion, ct).ConfigureAwait(false);
            foreach (var (hash, version) in map)
            {
                if (bySha1.TryGetValue(hash, out var f))
                    result.Add(new IdentifiedFile(f.Sha1, f.Murmur2, Map(version)));
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, RemoteVersion>> GetUpdatesAsync(IReadOnlyCollection<InstalledRef> installed, ContentTarget target, CancellationToken ct = default)
    {
        var result = new Dictionary<string, RemoteVersion>();
        var bySha1 = installed.GroupBy(i => i.Sha1).ToDictionary(g => g.Key, g => g.First());
        foreach (var batch in bySha1.Keys.Chunk(500))
        {
            var request = new MrUpdateRequest
            {
                Hashes = [.. batch],
                Loaders = target.Loaders.Count > 0 ? [.. target.Loaders] : null,
                GameVersions = target.MinecraftVersion is { } mc ? [mc] : null,
            };
            var map = await _api.PostAsync($"{ApiBase}/version_files/update", request,
                ModrinthJsonContext.Default.MrUpdateRequest, ModrinthJsonContext.Default.DictionaryStringMrVersion, ct).ConfigureAwait(false);
            foreach (var (hash, version) in map)
            {
                if (bySha1.TryGetValue(hash, out var current) && version.Id != current.VersionId)
                    result[current.VersionId] = Map(version);
            }
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
