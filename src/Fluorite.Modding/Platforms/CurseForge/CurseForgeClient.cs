using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Platforms.CurseForge;

/// <summary>
/// CurseForge for Studios API v1 (https://docs.curseforge.com/rest-api). Requires an API key, sent only to
/// <c>api.curseforge.com</c> (never to mirrors). Files whose authors opted out of third-party distribution have no
/// download URL; they're returned with <see cref="RemoteFile.ManualDownloadUrl"/> so the user can fetch them.
/// </summary>
public sealed class CurseForgeClient : IContentPlatform
{
    public const string ApiBase = "https://api.curseforge.com/v1";
    public const int MinecraftGameId = 432;
    private const int PageSize = 50;
    private const int MaxFilePages = 20;

    private readonly ApiHttp _api;
    private readonly ConcurrentDictionary<long, string> _webUrls = new();
    private readonly ConcurrentDictionary<ContentKind, IReadOnlyList<RemoteCategory>> _categories = new();

    public CurseForgeClient(HttpClient http, MirrorTable mirrors, string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _api = new ApiHttp(http, mirrors, (uri, request) =>
        {
            if (uri.Host.Equals("api.curseforge.com", StringComparison.OrdinalIgnoreCase))
                request.Headers.Add("x-api-key", apiKey);
        });
    }

    public ContentPlatform Platform => ContentPlatform.CurseForge;
    public TextFormat TextFormat => TextFormat.Html;

    public static int ClassId(ContentKind kind) => kind switch
    {
        ContentKind.Mod => 6,
        ContentKind.ResourcePack => 12,
        ContentKind.ShaderPack => 6552,
        ContentKind.DataPack => 6945,
        ContentKind.Modpack => 4471,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ContentKind KindOf(int classId) => classId switch
    {
        12 => ContentKind.ResourcePack,
        6552 => ContentKind.ShaderPack,
        6945 => ContentKind.DataPack,
        4471 => ContentKind.Modpack,
        _ => ContentKind.Mod,
    };

    /// <summary>CurseForge <c>ModLoaderType</c> for a platform loader name.</summary>
    public static int? LoaderType(string loader) => loader switch
    {
        "forge" => 1,
        "liteloader" => 3,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null,
    };

    private static readonly string[] s_loaderNames = ["forge", "neoforge", "fabric", "quilt", "liteloader"];

    public async Task<ContentSearchResult> SearchAsync(ContentSearchQuery query, CancellationToken ct = default)
    {
        var t = query.Target;
        var pageSize = Math.Clamp(query.Limit, 1, PageSize);
        var index = Math.Min(query.Offset, 10_000 - pageSize);
        var sortField = query.Sort switch
        {
            ContentSort.Downloads => 6,
            ContentSort.Follows => 12,
            ContentSort.Newest => 11,
            ContentSort.Updated => 3,
            _ => 2,
        };
        var url = new StringBuilder($"{ApiBase}/mods/search?gameId={MinecraftGameId}&classId={ClassId(t.Kind)}&index={index}&pageSize={pageSize}&sortField={sortField}&sortOrder=desc");
        if (!string.IsNullOrWhiteSpace(query.Text))
            url.Append("&searchFilter=").Append(Uri.EscapeDataString(query.Text.Trim()));
        if (t.MinecraftVersion is { Length: > 0 } mc)
            url.Append("&gameVersion=").Append(Uri.EscapeDataString(mc));
        if (t.Kind is ContentKind.Mod or ContentKind.Modpack)
        {
            var types = t.Loaders.Select(LoaderType).OfType<int>().Distinct().ToList();
            if (types.Count > 0)
                url.Append("&modLoaderTypes=").Append(Uri.EscapeDataString("[" + string.Join(',', types) + "]"));
        }
        if (query.Category is { Length: > 0 } category)
            url.Append("&categoryId=").Append(Uri.EscapeDataString(category));

        var r = await _api.GetAsync(url.ToString(), CurseForgeJsonContext.Default.CfResponseListCfMod, ct).ConfigureAwait(false);
        var hits = (r.Data ?? []).Select(Map).ToList();
        return new ContentSearchResult(hits, Math.Min(r.Pagination?.TotalCount ?? hits.Count, 10_000));
    }

    /// <summary>A project by its URL slug (<c>curseforge.com/minecraft/modpacks/&lt;slug&gt;</c>).</summary>
    public async Task<RemoteProject?> FindBySlugAsync(string slug, ContentKind kind, CancellationToken ct = default)
    {
        var r = await _api.GetAsync($"{ApiBase}/mods/search?gameId={MinecraftGameId}&classId={ClassId(kind)}&slug={Uri.EscapeDataString(slug)}",
            CurseForgeJsonContext.Default.CfResponseListCfMod, ct).ConfigureAwait(false);
        return r.Data?.FirstOrDefault(m => string.Equals(m.Slug, slug, StringComparison.OrdinalIgnoreCase)) is { } mod ? Map(mod) : null;
    }

    private RemoteProject Map(CfMod m)
    {
        if (m.Links?.WebsiteUrl is { Length: > 0 } web)
            _webUrls[m.Id] = web;
        return new RemoteProject(ContentPlatform.CurseForge, m.Id.ToString(CultureInfo.InvariantCulture), m.Slug, m.Name)
        {
            Summary = m.Summary,
            Author = m.Authors?.FirstOrDefault()?.Name,
            IconUrl = m.Logo?.ThumbnailUrl ?? m.Logo?.Url,
            Downloads = m.DownloadCount,
            Follows = m.ThumbsUpCount,
            Updated = m.DateModified,
            Kind = KindOf(m.ClassId),
            Categories = m.Categories?.Select(c => c.Name).ToList() ?? [],
            WebUrl = m.Links?.WebsiteUrl,
            SourceUrl = Empty(m.Links?.SourceUrl),
            IssuesUrl = Empty(m.Links?.IssuesUrl),
            WikiUrl = Empty(m.Links?.WikiUrl),
            GalleryUrls = m.Screenshots?.Select(s => s.Url).OfType<string>().ToList() ?? [],
        };
    }

    public async Task<RemoteProject> GetProjectAsync(string projectId, CancellationToken ct = default)
    {
        var r = await _api.GetAsync($"{ApiBase}/mods/{Id(projectId)}", CurseForgeJsonContext.Default.CfResponseCfMod, ct).ConfigureAwait(false);
        return Map(r.Data ?? throw new InvalidDataException($"CurseForge project {projectId} not found."));
    }

    public async Task<IReadOnlyList<RemoteProject>> GetProjectsAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
    {
        var mods = await GetModsAsync(projectIds.Select(Id), ct).ConfigureAwait(false);
        return mods.Select(Map).ToList();
    }

    private async Task<List<CfMod>> GetModsAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var result = new List<CfMod>();
        foreach (var batch in ids.Distinct().Chunk(500))
        {
            var r = await _api.PostAsync($"{ApiBase}/mods", new CfModsRequest { ModIds = [.. batch] },
                CurseForgeJsonContext.Default.CfModsRequest, CurseForgeJsonContext.Default.CfResponseListCfMod, ct).ConfigureAwait(false);
            result.AddRange(r.Data ?? []);
        }
        foreach (var m in result)
        {
            if (m.Links?.WebsiteUrl is { Length: > 0 } web)
                _webUrls[m.Id] = web;
        }
        return result;
    }

    public async Task<string?> GetDescriptionAsync(RemoteProject project, CancellationToken ct = default)
    {
        var r = await _api.GetAsync($"{ApiBase}/mods/{Id(project.Id)}/description", CurseForgeJsonContext.Default.CfResponseString, ct).ConfigureAwait(false);
        return r.Data;
    }

    public async Task<IReadOnlyList<RemoteVersion>> GetVersionsAsync(string projectId, ContentTarget? target, CancellationToken ct = default)
    {
        var files = new List<CfFile>();
        for (var page = 0; page < MaxFilePages; page++)
        {
            var url = $"{ApiBase}/mods/{Id(projectId)}/files?index={page * PageSize}&pageSize={PageSize}";
            if (target?.MinecraftVersion is { Length: > 0 } mc)
                url += "&gameVersion=" + Uri.EscapeDataString(mc);
            var r = await _api.GetAsync(url, CurseForgeJsonContext.Default.CfResponseListCfFile, ct).ConfigureAwait(false);
            var batch = r.Data ?? [];
            files.AddRange(batch);
            if (batch.Count < PageSize || files.Count >= (r.Pagination?.TotalCount ?? 0))
                break;
        }

        var versions = files.Where(f => f.IsServerPack != true).Select(Map).Where(v => Compatible(v, target)).OrderByDescending(v => v.Published).ToList();
        return await WithManualUrlsAsync(versions, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Old CurseForge files often carry no loader tag; treat untagged mod files as Forge (the only loader back then).
    /// </summary>
    private static bool Compatible(RemoteVersion v, ContentTarget? target)
    {
        if (target is null || target.Kind != ContentKind.Mod || target.Loaders.Count == 0)
            return true;
        if (v.Loaders.Count == 0)
            return target.Loaders.Contains("forge");
        return v.Loaders.Any(target.Loaders.Contains);
    }

    public async Task<RemoteVersion> GetVersionAsync(string projectId, string versionId, CancellationToken ct = default)
    {
        var r = await _api.GetAsync($"{ApiBase}/mods/{Id(projectId)}/files/{Id(versionId)}", CurseForgeJsonContext.Default.CfResponseCfFile, ct).ConfigureAwait(false);
        var v = Map(r.Data ?? throw new InvalidDataException($"CurseForge file {versionId} not found."));
        return (await WithManualUrlsAsync([v], ct).ConfigureAwait(false))[0];
    }

    public async Task<string?> GetChangelogAsync(RemoteVersion version, CancellationToken ct = default)
    {
        var r = await _api.GetAsync($"{ApiBase}/mods/{Id(version.ProjectId)}/files/{Id(version.Id)}/changelog", CurseForgeJsonContext.Default.CfResponseString, ct).ConfigureAwait(false);
        return r.Data;
    }

    internal static RemoteVersion Map(CfFile f)
    {
        var tags = f.GameVersions ?? [];
        return new RemoteVersion(ContentPlatform.CurseForge, f.ModId.ToString(CultureInfo.InvariantCulture), f.Id.ToString(CultureInfo.InvariantCulture), f.DisplayName, VersionNumberOf(f))
        {
            Channel = f.ReleaseType switch
            {
                2 => ReleaseChannel.Beta,
                3 => ReleaseChannel.Alpha,
                _ => ReleaseChannel.Release,
            },
            Published = f.FileDate,
            Downloads = f.DownloadCount,
            GameVersions = tags.Where(t => t.Length > 0 && char.IsAsciiDigit(t[0])).ToList(),
            Loaders = tags.Select(t => t.ToLowerInvariant()).Where(t => s_loaderNames.Contains(t)).ToList(),
            Files =
            [
                new RemoteFile(f.FileName, f.FileLength)
                {
                    Url = string.IsNullOrEmpty(f.DownloadUrl) ? null : f.DownloadUrl,
                    Sha1 = f.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                    Primary = true,
                },
            ],
            Dependencies = (f.Dependencies ?? [])
                .Where(d => d.RelationType is 1 or 2 or 3 or 5 or 6)
                .Select(d => new RemoteDependency(
                    d.RelationType switch
                    {
                        3 => RemoteDependencyKind.Required,
                        2 => RemoteDependencyKind.Optional,
                        5 => RemoteDependencyKind.Incompatible,
                        _ => RemoteDependencyKind.Embedded,
                    },
                    d.ModId.ToString(CultureInfo.InvariantCulture), null))
                .ToList(),
        };
    }

    /// <summary>CurseForge has no separate version number; the display name is the closest thing.</summary>
    private static string VersionNumberOf(CfFile f) =>
        string.IsNullOrWhiteSpace(f.DisplayName) ? Path.GetFileNameWithoutExtension(f.FileName) : f.DisplayName;

    /// <summary>Blocked files get the project's download page so the user can fetch them in the browser.</summary>
    private async Task<IReadOnlyList<RemoteVersion>> WithManualUrlsAsync(IReadOnlyList<RemoteVersion> versions, CancellationToken ct)
    {
        var blocked = versions.Where(v => v.Files.Any(f => f.Url is null)).ToList();
        if (blocked.Count == 0)
            return versions;
        var missing = blocked.Select(v => Id(v.ProjectId)).Where(id => !_webUrls.ContainsKey(id)).Distinct().ToList();
        if (missing.Count > 0)
            await GetModsAsync(missing, ct).ConfigureAwait(false);

        return versions.Select(v => v.Files.All(f => f.Url is not null) || !_webUrls.TryGetValue(Id(v.ProjectId), out var web)
            ? v
            : v with { Files = v.Files.Select(f => f.Url is null ? f with { ManualDownloadUrl = $"{web.TrimEnd('/')}/download/{v.Id}" } : f).ToList() }).ToList();
    }

    public async Task<IReadOnlyList<RemoteCategory>> GetCategoriesAsync(ContentKind kind, CancellationToken ct = default)
    {
        if (_categories.TryGetValue(kind, out var cached))
            return cached;
        var classId = ClassId(kind);
        var r = await _api.GetAsync($"{ApiBase}/categories?gameId={MinecraftGameId}&classId={classId}", CurseForgeJsonContext.Default.CfResponseListCfCategory, ct).ConfigureAwait(false);
        var list = (r.Data ?? [])
            .Where(c => c.IsClass != true)
            .Select(c => new RemoteCategory(c.Id.ToString(CultureInfo.InvariantCulture), c.Name, c.IconUrl))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        _categories[kind] = list;
        return list;
    }

    public async Task<IReadOnlyList<IdentifiedFile>> IdentifyAsync(IReadOnlyCollection<ContentHashes> files, CancellationToken ct = default)
    {
        var result = new List<IdentifiedFile>();
        var byPrint = files.GroupBy(f => f.Murmur2).ToDictionary(g => g.Key, g => g.First());
        foreach (var batch in byPrint.Keys.Chunk(500))
        {
            var r = await _api.PostAsync($"{ApiBase}/fingerprints/{MinecraftGameId}", new CfFingerprintsRequest { Fingerprints = [.. batch] },
                CurseForgeJsonContext.Default.CfFingerprintsRequest, CurseForgeJsonContext.Default.CfResponseCfFingerprintMatches, ct).ConfigureAwait(false);
            foreach (var match in r.Data?.ExactMatches ?? [])
            {
                if (match.File is { } file && byPrint.TryGetValue((uint)file.FileFingerprint, out var local))
                    result.Add(new IdentifiedFile(local.Sha1, local.Murmur2, Map(file)));
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, RemoteVersion>> GetUpdatesAsync(IReadOnlyCollection<InstalledRef> installed, ContentTarget target, CancellationToken ct = default)
    {
        var result = new Dictionary<string, RemoteVersion>();
        if (installed.Count == 0)
            return result;
        var mods = await GetModsAsync(installed.Select(i => Id(i.ProjectId)), ct).ConfigureAwait(false);
        var loaderTypes = target.Loaders.Select(LoaderType).OfType<int>().ToHashSet();

        var newest = new Dictionary<long, long>(); // installed fileId → newest fileId
        foreach (var i in installed)
        {
            var mod = mods.FirstOrDefault(m => m.Id == Id(i.ProjectId));
            if (mod?.LatestFilesIndexes is not { } indexes)
                continue;
            var candidates = indexes
                .Where(x => target.MinecraftVersion is null || x.GameVersion == target.MinecraftVersion)
                .Where(x => target.Kind != ContentKind.Mod || loaderTypes.Count == 0 || x.ModLoader is null or 0 || loaderTypes.Contains(x.ModLoader.Value))
                .ToList();
            var pick = candidates.Where(x => x.ReleaseType == 1).MaxBy(x => x.FileId) ?? candidates.MaxBy(x => x.FileId);
            var current = Id(i.VersionId);
            if (pick is not null && pick.FileId > current)
                newest[current] = pick.FileId;
        }
        if (newest.Count == 0)
            return result;

        var files = new List<CfFile>();
        foreach (var batch in newest.Values.Distinct().Chunk(500))
        {
            var r = await _api.PostAsync($"{ApiBase}/mods/files", new CfFilesRequest { FileIds = [.. batch] },
                CurseForgeJsonContext.Default.CfFilesRequest, CurseForgeJsonContext.Default.CfResponseListCfFile, ct).ConfigureAwait(false);
            files.AddRange(r.Data ?? []);
        }
        var mapped = await WithManualUrlsAsync(files.Select(Map).ToList(), ct).ConfigureAwait(false);
        foreach (var (current, latest) in newest)
        {
            if (mapped.FirstOrDefault(v => v.Id == latest.ToString(CultureInfo.InvariantCulture)) is { } v)
                result[current.ToString(CultureInfo.InvariantCulture)] = v;
        }
        return result;
    }

    /// <summary>
    /// Looks up files by id (a CurseForge modpack's <c>manifest.json</c> lists only project/file ids): name, URL (null
    /// when the author blocks third-party downloads), SHA-1, size, and the project's class (mod, resource pack…).
    /// Files that no longer exist are left out.
    /// </summary>
    public async Task<IReadOnlyList<CurseForgeFileInfo>> GetFilesAsync(IReadOnlyCollection<long> fileIds, CancellationToken ct = default)
    {
        var files = new List<CfFile>();
        foreach (var batch in fileIds.Distinct().Chunk(500))
        {
            var r = await _api.PostAsync($"{ApiBase}/mods/files", new CfFilesRequest { FileIds = [.. batch] },
                CurseForgeJsonContext.Default.CfFilesRequest, CurseForgeJsonContext.Default.CfResponseListCfFile, ct).ConfigureAwait(false);
            files.AddRange(r.Data ?? []);
        }
        var mods = (await GetModsAsync(files.Select(f => f.ModId), ct).ConfigureAwait(false)).ToDictionary(m => m.Id);
        return files.Select(f =>
        {
            mods.TryGetValue(f.ModId, out var mod);
            var web = mod?.Links?.WebsiteUrl;
            return new CurseForgeFileInfo(f.ModId, f.Id, f.FileName, string.IsNullOrEmpty(f.DownloadUrl) ? null : f.DownloadUrl,
                f.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value, f.FileLength, mod?.ClassId ?? 6, mod?.Name, f.DisplayName,
                web is null ? null : $"{web.TrimEnd('/')}/download/{f.Id}")
            {
                IconUrl = mod?.Logo?.ThumbnailUrl,
                WebUrl = web,
            };
        }).ToList();
    }

    private static long Id(string id) => long.Parse(id, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string? Empty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
