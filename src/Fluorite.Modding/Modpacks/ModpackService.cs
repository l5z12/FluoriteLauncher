using System.Globalization;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Platforms.CurseForge;
using Fluorite.Modding.Platforms.Modrinth;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Modpacks;

/// <summary>A newer version of an installed pack.</summary>
public sealed record PackUpdateInfo(string VersionId, string VersionName, DateTimeOffset? Published, RemoteVersion? Version = null);

/// <summary>
/// Turns anything that names a modpack into a <see cref="PackManifest"/> ready to install: a local archive, an
/// http(s) archive, a packwiz <c>pack.toml</c>, a Modrinth/CurseForge/FTB/Technic page URL or <c>platform:id[@version]</c>,
/// or a Modrinth/CurseForge modpack version. Also finds newer versions of installed packs.
/// </summary>
public sealed class ModpackService(ContentService content, HttpClient http, DownloadEngine downloader, MirrorTable mirrors)
{
    public ModpackInstaller Installer { get; } = new(downloader, mirrors);
    public ModpackExporter Exporter { get; } = new(content);
    public FtbClient Ftb { get; } = new(http);
    public TechnicClient Technic { get; } = new(http);

    private CurseForgeClient? CurseForge => content.Has(ContentPlatform.CurseForge) ? (CurseForgeClient)content.Get(ContentPlatform.CurseForge) : null;

    /// <summary>A pack file on disk or behind a direct link; given its project's icon when it carries none.</summary>
    public async Task<PackManifest> LoadFileAsync(string archive, CancellationToken ct = default)
    {
        var manifest = await ReadFileAsync(archive, ct).ConfigureAwait(false);
        await FindProjectAsync(manifest, archive, ct).ConfigureAwait(false);
        return manifest;
    }

    private async Task<PackManifest> ReadFileAsync(string archive, CancellationToken ct)
    {
        var manifest = await Task.Run(() => ModpackReader.Read(archive), ct).ConfigureAwait(false);
        await ResolveAsync(manifest, ct).ConfigureAwait(false);
        return manifest;
    }

    /// <summary>
    /// A Modrinth or CurseForge pack file doesn't say which project it's from, and rarely carries an icon. Its hash
    /// finds it on its own platform, for the icon and summary a pack installed from the site gets. Best effort: offline,
    /// or for a pack that was never published, it just keeps none.
    /// </summary>
    private static readonly TimeSpan s_lookupTimeout = TimeSpan.FromSeconds(15);

    private async Task FindProjectAsync(PackManifest manifest, string archive, CancellationToken ct)
    {
        if (manifest.IconEntry is not null || manifest.IconUrl is not null || manifest.IconKey is not null)
            return;
        ContentPlatform? platform = manifest.Format switch
        {
            PackFormat.Modrinth => ContentPlatform.Modrinth,
            PackFormat.CurseForge => ContentPlatform.CurseForge,
            _ => null,
        };
        if (platform is not { } p || !content.Has(p))
            return;
        try
        {
            var client = content.Get(p);
            var hashes = await ContentHashes.ComputeAsync(archive, ct).ConfigureAwait(false);
            // The shared HttpClient never times out; a stalled answer mustn't keep the pack from installing.
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(s_lookupTimeout);
            var matches = await client.IdentifyAsync([hashes], limit.Token).ConfigureAwait(false);
            if (matches.Count == 0 || await TryGetProjectAsync(client, matches[0].Version.ProjectId, limit.Token).ConfigureAwait(false) is not { } project)
                return;
            manifest.IconUrl = project.IconUrl;
            manifest.Summary ??= project.Summary;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or IOException or System.Text.Json.JsonException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
        }
    }

    private IContentPlatform? Modrinth => content.Has(ContentPlatform.Modrinth) ? content.Get(ContentPlatform.Modrinth) : null;

    private async Task ResolveAsync(PackManifest manifest, CancellationToken ct)
    {
        if (manifest.UnresolvedCurseForge.Count > 0)
        {
            var cf = CurseForge ?? throw new InvalidOperationException("This pack lists CurseForge files, which need the CurseForge API.");
            await CurseForgePacks.ResolveAsync(manifest, cf, ct).ConfigureAwait(false);
        }
        await CurseForgePacks.FillBlockedFromModrinthAsync(manifest, Modrinth, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads a Modrinth/CurseForge modpack version's file and reads it.</summary>
    public async Task<PackManifest> LoadVersionAsync(RemoteVersion version, string workDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var file = version.PrimaryFile ?? throw new InvalidDataException($"'{version.Name}' has no file.");
        if (file.Url is null)
            throw new InvalidOperationException($"The author of '{version.Name}' doesn't allow launchers to download it. Download it from {file.ManualDownloadUrl ?? "the project page"} and open the file in Fluorite.");
        Directory.CreateDirectory(workDir);
        var path = Path.Combine(workDir, PackPaths.Sanitize(file.FileName)?.Replace('/', '_') ?? "pack.zip");
        await downloader.DownloadAllAsync([new DownloadItem(mirrors.Candidates(file.Url), path, file.Sha1, file.Size > 0 ? file.Size : null) { DisplayName = file.FileName }], progress, ct).ConfigureAwait(false);
        // Not LoadFileAsync: the project is known, and LoadProjectAsync takes its icon from there.
        var manifest = await ReadFileAsync(path, ct).ConfigureAwait(false);
        manifest.Source = new PackSource(version.Platform == ContentPlatform.Modrinth ? PackFormat.Modrinth : PackFormat.CurseForge, version.ProjectId, version.Id, version.VersionNumber);
        manifest.Version ??= version.VersionNumber;
        return manifest;
    }

    /// <summary>The newest suitable version of a Modrinth/CurseForge modpack project.</summary>
    public async Task<RemoteVersion> LatestVersionAsync(ContentPlatform platform, string projectId, ReleaseChannel maxChannel = ReleaseChannel.Release, CancellationToken ct = default)
    {
        var versions = await content.Get(platform).GetVersionsAsync(projectId, new ContentTarget(ContentKind.Modpack, null, []), ct).ConfigureAwait(false);
        return versions.Where(v => v.Channel <= maxChannel).OrderByDescending(v => v.Published).FirstOrDefault()
            ?? versions.OrderByDescending(v => v.Published).FirstOrDefault()
            ?? throw new InvalidDataException("The project has no versions.");
    }

    /// <summary>Anything a user may paste or a <c>fluorite://install</c> link may carry.</summary>
    public async Task<PackManifest> LoadLocationAsync(string location, string workDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        location = location.Trim().Trim('"');
        if (File.Exists(location))
        {
            return location.EndsWith(".toml", StringComparison.OrdinalIgnoreCase)
                ? await ReadPackwizAsync(PackwizReader.NormalizeLocation(location), ct).ConfigureAwait(false)
                : await LoadFileAsync(location, ct).ConfigureAwait(false);
        }
        if (Directory.Exists(location) && File.Exists(Path.Combine(location, "pack.toml")))
            return await ReadPackwizAsync(PackwizReader.NormalizeLocation(location), ct).ConfigureAwait(false);

        var (scheme, id, version) = SplitShortForm(location);
        switch (scheme)
        {
            case "modrinth":
                return await LoadProjectAsync(ContentPlatform.Modrinth, id, version, workDir, progress, ct).ConfigureAwait(false);
            case "curseforge":
                return await LoadProjectAsync(ContentPlatform.CurseForge, id, version, workDir, progress, ct).ConfigureAwait(false);
            case "ftb":
                return await LoadFtbAsync(long.Parse(id, CultureInfo.InvariantCulture), version, ct).ConfigureAwait(false);
            case "technic":
                return await LoadTechnicAsync(id, workDir, progress, ct).ConfigureAwait(false);
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidDataException($"'{location}' isn't a modpack file, link or id Fluorite understands.");

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (uri.AbsolutePath.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
            return await ReadPackwizAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
        if (host.EndsWith("modrinth.com", StringComparison.Ordinal) && segments is ["modpack", var mrSlug, ..])
            return await LoadProjectAsync(ContentPlatform.Modrinth, mrSlug, segments.Length >= 4 && segments[2] == "version" ? segments[3] : null, workDir, progress, ct).ConfigureAwait(false);
        if (host.EndsWith("curseforge.com", StringComparison.Ordinal) && segments is ["minecraft", "modpacks", var cfSlug, ..])
        {
            var project = await (CurseForge ?? throw new InvalidOperationException("CurseForge isn't available.")).FindBySlugAsync(cfSlug, ContentKind.Modpack, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"CurseForge has no modpack '{cfSlug}'.");
            var fileId = segments.Length >= 5 && segments[3] is "files" or "download" ? segments[4] : null;
            return await LoadProjectAsync(ContentPlatform.CurseForge, project.Id, fileId, workDir, progress, ct).ConfigureAwait(false);
        }
        if (host.EndsWith("feed-the-beast.com", StringComparison.Ordinal) && segments is ["modpacks", var ftbSlug, ..]
            && long.TryParse(ftbSlug.Split('-')[0], CultureInfo.InvariantCulture, out var ftbId))
            return await LoadFtbAsync(ftbId, null, ct).ConfigureAwait(false);
        if (host.EndsWith("technicpack.net", StringComparison.Ordinal))
            return await LoadTechnicAsync(location, workDir, progress, ct).ConfigureAwait(false);

        // A direct link to an archive.
        Directory.CreateDirectory(workDir);
        var name = PackPaths.Sanitize(Uri.UnescapeDataString(segments.LastOrDefault() ?? "pack.zip"))?.Replace('/', '_') ?? "pack.zip";
        var path = Path.Combine(workDir, name);
        await downloader.DownloadAllAsync([new DownloadItem([uri], path) { DisplayName = name }], progress, ct).ConfigureAwait(false);
        return await LoadFileAsync(path, ct).ConfigureAwait(false);
    }

    /// <summary><c>modrinth:fabulously-optimized@1.2.3</c> → (modrinth, fabulously-optimized, 1.2.3).</summary>
    internal static (string? Scheme, string Id, string? Version) SplitShortForm(string location)
    {
        var colon = location.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return (null, location, null);
        var scheme = location[..colon].ToLowerInvariant();
        if (scheme is not ("modrinth" or "curseforge" or "ftb" or "technic"))
            return (null, location, null);
        var rest = location[(colon + 1)..];
        var at = rest.LastIndexOf('@');
        return at > 0 ? (scheme, rest[..at], rest[(at + 1)..]) : (scheme, rest, null);
    }

    private async Task<PackManifest> LoadProjectAsync(ContentPlatform platform, string projectId, string? versionId, string workDir, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var client = content.Get(platform);
        if (platform == ContentPlatform.Modrinth && !IsModrinthId(projectId))
            projectId = (await client.GetProjectAsync(projectId, ct).ConfigureAwait(false)).Id; // slug → id, so updates match.
        RemoteVersion version;
        if (versionId is null)
        {
            version = await LatestVersionAsync(platform, projectId, ct: ct).ConfigureAwait(false);
        }
        else
        {
            try
            {
                version = await client.GetVersionAsync(projectId, versionId, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                // Modrinth version URLs may use the version number rather than the id.
                var all = await client.GetVersionsAsync(projectId, new ContentTarget(ContentKind.Modpack, null, []), ct).ConfigureAwait(false);
                version = all.FirstOrDefault(v => v.VersionNumber == versionId || v.Id == versionId)
                    ?? throw new InvalidDataException($"The project has no version '{versionId}'.", ex);
            }
        }
        var manifest = await LoadVersionAsync(version, workDir, progress, ct).ConfigureAwait(false);
        if (manifest.IconUrl is null && await TryGetProjectAsync(client, projectId, ct).ConfigureAwait(false) is { } project)
        {
            manifest.IconUrl = project.IconUrl;
            manifest.Summary ??= project.Summary;
        }
        return manifest;
    }

    private async Task<PackManifest> ReadPackwizAsync(string url, CancellationToken ct)
    {
        var manifest = await PackwizReader.ReadAsync(url, http, CurseForge, ct).ConfigureAwait(false);
        await CurseForgePacks.FillBlockedFromModrinthAsync(manifest, Modrinth, ct).ConfigureAwait(false);
        return manifest;
    }

    private static bool IsModrinthId(string id) => id.Length == 8 && id.All(char.IsAsciiLetterOrDigit);

    private static async Task<RemoteProject?> TryGetProjectAsync(IContentPlatform client, string id, CancellationToken ct)
    {
        try
        {
            return await client.GetProjectAsync(id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            return null;
        }
    }

    private async Task<PackManifest> LoadFtbAsync(long id, string? versionId, CancellationToken ct)
    {
        var pack = await Ftb.GetPackAsync(id, ct).ConfigureAwait(false);
        var version = versionId is null
            ? pack.Versions.FirstOrDefault(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)) ?? (pack.Versions.Count > 0 ? pack.Versions[0] : null)
            : pack.Versions.FirstOrDefault(v => v.Id.ToString(CultureInfo.InvariantCulture) == versionId || v.Name == versionId);
        var manifest = await Ftb.GetManifestAsync(pack, version ?? throw new InvalidDataException($"FTB pack {id} has no such version."), CurseForge, ct).ConfigureAwait(false);
        await CurseForgePacks.FillBlockedFromModrinthAsync(manifest, Modrinth, ct).ConfigureAwait(false);
        return manifest;
    }

    private async Task<PackManifest> LoadTechnicAsync(string slugOrUrl, string workDir, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var pack = await Technic.GetPackAsync(slugOrUrl, ct).ConfigureAwait(false);
        return await Technic.DownloadAsync(pack, workDir, downloader, mirrors, progress, ct).ConfigureAwait(false);
    }

    // ── Updates ───────────────────────────────────────────────────────────

    /// <summary>A newer version of the pack an instance was installed from, or null.</summary>
    public async Task<PackUpdateInfo?> CheckUpdateAsync(PackState state, CancellationToken ct = default)
    {
        switch (state.Format)
        {
            case PackFormat.Modrinth or PackFormat.CurseForge when state.ProjectId is { } projectId:
            {
                var platform = state.Format == PackFormat.Modrinth ? ContentPlatform.Modrinth : ContentPlatform.CurseForge;
                if (!content.Has(platform))
                    return null;
                var versions = await content.Get(platform).GetVersionsAsync(projectId, new ContentTarget(ContentKind.Modpack, null, []), ct).ConfigureAwait(false);
                var current = versions.FirstOrDefault(v => v.Id == state.VersionId);
                var channel = current?.Channel ?? ReleaseChannel.Release;
                var newest = versions.Where(v => v.Channel <= channel).OrderByDescending(v => v.Published).FirstOrDefault();
                return newest is not null && newest.Id != state.VersionId && (current is null || newest.Published > current.Published)
                    ? new PackUpdateInfo(newest.Id, newest.VersionNumber, newest.Published, newest)
                    : null;
            }
            case PackFormat.Packwiz when state.Url is { } url:
            {
                var latest = await PackwizReader.ReadAsync(url, http, CurseForge, ct).ConfigureAwait(false);
                return latest.Version != state.Version ? new PackUpdateInfo(latest.Version ?? "", latest.Version ?? "", null) : null;
            }
            case PackFormat.Ftb when long.TryParse(state.ProjectId, CultureInfo.InvariantCulture, out var ftbId):
            {
                var pack = await Ftb.GetPackAsync(ftbId, ct).ConfigureAwait(false);
                var newest = pack.Versions.FirstOrDefault(v => v.Type.Equals("release", StringComparison.OrdinalIgnoreCase)) ?? (pack.Versions.Count > 0 ? pack.Versions[0] : null);
                return newest is not null && newest.Id.ToString(CultureInfo.InvariantCulture) != state.VersionId
                    ? new PackUpdateInfo(newest.Id.ToString(CultureInfo.InvariantCulture), newest.Name, newest.Updated)
                    : null;
            }
            case PackFormat.Technic when state.ProjectId is { } slug:
            {
                var pack = await Technic.GetPackAsync(slug, ct).ConfigureAwait(false);
                return pack.Version is { } build && build != state.VersionId ? new PackUpdateInfo(build, build, null) : null;
            }
            default:
                return null;
        }
    }

    /// <summary>Loads the manifest of an update found by <see cref="CheckUpdateAsync"/>.</summary>
    public async Task<PackManifest> LoadUpdateAsync(PackState state, PackUpdateInfo update, string workDir, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default) =>
        state.Format switch
        {
            PackFormat.Modrinth or PackFormat.CurseForge => await LoadVersionAsync(update.Version
                ?? await content.Get(state.Format == PackFormat.Modrinth ? ContentPlatform.Modrinth : ContentPlatform.CurseForge).GetVersionAsync(state.ProjectId!, update.VersionId, ct).ConfigureAwait(false),
                workDir, progress, ct).ConfigureAwait(false),
            PackFormat.Packwiz => await ReadPackwizAsync(state.Url!, ct).ConfigureAwait(false),
            PackFormat.Ftb => await LoadFtbAsync(long.Parse(state.ProjectId!, CultureInfo.InvariantCulture), update.VersionId, ct).ConfigureAwait(false),
            PackFormat.Technic => await LoadTechnicAsync(state.ProjectId!, workDir, progress, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"{state.Format} packs can't be updated."),
        };
}
