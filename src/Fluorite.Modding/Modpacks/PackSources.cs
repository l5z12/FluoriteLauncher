using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Platforms.CurseForge;
using Fluorite.Modding.Util;
using Tomlyn;
using Tomlyn.Model;

namespace Fluorite.Modding.Modpacks;

/// <summary>Fills in a CurseForge pack's files (its manifest only has project/file ids).</summary>
public static class CurseForgePacks
{
    public static async Task ResolveAsync(PackManifest manifest, CurseForgeClient client, CancellationToken ct = default)
    {
        if (manifest.UnresolvedCurseForge.Count == 0)
            return;
        var infos = await client.GetFilesAsync(manifest.UnresolvedCurseForge.Select(f => f.FileId).ToList(), ct).ConfigureAwait(false);
        var byId = infos.ToDictionary(i => i.FileId);
        foreach (var (project, fileId, required) in manifest.UnresolvedCurseForge)
        {
            if (!byId.TryGetValue(fileId, out var info))
            {
                manifest.Warnings.Add($"CurseForge file {fileId} (project {project}) no longer exists and was skipped.");
                continue;
            }
            var path = PackPaths.Sanitize($"{info.Folder}/{info.FileName}");
            if (path is null)
                continue;
            manifest.Files.Add(new PackFile(path)
            {
                Urls = info.Url is null ? [] : [info.Url],
                Sha1 = info.Sha1,
                Size = info.Size > 0 ? info.Size : null,
                Client = required ? PackSide.Required : PackSide.Optional,
                Source = ModpackReader.CurseForgeRef(project, fileId, info.DisplayName),
                ManualDownloadUrl = info.Url is null ? info.ManualDownloadUrl : null,
                ExtractToSaves = info.ClassId == 17,
                Title = info.ProjectName,
            });
        }
        manifest.UnresolvedCurseForge.Clear();
    }

    /// <summary>
    /// Files CurseForge won't hand to launchers are often published on Modrinth too. When Modrinth has a file with
    /// the exact same SHA-1 (byte-for-byte the same file, uploaded by its author), download it from there instead of
    /// sending the user to a browser. Returns how many were found.
    /// </summary>
    public static async Task<int> FillBlockedFromModrinthAsync(PackManifest manifest, IContentPlatform? modrinth, CancellationToken ct = default)
    {
        var blocked = manifest.Files.Where(f => f.IsBlocked && f.Sha1 is not null).ToList();
        if (blocked.Count == 0 || modrinth is null)
            return 0;
        IReadOnlyList<IdentifiedFile> matches;
        try
        {
            matches = await modrinth.IdentifyAsync(blocked.Select(f => new ContentHashes(f.Sha1!.ToLowerInvariant(), "", 0)).ToList(), ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return 0; // Manual download still works.
        }
        var found = 0;
        foreach (var f in blocked)
        {
            var hit = matches.FirstOrDefault(m => HashUtil.HashEquals(m.Sha1, f.Sha1))?.Version.Files.FirstOrDefault(x => HashUtil.HashEquals(x.Sha1, f.Sha1));
            if (hit?.Url is not { } url)
                continue;
            manifest.Files[manifest.Files.IndexOf(f)] = f with { Urls = [url], ManualDownloadUrl = null, Sha512 = hit.Sha512 ?? f.Sha512 };
            found++;
        }
        return found;
    }
}

/// <summary>
/// packwiz packs (<c>pack.toml</c> → <c>index.toml</c> → <c>*.pw.toml</c>), from an http(s) URL or a local folder.
/// Metafiles with <c>mode = "metadata:curseforge"</c> are resolved through the CurseForge API.
/// </summary>
public static class PackwizReader
{
    public static async Task<PackManifest> ReadAsync(string packTomlUrl, HttpClient http, CurseForgeClient? curseForge, CancellationToken ct = default)
    {
        var pack = await LoadTomlAsync(packTomlUrl, http, ct).ConfigureAwait(false);
        var versions = Table(pack, "versions") ?? throw new InvalidDataException("pack.toml has no [versions].");
        var (uid, loaderVersion) = Loader(versions);
        var manifest = new PackManifest
        {
            Format = PackFormat.Packwiz,
            Name = Str(pack, "name") ?? "packwiz pack",
            Version = Str(pack, "version"),
            Author = Str(pack, "author"),
            Summary = Str(pack, "description"),
            MinecraftVersion = Str(versions, "minecraft") ?? throw new InvalidDataException("pack.toml doesn't say which Minecraft version it needs."),
            LoaderUid = uid,
            LoaderVersion = loaderVersion,
            Source = new PackSource(PackFormat.Packwiz, null, Str(pack, "version"), Str(pack, "version")) { Url = packTomlUrl },
        };

        var indexInfo = Table(pack, "index") ?? throw new InvalidDataException("pack.toml has no [index].");
        var indexUrl = Relative(packTomlUrl, Str(indexInfo, "file") ?? "index.toml");
        var index = await LoadTomlAsync(indexUrl, http, ct).ConfigureAwait(false);
        var defaultFormat = Str(index, "hash-format") ?? "sha256";
        var baseUrl = indexUrl[..(indexUrl.LastIndexOf('/') + 1)];
        var cfIds = new List<(long Project, long File, string Path, PackSide Side)>();

        var entries = index.TryGetValue("files", out var f) && f is TomlTableArray arr ? arr : [];
        using var throttle = new SemaphoreSlim(16);
        var metas = await Task.WhenAll(entries.Select(async entry =>
        {
            var file = Str(entry, "file");
            if (file is null || PackPaths.Sanitize(file) is not { } rel)
                return NoEntry;
            var isMeta = entry.TryGetValue("metafile", out var m) && m is true;
            if (!isMeta)
            {
                (PackFile?, (long, long, string, PackSide)?) plain = (new PackFile(rel)
                {
                    Urls = [baseUrl + EscapePath(rel)],
                    OtherHash = Str(entry, "hash") is { } h ? (Str(entry, "hash-format") ?? defaultFormat, h) : null,
                }, null);
                return plain;
            }
            await throttle.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var meta = await LoadTomlAsync(baseUrl + EscapePath(rel), http, ct).ConfigureAwait(false);
                return MapMetafile(rel, meta);
            }
            finally
            {
                throttle.Release();
            }
        })).ConfigureAwait(false);

        foreach (var (file, cf) in metas)
        {
            if (file is not null)
                manifest.Files.Add(file);
            if (cf is { } c)
                cfIds.Add(c);
        }

        if (cfIds.Count > 0)
        {
            if (curseForge is null)
                throw new InvalidOperationException("This pack has CurseForge files, which need the CurseForge API.");
            var infos = (await curseForge.GetFilesAsync(cfIds.Select(c => c.File).ToList(), ct).ConfigureAwait(false)).ToDictionary(i => i.FileId);
            foreach (var (project, fileId, path, side) in cfIds)
            {
                if (!infos.TryGetValue(fileId, out var info))
                {
                    manifest.Warnings.Add($"CurseForge file {fileId} for {path} no longer exists and was skipped.");
                    continue;
                }
                manifest.Files.Add(new PackFile(path)
                {
                    Urls = info.Url is null ? [] : [info.Url],
                    Sha1 = info.Sha1,
                    Size = info.Size,
                    Client = side,
                    Source = ModpackReader.CurseForgeRef(project, fileId, info.DisplayName),
                    ManualDownloadUrl = info.Url is null ? info.ManualDownloadUrl : null,
                    Title = info.ProjectName,
                });
            }
        }
        return manifest;
    }

    private static readonly (PackFile?, (long, long, string, PackSide)?) NoEntry = (null, null);

    private static (PackFile?, (long, long, string, PackSide)?) MapMetafile(string metaPath, TomlTable meta)
    {
        var folder = metaPath.Contains('/', StringComparison.Ordinal) ? metaPath[..(metaPath.LastIndexOf('/') + 1)] : "";
        var target = PackPaths.Sanitize(folder + (Str(meta, "filename") ?? ""));
        if (target is null)
            return (null, null);
        var side = Str(meta, "side") switch
        {
            "server" => PackSide.Unsupported,
            _ => Table(meta, "option") is { } opt && opt.TryGetValue("optional", out var o) && o is true ? PackSide.Optional : PackSide.Required,
        };
        var download = Table(meta, "download") ?? new TomlTable();
        var update = Table(meta, "update");
        if (Str(download, "mode") is "metadata:curseforge" && Table(update, "curseforge") is { } cf
            && Num(cf, "project-id") is { } project && Num(cf, "file-id") is { } file)
            return (null, (project, file, target, side));

        var format = Str(download, "hash-format") ?? "sha256";
        var hash = Str(download, "hash");
        Content.RemoteRef? source = null;
        if (Table(update, "modrinth") is { } mr && Str(mr, "mod-id") is { } modId && Str(mr, "version") is { } versionId)
            source = new Content.RemoteRef { Platform = ContentPlatform.Modrinth, ProjectId = modId, VersionId = versionId };
        else if (Table(update, "curseforge") is { } cf2 && Num(cf2, "project-id") is { } p2 && Num(cf2, "file-id") is { } f2)
            source = ModpackReader.CurseForgeRef(p2, f2);
        return (new PackFile(target)
        {
            Urls = Str(download, "url") is { } url ? [url] : [],
            Sha1 = format == "sha1" ? hash : null,
            Sha512 = format == "sha512" ? hash : null,
            OtherHash = format is not ("sha1" or "sha512") && hash is not null ? (format, hash) : null,
            Client = side,
            Source = source,
            Title = Str(meta, "name"),
        }, null);
    }

    private static (string? Uid, string? Version) Loader(TomlTable versions)
    {
        if (Str(versions, "neoforge") is { } neo)
            return (ComponentUids.NeoForge, neo);
        if (Str(versions, "forge") is { } forge)
            return (ComponentUids.Forge, VersionProfileInspector.StripMinecraftPrefix(forge));
        if (Str(versions, "fabric") is { } fabric)
            return (ComponentUids.FabricLoader, fabric);
        if (Str(versions, "quilt") is { } quilt)
            return (ComponentUids.QuiltLoader, quilt);
        if (Str(versions, "liteloader") is { } lite)
            return (ComponentUids.LiteLoader, lite);
        return (null, null);
    }

    private static async Task<TomlTable> LoadTomlAsync(string url, HttpClient http, CancellationToken ct)
    {
        var text = url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? await http.GetStringAsync(new Uri(url), ct).ConfigureAwait(false)
            : await File.ReadAllTextAsync(new Uri(url).IsFile ? new Uri(url).LocalPath : url, ct).ConfigureAwait(false);
        return TomlSerializer.Deserialize<TomlTable>(text, Metadata.ModsTomlContext.Default) ?? new TomlTable();
    }

    /// <summary>A local <c>pack.toml</c> path becomes a <c>file://</c> URL so relative entries resolve the same way.</summary>
    public static string NormalizeLocation(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
            ? location
            : new Uri(Path.GetFullPath(Directory.Exists(location) ? Path.Combine(location, "pack.toml") : location)).AbsoluteUri;

    private static string Relative(string baseUrl, string relative) => new Uri(new Uri(baseUrl), relative).AbsoluteUri;

    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static TomlTable? Table(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v) ? v as TomlTable : null;

    private static string? Str(TomlTable? t, string key) => t is not null && t.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    private static long? Num(TomlTable t, string key) => t.TryGetValue(key, out var v) ? v switch
    {
        long l => l,
        int i => i,
        string s when long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    } : null;
}

/// <summary>FTB modpacks (<c>api.feed-the-beast.com</c>): pack info and version manifests.</summary>
public sealed class FtbClient(HttpClient http)
{
    public const string ApiBase = "https://api.feed-the-beast.com/v1/modpacks/public";

    public sealed record FtbVersion(long Id, string Name, string Type, DateTimeOffset? Updated);

    public sealed record FtbPack(long Id, string Name, string? Synopsis, string? IconUrl, IReadOnlyList<FtbVersion> Versions);

    public async Task<FtbPack> GetPackAsync(long id, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{ApiBase}/modpack/{id}", ct).ConfigureAwait(false);
        var versions = (json["versions"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(v => new FtbVersion(ModpackReader.Long(v, "id") ?? 0, ModpackReader.Str(v, "name") ?? "", ModpackReader.Str(v, "type") ?? "release",
                ModpackReader.Long(v, "updated") is { } u ? DateTimeOffset.FromUnixTimeSeconds(u) : null))
            .OrderByDescending(v => v.Updated)
            .ToList();
        var icon = (json["art"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(a => ModpackReader.Str(a, "type") is "square" or "logo");
        return new FtbPack(id, ModpackReader.Str(json, "name") ?? $"FTB pack {id}", ModpackReader.Str(json, "synopsis"), ModpackReader.Str(icon, "url"), versions);
    }

    public async Task<PackManifest> GetManifestAsync(FtbPack pack, FtbVersion version, CurseForgeClient? curseForge, CancellationToken ct = default)
    {
        var json = await GetJsonAsync($"{ApiBase}/modpack/{pack.Id}/{version.Id}", ct).ConfigureAwait(false);
        string? mc = null, uid = null, loaderVersion = null;
        foreach (var t in (json["targets"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = ModpackReader.Str(t, "name");
            var v = ModpackReader.Str(t, "version");
            if (ModpackReader.Str(t, "type") == "game" && name == "minecraft")
                mc = v;
            else if (ModpackReader.Str(t, "type") == "modloader")
                (uid, loaderVersion) = name switch
                {
                    "forge" => (ComponentUids.Forge, v is null ? null : VersionProfileInspector.StripMinecraftPrefix(v)),
                    "neoforge" => (ComponentUids.NeoForge, v),
                    "fabric" => (ComponentUids.FabricLoader, v),
                    "quilt" => (ComponentUids.QuiltLoader, v),
                    _ => (uid, loaderVersion),
                };
        }
        var manifest = new PackManifest
        {
            Format = PackFormat.Ftb,
            Name = pack.Name,
            Version = version.Name,
            Summary = pack.Synopsis,
            MinecraftVersion = mc ?? throw new InvalidDataException("The FTB version doesn't say which Minecraft version it needs."),
            LoaderUid = uid,
            LoaderVersion = loaderVersion,
            IconUrl = pack.IconUrl,
            Source = new PackSource(PackFormat.Ftb, pack.Id.ToString(CultureInfo.InvariantCulture), version.Id.ToString(CultureInfo.InvariantCulture), version.Name),
        };
        if (json["specs"] is JsonObject specs && ModpackReader.Long(specs, "recommended") is { } ram and >= 512)
            manifest.Settings.MaxMemoryMb = (int)ram;

        foreach (var f in (json["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (f["serveronly"] is JsonValue so && so.TryGetValue<bool>(out var serverOnly) && serverOnly)
                continue;
            var path = PackPaths.Sanitize((ModpackReader.Str(f, "path") ?? "") + (ModpackReader.Str(f, "name") ?? ""));
            if (path is null)
                continue;
            var optional = f["optional"] is JsonValue o && o.TryGetValue<bool>(out var opt) && opt;
            var cf = f["curseforge"] as JsonObject;
            long? project = cf is null ? null : ModpackReader.Long(cf, "project");
            long? fileId = cf is null ? null : ModpackReader.Long(cf, "file");
            if (ModpackReader.Str(f, "url") is { } url)
            {
                manifest.Files.Add(new PackFile(path)
                {
                    Urls = [url],
                    Sha1 = ModpackReader.Str(f, "sha1"),
                    Size = ModpackReader.Long(f, "size"),
                    Client = optional ? PackSide.Optional : PackSide.Required,
                    Source = project is > 0 && fileId is > 0 ? ModpackReader.CurseForgeRef(project.Value, fileId.Value) : null,
                });
            }
            else if (project is > 0 && fileId is > 0)
            {
                manifest.UnresolvedCurseForge.Add((project.Value, fileId.Value, !optional));
            }
        }
        if (manifest.UnresolvedCurseForge.Count > 0 && curseForge is not null)
            await CurseForgePacks.ResolveAsync(manifest, curseForge, ct).ConfigureAwait(false);
        return manifest;
    }

    private async Task<JsonObject> GetJsonAsync(string url, CancellationToken ct)
    {
        var bytes = await http.GetByteArrayAsync(new Uri(url), ct).ConfigureAwait(false);
        var json = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException($"Unexpected response from {url}.");
        if (ModpackReader.Str(json, "status") == "error")
            throw new InvalidDataException(ModpackReader.Str(json, "message") ?? $"FTB API error for {url}.");
        return json;
    }
}

/// <summary>Technic Platform packs: a single zip, or a Solder build (a list of zips, each extracted into the game folder).</summary>
public sealed class TechnicClient(HttpClient http)
{
    public const string PlatformApi = "https://api.technicpack.net/modpack/";
    private const string BuildParameter = "fluorite";

    public sealed record TechnicPack(string Slug, string Name, string? MinecraftVersion, string? ZipUrl, string? SolderUrl, string? Version, string? IconUrl);

    /// <summary>Accepts a slug, a <c>technicpack.net/modpack/&lt;slug&gt;</c> page or an API URL.</summary>
    public static string SlugFrom(string input)
    {
        var s = input.Trim().TrimEnd('/');
        foreach (var marker in new[] { "/modpack/" })
        {
            var i = s.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                s = s[(i + marker.Length)..];
        }
        var q = s.IndexOfAny(['?', '#', '/']);
        s = q >= 0 ? s[..q] : s;
        // technicpack.net page slugs look like "tekkit-legends.736902"; the API wants the part before the dot.
        var dot = s.LastIndexOf('.');
        return dot > 0 && s[(dot + 1)..].All(char.IsAsciiDigit) ? s[..dot] : s;
    }

    public async Task<TechnicPack> GetPackAsync(string slugOrUrl, CancellationToken ct = default)
    {
        var slug = SlugFrom(slugOrUrl);
        var json = JsonNode.Parse(await http.GetByteArrayAsync(new Uri($"{PlatformApi}{Uri.EscapeDataString(slug)}?build={BuildParameter}"), ct).ConfigureAwait(false)) as JsonObject
            ?? throw new InvalidDataException("Unexpected response from the Technic API.");
        if (ModpackReader.Str(json, "error") is { } error)
            throw new InvalidDataException(error);
        return new TechnicPack(slug, ModpackReader.Str(json, "displayName") ?? ModpackReader.Str(json, "name") ?? slug, ModpackReader.Str(json, "minecraft"),
            ModpackReader.Str(json, "url"), ModpackReader.Str(json, "solder"), ModpackReader.Str(json, "version"), ModpackReader.Str(json["icon"] as JsonObject, "url"));
    }

    /// <summary>
    /// Downloads the pack's zip(s) into <paramref name="workDir"/> and returns a manifest whose overrides come from a
    /// single merged archive (later Solder zips override earlier ones).
    /// </summary>
    public async Task<PackManifest> DownloadAsync(TechnicPack pack, string workDir, DownloadEngine downloader, MirrorTable mirrors, IProgress<DownloadProgress>? progress, CancellationToken ct = default)
    {
        Directory.CreateDirectory(workDir);
        var merged = Path.Combine(workDir, "technic-merged.zip");
        var zips = new List<string>();
        string? mc = pack.MinecraftVersion;
        string? build = pack.Version;
        if (pack.SolderUrl is { } solder)
        {
            var api = solder.TrimEnd('/');
            if (build is null)
            {
                var info = JsonNode.Parse(await http.GetByteArrayAsync(new Uri($"{api}/modpack/{pack.Slug}"), ct).ConfigureAwait(false)) as JsonObject;
                build = ModpackReader.Str(info, "recommended") ?? ModpackReader.Str(info, "latest");
            }
            var buildJson = JsonNode.Parse(await http.GetByteArrayAsync(new Uri($"{api}/modpack/{pack.Slug}/{build}"), ct).ConfigureAwait(false)) as JsonObject
                ?? throw new InvalidDataException("Unexpected response from the pack's Solder server.");
            mc = ModpackReader.Str(buildJson, "minecraft") ?? mc;
            var items = new List<DownloadItem>();
            var i = 0;
            foreach (var mod in (buildJson["mods"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (ModpackReader.Str(mod, "url") is not { } url)
                    continue;
                var path = Path.Combine(workDir, $"{i++:D4}.zip");
                items.Add(new DownloadItem(mirrors.Candidates(url), path) { DisplayName = ModpackReader.Str(mod, "name") ?? url });
                zips.Add(path);
            }
            await downloader.DownloadAllAsync(items, progress, ct).ConfigureAwait(false);
        }
        else if (pack.ZipUrl is { } zipUrl)
        {
            var path = Path.Combine(workDir, "pack.zip");
            await downloader.DownloadAllAsync([new DownloadItem(mirrors.Candidates(zipUrl), path) { DisplayName = pack.Name }], progress, ct).ConfigureAwait(false);
            zips.Add(path);
        }
        else
        {
            throw new InvalidDataException("This Technic pack has no download.");
        }

        MergeZips(zips, merged);
        using var archive = ZipFile.OpenRead(merged);
        var manifest = ModpackReader.ReadTechnic(archive, mc, pack.Name);
        manifest.ArchivePath = merged;
        manifest.IconUrl = pack.IconUrl;
        manifest.Version = build;
        manifest.Source = new PackSource(PackFormat.Technic, pack.Slug, build, build) { Url = pack.Slug };
        return manifest;
    }

    private static void MergeZips(List<string> zips, string target)
    {
        if (File.Exists(target))
            File.Delete(target);
        // Later zips win: find each entry's winning zip first, then copy once.
        var winner = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < zips.Count; i++)
        {
            using var input = ZipFile.OpenRead(zips[i]);
            foreach (var e in input.Entries.Where(e => !e.FullName.EndsWith('/')))
                winner[e.FullName] = i;
        }
        using var output = ZipFile.Open(target, ZipArchiveMode.Create);
        for (var i = 0; i < zips.Count; i++)
        {
            using var input = ZipFile.OpenRead(zips[i]);
            foreach (var e in input.Entries)
            {
                if (e.FullName.EndsWith('/') || winner[e.FullName] != i)
                    continue;
                var copy = output.CreateEntry(e.FullName, CompressionLevel.Fastest);
                using var src = e.Open();
                using var dst = copy.Open();
                src.CopyTo(dst);
            }
        }
    }
}
