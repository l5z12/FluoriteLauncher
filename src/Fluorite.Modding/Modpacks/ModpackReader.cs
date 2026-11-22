using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Modpacks;

/// <summary>
/// Reads modpack archives: Modrinth <c>.mrpack</c>, CurseForge zips (<c>manifest.json</c>), Prism/MultiMC instance
/// zips and Technic pack zips. Every path is validated against escaping the instance.
/// </summary>
public static class ModpackReader
{
    private static readonly JsonDocumentOptions s_json = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static PackManifest Read(string archivePath)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var manifest = Read(zip);
        manifest.ArchivePath = archivePath;
        return manifest;
    }

    public static PackManifest Read(ZipArchive zip)
    {
        if (zip.GetEntry("modrinth.index.json") is { } mr)
            return ReadMrpack(zip, Parse(mr));
        if (zip.GetEntry("manifest.json") is { } cf && Parse(cf) is { } cfJson && Str(cfJson, "manifestType") is null or "minecraftModpack")
            return ReadCurseForge(zip, cfJson);
        if (FindInstanceCfg(zip) is { } cfg)
            return ReadMultiMC(zip, cfg);
        if (zip.GetEntry("bin/modpack.jar") is not null || zip.GetEntry("bin/version.json") is not null)
            return ReadTechnic(zip, null);
        throw new InvalidDataException("This isn't a modpack Fluorite recognises (Modrinth .mrpack, CurseForge zip, Prism/MultiMC zip or Technic zip).");
    }

    // ── Modrinth (.mrpack) ────────────────────────────────────────────────

    private static PackManifest ReadMrpack(ZipArchive zip, JsonObject index)
    {
        var deps = index["dependencies"] as JsonObject ?? throw new InvalidDataException("modrinth.index.json has no dependencies.");
        var (loaderUid, loaderVersion) = LoaderFromModrinth(deps);
        var manifest = new PackManifest
        {
            Format = PackFormat.Modrinth,
            Name = Str(index, "name") ?? "Modrinth pack",
            Version = Str(index, "versionId"),
            Summary = Str(index, "summary"),
            MinecraftVersion = Str(deps, "minecraft") ?? throw new InvalidDataException("The pack doesn't say which Minecraft version it needs."),
            LoaderUid = loaderUid,
            LoaderVersion = loaderVersion,
        };
        foreach (var f in index["files"] as JsonArray ?? [])
        {
            if (f is not JsonObject file)
                continue;
            var path = PackPaths.Sanitize(Str(file, "path"));
            if (path is null)
            {
                manifest.Warnings.Add($"Skipped a file with an unsafe path: {Str(file, "path")}");
                continue;
            }
            var hashes = file["hashes"] as JsonObject;
            var urls = (file["downloads"] as JsonArray ?? []).Select(u => u?.GetValue<string>()).OfType<string>()
                .Where(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || u.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).ToList();
            var client = Str(file["env"] as JsonObject, "client") switch
            {
                "unsupported" => PackSide.Unsupported,
                "optional" => PackSide.Optional,
                _ => PackSide.Required,
            };
            manifest.Files.Add(new PackFile(path)
            {
                Urls = urls,
                Sha1 = Str(hashes, "sha1"),
                Sha512 = Str(hashes, "sha512"),
                Size = file["fileSize"] is JsonValue s && s.TryGetValue<long>(out var size) ? size : null,
                Client = client,
            });
        }
        AddOverrides(zip, manifest, "overrides/");
        AddOverrides(zip, manifest, "client-overrides/");
        manifest.IconEntry = zip.GetEntry("icon.png") is not null ? "icon.png" : null;
        return manifest;
    }

    internal static (string? Uid, string? Version) LoaderFromModrinth(JsonObject deps)
    {
        if (Str(deps, "neoforge") is { } neo)
            return (ComponentUids.NeoForge, neo);
        if (Str(deps, "forge") is { } forge)
            return (ComponentUids.Forge, VersionProfileInspector.StripMinecraftPrefix(forge));
        if (Str(deps, "fabric-loader") is { } fabric)
            return (ComponentUids.FabricLoader, fabric);
        if (Str(deps, "quilt-loader") is { } quilt)
            return (ComponentUids.QuiltLoader, quilt);
        return (null, null);
    }

    // ── CurseForge zip ────────────────────────────────────────────────────

    private static PackManifest ReadCurseForge(ZipArchive zip, JsonObject m)
    {
        var mc = m["minecraft"] as JsonObject ?? throw new InvalidDataException("manifest.json has no minecraft section.");
        var loaders = (mc["modLoaders"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        var primary = loaders.FirstOrDefault(l => l["primary"] is JsonValue p && p.TryGetValue<bool>(out var b) && b) ?? loaders.FirstOrDefault();
        var (uid, version) = ParseCurseForgeLoader(Str(primary, "id"), Str(mc, "version"));
        var manifest = new PackManifest
        {
            Format = PackFormat.CurseForge,
            Name = Str(m, "name") ?? "CurseForge pack",
            Version = Str(m, "version"),
            Author = Str(m, "author"),
            MinecraftVersion = Str(mc, "version") ?? throw new InvalidDataException("manifest.json doesn't say which Minecraft version it needs."),
            LoaderUid = uid,
            LoaderVersion = version,
        };
        if (mc["recommendedRam"] is JsonValue ram && ram.TryGetValue<int>(out var mb) && mb >= 512)
            manifest.Settings.MaxMemoryMb = mb;
        foreach (var f in m["files"] as JsonArray ?? [])
        {
            if (f is JsonObject file && Long(file, "projectID") is { } project && Long(file, "fileID") is { } fileId)
                manifest.UnresolvedCurseForge.Add((project, fileId, file["required"] is not JsonValue r || !r.TryGetValue<bool>(out var req) || req));
        }
        var overrides = Str(m, "overrides") ?? "overrides";
        AddOverrides(zip, manifest, overrides.TrimEnd('/') + "/");
        return manifest;
    }

    /// <summary><c>forge-47.2.0</c>, <c>neoforge-21.1.80</c>, <c>fabric-0.16.9</c>, <c>quilt-0.26.4</c>.</summary>
    internal static (string? Uid, string? Version) ParseCurseForgeLoader(string? id, string? minecraft)
    {
        var dash = id?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        if (id is null || dash <= 0)
            return (null, null);
        var name = id[..dash].ToLowerInvariant();
        var version = id[(dash + 1)..];
        return name switch
        {
            "forge" => (ComponentUids.Forge, VersionProfileInspector.StripMinecraftPrefix(version)),
            "neoforge" => (ComponentUids.NeoForge, minecraft == "1.20.1" && version.StartsWith("1.20.1-", StringComparison.Ordinal) ? version[7..] : version),
            "fabric" => (ComponentUids.FabricLoader, version),
            "quilt" => (ComponentUids.QuiltLoader, version),
            _ => (null, null),
        };
    }

    // ── Prism / MultiMC zip ───────────────────────────────────────────────

    private static ZipArchiveEntry? FindInstanceCfg(ZipArchive zip) =>
        zip.Entries.Where(e => e.Name == "instance.cfg" && e.FullName.Count(c => c == '/') <= 1).OrderBy(e => e.FullName.Length).FirstOrDefault();

    private static PackManifest ReadMultiMC(ZipArchive zip, ZipArchiveEntry cfgEntry)
    {
        var prefix = cfgEntry.FullName[..^"instance.cfg".Length];
        IniFile cfg;
        using (var reader = new StreamReader(cfgEntry.Open()))
            cfg = IniFile.Parse(reader.ReadToEnd());
        var warnings = new List<string>();
        var components = zip.GetEntry(prefix + "mmc-pack.json") is { } pack
            ? MultiMCLayout.ParseComponents(ReadBytes(pack), uid => zip.GetEntry($"{prefix}patches/{uid}.json") is not null, warnings)
            : throw new InvalidDataException("The Prism/MultiMC zip has no mmc-pack.json.");
        var mc = components.FirstOrDefault(c => c.Uid == ComponentUids.Minecraft)?.Version
            ?? throw new InvalidDataException("mmc-pack.json has no Minecraft component.");
        var loader = ComponentResolver.PrimaryLoader(new InstanceManifest { Components = components });
        var manifest = new PackManifest
        {
            Format = PackFormat.MultiMC,
            Name = cfg.Get("name") is { Length: > 0 } n ? n : "Imported instance",
            Summary = cfg.Get("notes"),
            MinecraftVersion = mc,
            LoaderUid = loader?.Uid,
            LoaderVersion = loader?.Version,
        };
        manifest.Warnings.AddRange(warnings);
        InstanceSettingsDiff.Overlay(manifest.Settings, MultiMCLayout.ReadSettings(cfg));

        // Prism's rule: minecraft/ unless only .minecraft/ exists.
        var hasMc = zip.Entries.Any(e => e.FullName.StartsWith(prefix + "minecraft/", StringComparison.Ordinal));
        var hasDot = zip.Entries.Any(e => e.FullName.StartsWith(prefix + ".minecraft/", StringComparison.Ordinal));
        AddOverrides(zip, manifest, prefix + (hasDot && !hasMc ? ".minecraft/" : "minecraft/"));
        if (zip.Entries.Any(e => e.FullName.StartsWith(prefix + "jarmods/", StringComparison.Ordinal) && e.Length > 0))
            manifest.Warnings.Add("The instance uses jar mods, which Fluorite doesn't support yet; they were not imported.");
        // Prism exports the icon (a built-in one rendered to PNG too) next to instance.cfg, named after its key;
        // otherwise it looks for icon.png, as Prism's import does.
        var key = cfg.Get("iconKey");
        var topLevel = zip.Entries.Where(e => e.Name.Length > 0 && e.FullName.StartsWith(prefix, StringComparison.Ordinal) && e.FullName.IndexOf('/', prefix.Length) < 0)
            .Select(e => e.FullName).ToList();
        manifest.IconEntry = (PrismIcons.IsDefault(key) ? null : PrismIcons.Best(topLevel, key!)) ?? PrismIcons.Best(topLevel, "icon");
        if (manifest.IconEntry is null && key is not null && PrismIcons.BuiltIn.Contains(key))
            manifest.IconKey = key;
        return manifest;
    }

    // ── Technic zip ───────────────────────────────────────────────────────

    /// <summary>
    /// Technic packs ship their loader as <c>bin/version.json</c> or a <c>bin/modpack.jar</c> containing one (Forge
    /// universal era). Everything else in the zip goes into the game directory as is.
    /// </summary>
    public static PackManifest ReadTechnic(ZipArchive zip, string? minecraftHint, string? name = null)
    {
        JsonObject? versionJson = null;
        string? fmlMinecraft = null;
        if (zip.GetEntry("bin/version.json") is { } vj)
        {
            versionJson = Parse(vj);
        }
        else if (zip.GetEntry("bin/modpack.jar") is { } jarEntry)
        {
            using var ms = new MemoryStream();
            using (var s = jarEntry.Open())
                s.CopyTo(ms);
            ms.Position = 0;
            using var jar = new ZipArchive(ms, ZipArchiveMode.Read);
            if (jar.GetEntry("version.json") is { } inner)
                versionJson = Parse(inner);
            if (jar.GetEntry("fmlversion.properties") is { } props)
            {
                using var r = new StreamReader(props.Open());
                fmlMinecraft = r.ReadToEnd().Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("fmlbuild.mcversion=", StringComparison.Ordinal))?["fmlbuild.mcversion=".Length..];
            }
            if (versionJson is null)
                throw new InvalidDataException("This Technic pack patches the game jar directly (jar mods), which Fluorite doesn't support yet.");
        }

        var profile = versionJson is null
            ? new InspectedProfile("", minecraftHint, null, null)
            : VersionProfileInspector.Inspect(versionJson, fmlMinecraft ?? minecraftHint);
        var manifest = new PackManifest
        {
            Format = PackFormat.Technic,
            Name = name ?? "Technic pack",
            MinecraftVersion = profile.MinecraftVersion ?? minecraftHint ?? throw new InvalidDataException("Couldn't tell which Minecraft version this Technic pack needs."),
            LoaderUid = profile.LoaderUid,
            LoaderVersion = profile.LoaderVersion,
        };
        foreach (var e in zip.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.StartsWith("bin/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (PackPaths.Sanitize(e.FullName) is { } path)
                manifest.Overrides.Add(new PackOverride(e.FullName, path));
        }
        return manifest;
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static void AddOverrides(ZipArchive zip, PackManifest manifest, string prefix)
    {
        foreach (var e in zip.Entries)
        {
            if (!e.FullName.StartsWith(prefix, StringComparison.Ordinal) || e.FullName.EndsWith('/') || e.FullName.Length == prefix.Length)
                continue;
            var path = PackPaths.Sanitize(e.FullName[prefix.Length..]);
            if (path is null)
            {
                manifest.Warnings.Add($"Skipped an override with an unsafe path: {e.FullName}");
                continue;
            }
            manifest.Overrides.RemoveAll(o => string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)); // client-overrides win.
            manifest.Overrides.Add(new PackOverride(e.FullName, path));
        }
    }

    private static JsonObject Parse(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        return JsonNode.Parse(s, documentOptions: s_json) as JsonObject ?? throw new InvalidDataException($"{entry.FullName} is not a JSON object.");
    }

    private static byte[] ReadBytes(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    internal static string? Str(JsonObject? o, string key) =>
        o?[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    internal static long? Long(JsonObject o, string key) => o[key] switch
    {
        JsonValue v when v.TryGetValue<long>(out var l) => l,
        JsonValue v when v.TryGetValue<string>(out var s) && long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    /// <summary>Provenance record for a file Fluorite knows the platform identity of.</summary>
    internal static RemoteRef CurseForgeRef(long projectId, long fileId, string? versionName = null) => new()
    {
        Platform = ContentPlatform.CurseForge,
        ProjectId = projectId.ToString(CultureInfo.InvariantCulture),
        VersionId = fileId.ToString(CultureInfo.InvariantCulture),
        VersionNumber = versionName,
    };
}
