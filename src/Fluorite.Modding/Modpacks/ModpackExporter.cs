using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Modpacks;

public sealed class PackExportOptions
{
    /// <summary><see cref="PackFormat.Modrinth"/>, <see cref="PackFormat.CurseForge"/>, <see cref="PackFormat.MultiMC"/> or <see cref="PackFormat.Packwiz"/>.</summary>
    public required PackFormat Format { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string? Author { get; init; }
    public string? Summary { get; init; }

    /// <summary>Top-level files/folders of the game directory to include (see <see cref="ModpackExporter.DefaultSelection"/>).</summary>
    public required IReadOnlyCollection<string> Include { get; init; }
}

public sealed record PackExportResult(string Path, int Referenced, int Bundled, IReadOnlyList<string> Warnings);

/// <summary>A top-level entry of the game folder offered for export.</summary>
public sealed record ExportCandidate(string Name, bool IsDirectory, bool IncludedByDefault);

/// <summary>
/// Exports an instance as a Modrinth <c>.mrpack</c>, CurseForge zip, Prism/MultiMC zip or packwiz folder. Content files
/// that Modrinth/CurseForge know by hash are referenced (downloaded by whoever installs the pack); everything else is
/// bundled as an override.
/// </summary>
public sealed class ModpackExporter(ContentService content)
{
    private static readonly HashSet<string> s_neverExport = new(StringComparer.OrdinalIgnoreCase)
    {
        "logs", "crash-reports", "screenshots", "natives", ".fluorite", "debug", "downloads", "quickPlay", ".mixin.out",
        "usercache.json", "usernamecache.json", "realms_persistence.json", "command_history.txt", "launcher_log.txt",
        "hs_err_pid", ".cache", "local", "backups", "patchouli_data.json",
        // Loader caches, rebuilt on launch.
        ".fabric", ".quilt", ".connector", "modernfix", "debug-profile.json",
    };

    private static readonly HashSet<string> s_offByDefault = new(StringComparer.OrdinalIgnoreCase)
    {
        "saves", "servers.dat", "servers.dat_old", "options.txt", "optionsof.txt", "optionsshaders.txt", "schematics", "replay_recordings", "XaeroWorldMap", "XaeroWaypoints", "journeymap",
    };

    /// <summary>What can be exported from <paramref name="gameDirectory"/>, with sensible defaults (no worlds or personal settings).</summary>
    public static IReadOnlyList<ExportCandidate> DefaultSelection(string gameDirectory)
    {
        if (!Directory.Exists(gameDirectory))
            return [];
        return Directory.EnumerateFileSystemEntries(gameDirectory)
            .Select(p => (Name: Path.GetFileName(p), IsDir: Directory.Exists(p)))
            .Where(e => !s_neverExport.Contains(e.Name) && !e.Name.StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase) && !e.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.IsDir).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new ExportCandidate(e.Name, e.IsDir, !s_offByDefault.Contains(e.Name) && !e.Name.StartsWith('.')))
            .ToList();
    }

    public async Task<PackExportResult> ExportAsync(GameInstance instance, PackExportOptions options, string outputPath, CancellationToken ct = default)
    {
        var files = Collect(instance.GameDirectory, options.Include, keepDisabled: options.Format == PackFormat.MultiMC);
        return options.Format switch
        {
            PackFormat.Modrinth => await ExportMrpackAsync(instance, options, files, outputPath, ct).ConfigureAwait(false),
            PackFormat.CurseForge => await ExportCurseForgeAsync(instance, options, files, outputPath, ct).ConfigureAwait(false),
            PackFormat.MultiMC => ExportPrism(instance, options, files, outputPath),
            PackFormat.Packwiz => await ExportPackwizAsync(instance, options, files, outputPath, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Can't export as {options.Format}."),
        };
    }

    /// <summary>Relative path (<c>/</c>) → full path of every file under the selected entries.</summary>
    private static List<(string Relative, string Full)> Collect(string gameDir, IReadOnlyCollection<string> include, bool keepDisabled)
    {
        var result = new List<(string, string)>();
        foreach (var name in include)
        {
            var full = Path.Combine(gameDir, name);
            if (File.Exists(full))
                result.Add((name, full));
            else if (Directory.Exists(full))
            {
                foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                    result.Add((Path.GetRelativePath(gameDir, f).Replace('\\', '/'), f));
            }
        }
        return result.Where(f => (keepDisabled || !f.Item1.EndsWith(ContentLocation.DisabledSuffix, StringComparison.OrdinalIgnoreCase))
                && !f.Item1.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool IsContent(string relative) => PackPaths.LocationOf(relative) is not null;

    /// <summary>Hashes the content files and asks a platform which of them it hosts.</summary>
    private async Task<Dictionary<string, (ContentHashes Hashes, IdentifiedFile Match)>> IdentifyAsync(
        ContentPlatform platform, List<(string Relative, string Full)> files, CancellationToken ct)
    {
        var result = new Dictionary<string, (ContentHashes, IdentifiedFile)>(StringComparer.OrdinalIgnoreCase);
        if (!content.Has(platform))
            return result;
        var hashed = new List<(string Relative, ContentHashes Hashes)>();
        foreach (var f in files.Where(f => IsContent(f.Relative)))
            hashed.Add((f.Relative, await ContentHashes.ComputeAsync(f.Full, ct).ConfigureAwait(false)));
        if (hashed.Count == 0)
            return result;
        var matches = await content.Get(platform).IdentifyAsync(hashed.Select(h => h.Hashes).ToList(), ct).ConfigureAwait(false);
        var bySha1 = matches.GroupBy(m => m.Sha1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var (rel, hashes) in hashed)
        {
            if (bySha1.TryGetValue(hashes.Sha1, out var m))
                result[rel] = (hashes, m);
        }
        return result;
    }

    // ── Modrinth ──────────────────────────────────────────────────────────

    private async Task<PackExportResult> ExportMrpackAsync(GameInstance instance, PackExportOptions o, List<(string Relative, string Full)> files, string output, CancellationToken ct)
    {
        var known = await IdentifyAsync(ContentPlatform.Modrinth, files, ct).ConfigureAwait(false);
        var m = instance.Manifest;
        var deps = new JsonObject { ["minecraft"] = m.MinecraftVersion };
        var warnings = new List<string>();
        if (ComponentResolver.PrimaryLoader(m) is { } loader)
        {
            var key = loader.Uid switch
            {
                ComponentUids.FabricLoader => "fabric-loader",
                ComponentUids.QuiltLoader => "quilt-loader",
                ComponentUids.Forge => "forge",
                ComponentUids.NeoForge => "neoforge",
                _ => null,
            };
            if (key is null)
                warnings.Add($"Modrinth packs can't express {loader.Uid}; installers will get vanilla {m.MinecraftVersion}.");
            else
                deps[key] = loader.Version;
        }

        var entries = new JsonArray();
        var bundled = 0;
        using (var zip = Create(output))
        {
            foreach (var (rel, full) in files)
            {
                if (known.TryGetValue(rel, out var hit) && hit.Match.Version.Files.FirstOrDefault(f => HashUtil(f.Sha1, hit.Hashes.Sha1)) is { Url: { } url } file)
                {
                    entries.Add((JsonNode)new JsonObject
                    {
                        ["path"] = rel,
                        ["hashes"] = new JsonObject { ["sha1"] = hit.Hashes.Sha1, ["sha512"] = hit.Hashes.Sha512 },
                        ["env"] = new JsonObject { ["client"] = "required", ["server"] = rel.StartsWith("mods/", StringComparison.Ordinal) ? "required" : "unsupported" },
                        ["downloads"] = new JsonArray((JsonNode)url),
                        ["fileSize"] = new FileInfo(full).Length,
                    });
                    continue;
                }
                zip.CreateEntryFromFile(full, "overrides/" + rel, CompressionLevel.Optimal);
                bundled++;
            }
            var index = new JsonObject
            {
                ["formatVersion"] = 1,
                ["game"] = "minecraft",
                ["versionId"] = o.Version,
                ["name"] = o.Name,
                ["summary"] = o.Summary,
                ["files"] = entries,
                ["dependencies"] = deps,
            };
            WriteJson(zip, "modrinth.index.json", index);
        }
        return new PackExportResult(output, entries.Count, bundled, warnings);
    }

    // ── CurseForge ────────────────────────────────────────────────────────

    private async Task<PackExportResult> ExportCurseForgeAsync(GameInstance instance, PackExportOptions o, List<(string Relative, string Full)> files, string output, CancellationToken ct)
    {
        var known = await IdentifyAsync(ContentPlatform.CurseForge, files, ct).ConfigureAwait(false);
        var m = instance.Manifest;
        var warnings = new List<string>();
        var loaders = new JsonArray();
        if (ComponentResolver.PrimaryLoader(m) is { } loader)
        {
            var prefix = loader.Uid switch
            {
                ComponentUids.Forge => "forge",
                ComponentUids.NeoForge => "neoforge",
                ComponentUids.FabricLoader => "fabric",
                ComponentUids.QuiltLoader => "quilt",
                _ => null,
            };
            if (prefix is null)
                warnings.Add($"CurseForge packs can't express {loader.Uid}.");
            else
                loaders.Add((JsonNode)new JsonObject { ["id"] = $"{prefix}-{loader.Version}", ["primary"] = true });
        }

        var list = new JsonArray();
        var html = new StringBuilder("<ul>\n");
        var bundled = 0;
        using (var zip = Create(output))
        {
            foreach (var (rel, full) in files)
            {
                if (known.TryGetValue(rel, out var hit))
                {
                    var v = hit.Match.Version;
                    list.Add((JsonNode)new JsonObject
                    {
                        ["projectID"] = long.Parse(v.ProjectId, CultureInfo.InvariantCulture),
                        ["fileID"] = long.Parse(v.Id, CultureInfo.InvariantCulture),
                        ["required"] = true,
                    });
                    html.Append("<li>").Append(WebUtility.HtmlEncode(v.Name)).Append("</li>\n");
                    continue;
                }
                zip.CreateEntryFromFile(full, "overrides/" + rel, CompressionLevel.Optimal);
                bundled++;
            }
            var manifest = new JsonObject
            {
                ["minecraft"] = new JsonObject { ["version"] = m.MinecraftVersion, ["modLoaders"] = loaders },
                ["manifestType"] = "minecraftModpack",
                ["manifestVersion"] = 1,
                ["name"] = o.Name,
                ["version"] = o.Version,
                ["author"] = o.Author ?? "",
                ["files"] = list,
                ["overrides"] = "overrides",
            };
            if (m.Settings.MaxMemoryMb is { } ram)
                ((JsonObject)manifest["minecraft"]!)["recommendedRam"] = ram;
            WriteJson(zip, "manifest.json", manifest);
            var entry = zip.CreateEntry("modlist.html");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write(html.Append("</ul>\n"));
        }
        return new PackExportResult(output, list.Count, bundled, warnings);
    }

    // ── Prism / MultiMC ───────────────────────────────────────────────────

    private static PackExportResult ExportPrism(GameInstance instance, PackExportOptions o, List<(string Relative, string Full)> files, string output)
    {
        var m = instance.Manifest;
        var components = new JsonArray();
        var mc = m.MinecraftVersion;
        components.Add((JsonNode)new JsonObject { ["uid"] = ComponentUids.Minecraft, ["version"] = mc, ["important"] = true });
        if (ComponentResolver.PrimaryLoader(m) is { } loader)
        {
            if (loader.Uid is ComponentUids.FabricLoader or ComponentUids.LegacyFabric)
                components.Add((JsonNode)new JsonObject { ["uid"] = loader.Uid == ComponentUids.LegacyFabric ? "net.legacyfabric.intermediary" : "net.fabricmc.intermediary", ["version"] = mc, ["dependencyOnly"] = true });
            if (loader.Uid == ComponentUids.QuiltLoader)
                components.Add((JsonNode)new JsonObject { ["uid"] = "net.fabricmc.intermediary", ["version"] = mc, ["dependencyOnly"] = true });
            components.Add((JsonNode)new JsonObject { ["uid"] = loader.Uid == ComponentUids.LegacyFabric ? ComponentUids.FabricLoader : loader.Uid, ["version"] = loader.Version });
        }

        var cfg = new StringBuilder();
        cfg.Append("[General]\n");
        cfg.Append("ConfigVersion=1.2\n");
        cfg.Append("InstanceType=OneSix\n");
        // The icon file goes next to instance.cfg named after its key, which Prism installs into its icons folder on
        // import. A content-derived key never overwrites another pack's icon there. Built-in keys pass through.
        var iconFile = m.Icon is { } iconPath && File.Exists(iconPath) && PrismIcons.IsIconFile(iconPath) ? iconPath : null;
        var iconKey = iconFile is not null
            ? "fluorite_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(iconFile)))[..12]
            : m.IconKey ?? "default";
        cfg.Append("iconKey=").Append(iconKey).Append('\n');
        cfg.Append("name=").Append(IniFile.Encode(o.Name)).Append('\n');
        cfg.Append("notes=").Append(IniFile.Encode(o.Summary ?? m.Notes ?? "")).Append('\n');
        var s = m.Settings;
        if (s.MaxMemoryMb is not null || s.MinMemoryMb is not null)
        {
            cfg.Append("OverrideMemory=true\n");
            if (s.MaxMemoryMb is { } max)
                cfg.Append("MaxMemAlloc=").Append(max.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (s.MinMemoryMb is { } min)
                cfg.Append("MinMemAlloc=").Append(min.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        if (s.JvmArguments is { } jvm)
            cfg.Append("OverrideJavaArgs=true\nJvmArgs=").Append(IniFile.Encode(jvm)).Append('\n');

        using (var zip = Create(output))
        {
            WriteText(zip, "instance.cfg", cfg.ToString());
            WriteJson(zip, "mmc-pack.json", new JsonObject { ["components"] = components, ["formatVersion"] = 1 });
            if (iconFile is not null)
                zip.CreateEntryFromFile(iconFile, iconKey + Path.GetExtension(iconFile).ToLowerInvariant(), CompressionLevel.Optimal);
            foreach (var (rel, full) in files)
                zip.CreateEntryFromFile(full, "minecraft/" + rel, CompressionLevel.Optimal);
        }
        return new PackExportResult(output, 0, files.Count, []);
    }

    // ── packwiz ───────────────────────────────────────────────────────────

    private async Task<PackExportResult> ExportPackwizAsync(GameInstance instance, PackExportOptions o, List<(string Relative, string Full)> files, string outputDir, CancellationToken ct)
    {
        var modrinth = await IdentifyAsync(ContentPlatform.Modrinth, files, ct).ConfigureAwait(false);
        var remaining = files.Where(f => !modrinth.ContainsKey(f.Relative)).ToList();
        var curseforge = await IdentifyAsync(ContentPlatform.CurseForge, remaining, ct).ConfigureAwait(false);
        Directory.CreateDirectory(outputDir);
        var index = new StringBuilder("hash-format = \"sha256\"\n");
        var referenced = 0;
        var bundled = 0;

        foreach (var (rel, full) in files)
        {
            string written;
            var metaFile = false;
            if (modrinth.TryGetValue(rel, out var mr) && mr.Match.Version.Files.FirstOrDefault(f => HashUtil(f.Sha1, mr.Hashes.Sha1)) is { Url: { } url })
            {
                written = WriteMetafile(outputDir, rel, Path.GetFileName(rel), url, "sha512", mr.Hashes.Sha512,
                    $"[update.modrinth]\nmod-id = {Toml(mr.Match.Version.ProjectId)}\nversion = {Toml(mr.Match.Version.Id)}\n", mr.Match.Version.Name);
                metaFile = true;
                referenced++;
            }
            else if (curseforge.TryGetValue(rel, out var cf))
            {
                written = WriteMetafile(outputDir, rel, Path.GetFileName(rel), null, "sha1", cf.Hashes.Sha1,
                    $"[update.curseforge]\nfile-id = {cf.Match.Version.Id}\nproject-id = {cf.Match.Version.ProjectId}\n", cf.Match.Version.Name);
                metaFile = true;
                referenced++;
            }
            else
            {
                written = rel;
                var target = PackPaths.Resolve(outputDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(full, target, overwrite: true);
                bundled++;
            }
            var hash = await ModpackInstaller.HashAsync(PackPaths.Resolve(outputDir, written), "sha256", ct).ConfigureAwait(false);
            index.Append("\n[[files]]\nfile = ").Append(Toml(written)).Append("\nhash = ").Append(Toml(hash)).Append('\n');
            if (metaFile)
                index.Append("metafile = true\n");
        }

        var indexPath = Path.Combine(outputDir, "index.toml");
        await File.WriteAllTextAsync(indexPath, index.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        var m = instance.Manifest;
        var versions = new StringBuilder("[versions]\nminecraft = ").Append(Toml(m.MinecraftVersion ?? "")).Append('\n');
        if (ComponentResolver.PrimaryLoader(m) is { } loader && loader.Uid switch
        {
            ComponentUids.FabricLoader => "fabric",
            ComponentUids.QuiltLoader => "quilt",
            ComponentUids.Forge => "forge",
            ComponentUids.NeoForge => "neoforge",
            ComponentUids.LiteLoader => "liteloader",
            _ => null,
        } is { } key)
            versions.Append(key).Append(" = ").Append(Toml(loader.Version)).Append('\n');
        var pack = new StringBuilder()
            .Append("name = ").Append(Toml(o.Name)).Append('\n')
            .Append("author = ").Append(Toml(o.Author ?? "")).Append('\n')
            .Append("version = ").Append(Toml(o.Version)).Append('\n')
            .Append("pack-format = \"packwiz:1.1.0\"\n\n")
            .Append("[index]\nfile = \"index.toml\"\nhash-format = \"sha256\"\nhash = ")
            .Append(Toml(await ModpackInstaller.HashAsync(indexPath, "sha256", ct).ConfigureAwait(false))).Append("\n\n")
            .Append(versions);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "pack.toml"), pack.ToString(), new UTF8Encoding(false), ct).ConfigureAwait(false);
        return new PackExportResult(outputDir, referenced, bundled, []);
    }

    /// <summary>Writes <c>&lt;folder&gt;/&lt;name&gt;.pw.toml</c> next to where the file would go; returns its relative path.</summary>
    private static string WriteMetafile(string root, string relative, string fileName, string? url, string hashFormat, string hash, string update, string title)
    {
        var folder = relative.Contains('/', StringComparison.Ordinal) ? relative[..relative.LastIndexOf('/')] : "";
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var metaRel = (folder.Length > 0 ? folder + "/" : "") + stem + ".pw.toml";
        var sb = new StringBuilder()
            .Append("name = ").Append(Toml(title)).Append('\n')
            .Append("filename = ").Append(Toml(fileName)).Append('\n')
            .Append("side = \"both\"\n\n[download]\n");
        if (url is not null)
            sb.Append("url = ").Append(Toml(url)).Append('\n');
        else
            sb.Append("mode = \"metadata:curseforge\"\n");
        sb.Append("hash-format = ").Append(Toml(hashFormat)).Append("\nhash = ").Append(Toml(hash)).Append("\n\n").Append(update);
        var target = PackPaths.Resolve(root, metaRel);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, sb.ToString(), new UTF8Encoding(false));
        return metaRel;
    }

    private static string Toml(string s) => "\"" + s.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static bool HashUtil(string? a, string b) => Core.Net.HashUtil.HashEquals(a, b);

    private static ZipArchive Create(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (File.Exists(path))
            File.Delete(path);
        return ZipFile.Open(path, ZipArchiveMode.Create);
    }

    private static void WriteJson(ZipArchive zip, string name, JsonObject json) =>
        WriteText(zip, name, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

    private static void WriteText(ZipArchive zip, string name, string text)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    /// <summary>SHA-256 of a string, for tests.</summary>
    internal static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
