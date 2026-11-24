using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tomlyn;
using Tomlyn.Model;

namespace Fluorite.Modding.Metadata;

/// <summary>
/// Reads what a mod jar / pack zip / pack folder declares about itself: <c>fabric.mod.json</c>, <c>quilt.mod.json</c>,
/// <c>META-INF/mods.toml</c>, <c>META-INF/neoforge.mods.toml</c>, <c>mcmod.info</c>, <c>litemod.json</c> and
/// <c>pack.mcmeta</c>, plus jar-in-jar (Fabric/Quilt <c>jars</c>, Forge <c>META-INF/jarjar/metadata.json</c>).
/// Never throws for malformed content: unknown or broken metadata yields <see cref="ContentMetadata.Empty"/>.
/// </summary>
public static partial class ContentMetadataReader
{
    private const int MaxNestingDepth = 3;
    private const long MaxNestedJarBytes = 64L << 20;

    private static readonly JsonDocumentOptions s_json = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static ContentMetadata Read(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return ReadDirectory(path);
            using var zip = ZipFile.OpenRead(path);
            return Read(zip, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            return ContentMetadata.Empty;
        }
    }

    public static ContentMetadata Read(Stream archive)
    {
        try
        {
            using var zip = new ZipArchive(archive, ZipArchiveMode.Read, leaveOpen: true);
            return Read(zip, 0);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            return ContentMetadata.Empty;
        }
    }

    /// <summary>Reads an archive entry's bytes (e.g. <see cref="ContentMetadata.IconEntry"/>); null if missing or too big.</summary>
    public static byte[]? ReadEntry(string archivePath, string entryName, long maxBytes = 4 << 20)
    {
        try
        {
            if (Directory.Exists(archivePath))
            {
                var file = Path.Combine(archivePath, entryName.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(file) && new FileInfo(file).Length <= maxBytes ? File.ReadAllBytes(file) : null;
            }
            using var zip = ZipFile.OpenRead(archivePath);
            var entry = FindEntry(zip, entryName);
            if (entry is null || entry.Length > maxBytes)
                return null;
            using var s = entry.Open();
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return ms.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ContentMetadata ReadDirectory(string dir)
    {
        var mcmeta = Path.Combine(dir, "pack.mcmeta");
        var hasShaders = Directory.Exists(Path.Combine(dir, "shaders"));
        if (!File.Exists(mcmeta))
            return new ContentMetadata { IsShaderPack = hasShaders, Name = Path.GetFileName(dir) };
        var (format, description) = ParsePackMcmeta(File.ReadAllText(mcmeta));
        return new ContentMetadata
        {
            PackFormat = format,
            Description = description,
            IconEntry = File.Exists(Path.Combine(dir, "pack.png")) ? "pack.png" : null,
            IsShaderPack = hasShaders,
        };
    }

    private static ContentMetadata Read(ZipArchive zip, int depth)
    {
        var mods = new List<ModInfo>();
        string? icon = null;
        var nestedPaths = new List<string>();
        string? manifestVersion = null;

        string? Text(string name) => FindEntry(zip, name) is { } e ? ReadText(e) : null;

        if (Text("fabric.mod.json") is { } fabric && ParseFabric(fabric) is { } f)
        {
            mods.Add(f.Mod);
            icon ??= f.Icon;
            nestedPaths.AddRange(f.Jars);
        }
        if (Text("quilt.mod.json") is { } quilt && ParseQuilt(quilt) is { } q)
        {
            mods.Add(q.Mod);
            icon ??= q.Icon;
            nestedPaths.AddRange(q.Jars);
        }

        var neoToml = Text("META-INF/neoforge.mods.toml");
        var forgeToml = Text("META-INF/mods.toml");
        if (neoToml is not null || forgeToml is not null)
        {
            manifestVersion = ReadManifestVersion(Text("META-INF/MANIFEST.MF"));
            if (neoToml is not null)
            {
                var (list, logo) = ParseModsToml(neoToml, ModLoaderKind.NeoForge, manifestVersion);
                mods.AddRange(list);
                icon ??= logo;
            }
            if (forgeToml is not null)
            {
                var (list, logo) = ParseModsToml(forgeToml, ModLoaderKind.Forge, manifestVersion);
                // A jar shipping both tomls lists the same mods twice; keep the Forge entries only for new ids.
                mods.AddRange(list.Where(m => !mods.Any(x => x.Id == m.Id && x.Loader == ModLoaderKind.NeoForge)));
                icon ??= logo;
            }
            if (Text("META-INF/jarjar/metadata.json") is { } jarjar)
                nestedPaths.AddRange(ParseJarJar(jarjar));
        }

        if (mods.Count == 0 && Text("mcmod.info") is { } mcmod)
        {
            var (list, logo) = ParseMcModInfo(mcmod);
            mods.AddRange(list);
            icon ??= logo;
        }
        if (mods.Count == 0 && Text("litemod.json") is { } litemod && ParseLiteMod(litemod) is { } lite)
            mods.Add(lite);

        // OptiFine has no mod metadata; it's recognisable by its own packages.
        var isOptiFine = mods.Count == 0 && zip.Entries.Any(e =>
            e.FullName.StartsWith("optifine/", StringComparison.Ordinal) || e.FullName.StartsWith("net/optifine/", StringComparison.Ordinal));

        int? packFormat = null;
        string? packDescription = null;
        if (Text("pack.mcmeta") is { } mcmetaText)
        {
            (packFormat, packDescription) = ParsePackMcmeta(mcmetaText);
            if (mods.Count == 0 && FindEntry(zip, "pack.png") is not null)
                icon ??= "pack.png";
        }
        var isShaderPack = mods.Count == 0 && zip.Entries.Any(e => e.FullName.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase));

        var nested = new List<ModInfo>();
        if (depth < MaxNestingDepth)
        {
            foreach (var path in nestedPaths.Distinct(StringComparer.Ordinal))
            {
                if (FindEntry(zip, path) is not { } entry || entry.Length > MaxNestedJarBytes)
                    continue;
                try
                {
                    // Exactly its size: a growing MemoryStream allocates (and discards) up to twice that, per nested jar.
                    var bytes = new byte[entry.Length];
                    using (var s = entry.Open())
                        s.ReadExactly(bytes);
                    using var inner = new ZipArchive(new MemoryStream(bytes, writable: false), ZipArchiveMode.Read);
                    var meta = Read(inner, depth + 1);
                    nested.AddRange(meta.Mods);
                    nested.AddRange(meta.Nested);
                }
                catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                {
                }
            }
        }

        var primary = mods.FirstOrDefault();
        if (icon is not null && FindEntry(zip, icon) is null)
            icon = null;
        return new ContentMetadata
        {
            Mods = mods,
            Nested = nested,
            Name = primary?.Name ?? (isOptiFine ? "OptiFine" : null),
            Version = primary?.Version,
            Description = primary?.Description ?? packDescription,
            Authors = primary?.Authors ?? [],
            IconEntry = icon,
            PackFormat = packFormat,
            IsShaderPack = isShaderPack,
            IsOptiFine = isOptiFine,
        };
    }

    // ── Fabric ────────────────────────────────────────────────────────────

    internal static (ModInfo Mod, string? Icon, List<string> Jars)? ParseFabric(string text)
    {
        using var doc = ParseJson(text);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        var root = doc.RootElement;
        if (Str(root, "id") is not { Length: > 0 } id)
            return null;

        var deps = new List<ModDependency>();
        AddFabricDeps(root, "depends", DependencyKind.Required, deps);
        AddFabricDeps(root, "recommends", DependencyKind.Optional, deps);
        AddFabricDeps(root, "suggests", DependencyKind.Optional, deps);
        AddFabricDeps(root, "breaks", DependencyKind.Incompatible, deps);
        AddFabricDeps(root, "conflicts", DependencyKind.Discouraged, deps);

        var provides = new List<string>();
        if (root.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
            provides.AddRange(p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));

        var jars = new List<string>();
        if (root.TryGetProperty("jars", out var j) && j.ValueKind == JsonValueKind.Array)
        {
            foreach (var jar in j.EnumerateArray())
            {
                if (jar.ValueKind == JsonValueKind.Object && Str(jar, "file") is { Length: > 0 } file)
                    jars.Add(file);
            }
        }

        var mod = new ModInfo(id, Str(root, "name"), Str(root, "version"), ModLoaderKind.Fabric, deps, provides)
        {
            Description = Str(root, "description"),
            Authors = People(root, "authors"),
            Environment = Str(root, "environment"),
            Links = Contact(root),
            License = License(root),
        };
        return (mod, Icon(root, "icon"), jars);
    }

    private static void AddFabricDeps(JsonElement root, string property, DependencyKind kind, List<ModDependency> deps)
    {
        if (!root.TryGetProperty(property, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return;
        foreach (var dep in obj.EnumerateObject())
        {
            var range = dep.Value.ValueKind switch
            {
                JsonValueKind.String => dep.Value.GetString(),
                // An array is an OR of predicates; keep it as "a || b" for VersionPredicate.
                JsonValueKind.Array => string.Join(" || ", dep.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
                _ => null,
            };
            deps.Add(new ModDependency(dep.Name, kind, range is "*" or "" ? null : range));
        }
    }

    // ── Quilt ─────────────────────────────────────────────────────────────

    internal static (ModInfo Mod, string? Icon, List<string> Jars)? ParseQuilt(string text)
    {
        using var doc = ParseJson(text);
        if (doc is null || !doc.RootElement.TryGetProperty("quilt_loader", out var ql) || ql.ValueKind != JsonValueKind.Object)
            return null;
        if (Str(ql, "id") is not { Length: > 0 } id)
            return null;

        var deps = new List<ModDependency>();
        AddQuiltDeps(ql, "depends", DependencyKind.Required, deps);
        AddQuiltDeps(ql, "breaks", DependencyKind.Incompatible, deps);

        var provides = new List<string>();
        if (ql.TryGetProperty("provides", out var p) && p.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in p.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.String)
                    provides.Add(e.GetString()!);
                else if (e.ValueKind == JsonValueKind.Object && Str(e, "id") is { } pid)
                    provides.Add(pid);
            }
        }

        var jars = new List<string>();
        if (ql.TryGetProperty("jars", out var j) && j.ValueKind == JsonValueKind.Array)
            jars.AddRange(j.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));

        string? name = null, description = null, icon = null, license = null;
        var authors = new List<string>();
        List<ModLink> links = [];
        if (ql.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            name = Str(meta, "name");
            description = Str(meta, "description");
            icon = Icon(meta, "icon");
            if (meta.TryGetProperty("contributors", out var c) && c.ValueKind == JsonValueKind.Object)
                authors.AddRange(c.EnumerateObject().Select(x => x.Name));
            links = Contact(meta);
            license = License(meta);
        }
        string? environment = null;
        if (doc.RootElement.TryGetProperty("minecraft", out var mc) && mc.ValueKind == JsonValueKind.Object)
            environment = Str(mc, "environment");

        var mod = new ModInfo(id, name, Str(ql, "version"), ModLoaderKind.Quilt, deps, provides)
        {
            Description = description,
            Authors = authors,
            Environment = environment,
            Links = links,
            License = license,
        };
        return (mod, icon, jars);
    }

    private static void AddQuiltDeps(JsonElement ql, string property, DependencyKind kind, List<ModDependency> deps)
    {
        if (!ql.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return;
        foreach (var d in arr.EnumerateArray())
        {
            if (d.ValueKind == JsonValueKind.String)
            {
                deps.Add(new ModDependency(StripMavenGroup(d.GetString()!), kind, null));
            }
            else if (d.ValueKind == JsonValueKind.Object && Str(d, "id") is { } did)
            {
                var optional = d.TryGetProperty("optional", out var o) && o.ValueKind == JsonValueKind.True;
                string? range = null;
                if (d.TryGetProperty("versions", out var v))
                {
                    range = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Array => string.Join(" || ", v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())),
                        _ => null, // {"any": […]}/{"all": […]} objects: too rare to evaluate, treat as any.
                    };
                }
                deps.Add(new ModDependency(StripMavenGroup(did), optional && kind == DependencyKind.Required ? DependencyKind.Optional : kind, range is "*" or "" ? null : range));
            }
        }
    }

    // Quilt ids may be "group:id"; mod ids are compared without the group.
    private static string StripMavenGroup(string id) => id.Contains(':', StringComparison.Ordinal) ? id[(id.IndexOf(':', StringComparison.Ordinal) + 1)..] : id;

    // ── Forge / NeoForge mods.toml ────────────────────────────────────────

    internal static (List<ModInfo> Mods, string? Logo) ParseModsToml(string text, ModLoaderKind loader, string? manifestVersion)
    {
        var result = new List<ModInfo>();
        TomlTable table;
        try
        {
            table = TomlSerializer.Deserialize<TomlTable>(text, ModsTomlContext.Default)!;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (result, null);
        }
        if (table is null)
            return (result, null);

        var logo = TomlStr(table, "logoFile");
        var dependencies = table.TryGetValue("dependencies", out var d) && d is TomlTable dt ? dt : null;
        if (!table.TryGetValue("mods", out var modsObj) || modsObj is not TomlTableArray mods)
            return (result, logo);

        foreach (var m in mods)
        {
            if (TomlStr(m, "modId") is not { Length: > 0 } id)
                continue;
            var version = TomlStr(m, "version");
            if (version is not null && version.Contains("${file.jarVersion}", StringComparison.Ordinal))
                version = manifestVersion ?? null;

            var deps = new List<ModDependency>();
            var effectiveLoader = loader;
            if (dependencies is not null && dependencies.TryGetValue(id, out var depsObj) && depsObj is TomlTableArray depArray)
            {
                foreach (var dep in depArray)
                {
                    if (TomlStr(dep, "modId") is not { Length: > 0 } depId)
                        continue;
                    var side = TomlStr(dep, "side");
                    if (string.Equals(side, "SERVER", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var kind = TomlStr(dep, "type")?.ToLowerInvariant() switch
                    {
                        "required" => DependencyKind.Required,
                        "optional" => DependencyKind.Optional,
                        "incompatible" => DependencyKind.Incompatible,
                        "discouraged" => DependencyKind.Discouraged,
                        _ => dep.TryGetValue("mandatory", out var mand) && mand is bool b && !b ? DependencyKind.Optional : DependencyKind.Required,
                    };
                    var range = TomlStr(dep, "versionRange");
                    deps.Add(new ModDependency(depId, kind, range is "*" or "" ? null : range));
                    // Early NeoForge (1.20.2–1.20.4) still used mods.toml; tell it apart by its dependency.
                    if (loader == ModLoaderKind.Forge && depId == "neoforge" && kind == DependencyKind.Required)
                        effectiveLoader = ModLoaderKind.NeoForge;
                }
            }

            // displayURL is the mod's own; the issue tracker and license are the file's, shared by all its mods.
            var links = new List<ModLink>();
            AddLink(links, ModLink.Homepage, TomlStr(m, "displayURL"));
            AddLink(links, ModLink.Issues, TomlStr(m, "issueTrackerURL") ?? TomlStr(table, "issueTrackerURL"));
            result.Add(new ModInfo(id, TomlStr(m, "displayName"), version, effectiveLoader, deps, [])
            {
                Description = TomlStr(m, "description")?.Trim(),
                Authors = SplitAuthors(TomlStr(m, "authors") ?? TomlStr(table, "authors")),
                Links = links,
                License = Clean(TomlStr(table, "license")),
            });
            logo ??= TomlStr(m, "logoFile");
        }
        return (result, logo);
    }

    private static string? TomlStr(TomlTable t, string key) =>
        t.TryGetValue(key, out var v) ? v switch
        {
            string s => s,
            TomlArray a => string.Join(", ", a.OfType<string>()),
            _ => null,
        } : null;

    private static string[] SplitAuthors(string? authors) =>
        string.IsNullOrWhiteSpace(authors)
            ? []
            : authors.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? ReadManifestVersion(string? manifest)
    {
        if (manifest is null)
            return null;
        foreach (var line in manifest.Split('\n'))
        {
            var l = line.TrimEnd('\r');
            if (l.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase))
                return l["Implementation-Version:".Length..].Trim();
        }
        return null;
    }

    internal static List<string> ParseJarJar(string text)
    {
        var result = new List<string>();
        using var doc = ParseJson(text);
        if (doc is null || !doc.RootElement.TryGetProperty("jars", out var jars) || jars.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var jar in jars.EnumerateArray())
        {
            if (jar.ValueKind == JsonValueKind.Object && Str(jar, "path") is { Length: > 0 } path)
                result.Add(path);
        }
        return result;
    }

    // ── Legacy Forge mcmod.info ───────────────────────────────────────────

    internal static (List<ModInfo> Mods, string? Logo) ParseMcModInfo(string text)
    {
        var result = new List<ModInfo>();
        using var doc = ParseJson(text);
        if (doc is null)
            return (result, null);
        var root = doc.RootElement;
        var list = root.ValueKind switch
        {
            JsonValueKind.Array => root,
            JsonValueKind.Object when root.TryGetProperty("modList", out var ml) && ml.ValueKind == JsonValueKind.Array => ml,
            _ => default,
        };
        if (list.ValueKind != JsonValueKind.Array)
            return (result, null);

        string? logo = null;
        foreach (var m in list.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || Str(m, "modid") is not { Length: > 0 } id)
                continue;
            var deps = new List<ModDependency>();
            if (m.TryGetProperty("requiredMods", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in req.EnumerateArray())
                {
                    if (r.ValueKind != JsonValueKind.String)
                        continue;
                    var spec = r.GetString()!;
                    var at = spec.IndexOf('@', StringComparison.Ordinal);
                    deps.Add(at < 0 ? new ModDependency(spec, DependencyKind.Required, null) : new ModDependency(spec[..at], DependencyKind.Required, spec[(at + 1)..]));
                }
            }
            // A bare mcversion ("1.12") is informational in legacy Forge; only explicit ranges are enforced.
            if (Str(m, "mcversion") is { Length: > 0 } mcv && mcv[0] is '[' or '(')
                deps.Add(new ModDependency("minecraft", DependencyKind.Required, mcv));

            var authors = People(m, "authorList");
            if (authors.Count == 0)
                authors = People(m, "authors");
            var links = new List<ModLink>();
            AddLink(links, ModLink.Homepage, Str(m, "url")); // updateUrl is for update checks, not people.
            result.Add(new ModInfo(id, Str(m, "name"), Str(m, "version"), ModLoaderKind.LegacyForge, deps, [])
            {
                Description = Str(m, "description")?.Trim(),
                Authors = authors,
                Links = links,
            });
            logo ??= Str(m, "logoFile") is { Length: > 0 } lf ? lf.TrimStart('/') : null;
        }
        return (result, logo);
    }

    // ── LiteLoader ────────────────────────────────────────────────────────

    internal static ModInfo? ParseLiteMod(string text)
    {
        using var doc = ParseJson(text);
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object)
            return null;
        var r = doc.RootElement;
        var name = Str(r, "name");
        if (string.IsNullOrEmpty(name))
            return null;
        return new ModInfo(name.ToLowerInvariant(), name, Str(r, "version"), ModLoaderKind.LiteLoader, [], [])
        {
            Description = Str(r, "description"),
            Authors = SplitAuthors(Str(r, "author")),
        };
    }

    // ── pack.mcmeta ───────────────────────────────────────────────────────

    internal static (int? Format, string? Description) ParsePackMcmeta(string text)
    {
        using var doc = ParseJson(text);
        if (doc is null || !doc.RootElement.TryGetProperty("pack", out var pack) || pack.ValueKind != JsonValueKind.Object)
            return (null, null);
        int? format = pack.TryGetProperty("pack_format", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var n) ? n : null;
        var description = pack.TryGetProperty("description", out var d) ? PlainText(FlattenTextComponent(d)) : null;
        return (format, string.IsNullOrWhiteSpace(description) ? null : description.Trim());
    }

    /// <summary>Plain text of a Minecraft text component (string, {"text","extra"} or array).</summary>
    internal static string FlattenTextComponent(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                return e.GetString() ?? "";
            case JsonValueKind.Array:
                return string.Concat(e.EnumerateArray().Select(FlattenTextComponent));
            case JsonValueKind.Object:
                var sb = new StringBuilder();
                if (e.TryGetProperty("text", out var t))
                    sb.Append(FlattenTextComponent(t));
                else if (e.TryGetProperty("translate", out var tr) && tr.ValueKind == JsonValueKind.String)
                    sb.Append(tr.GetString());
                if (e.TryGetProperty("extra", out var extra))
                    sb.Append(FlattenTextComponent(extra));
                return sb.ToString();
            case JsonValueKind.Number:
                return e.GetRawText();
            default:
                return "";
        }
    }

    /// <summary>
    /// Text as Fluorite shows it: without legacy <c>§x</c> formatting codes, and without private-use characters
    /// (icons a pack's own font draws in game, which any other font shows as boxes).
    /// </summary>
    public static string PlainText(string text) => NotPlainText().Replace(text, "");

    // UTF-16: the BMP private-use area, and planes 15–16 (high surrogates DB80–DBFF).
    [GeneratedRegex(@"§.|[-]|[\uDB80-\uDBFF][\uDC00-\uDFFF]", RegexOptions.CultureInvariant)]
    private static partial Regex NotPlainText();

    // ── helpers ───────────────────────────────────────────────────────────

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name)
    {
        name = name.TrimStart('/');
        return zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ReadText(ZipArchiveEntry entry)
    {
        if (entry.Length > 4 << 20)
            return null;
        using var s = entry.Open();
        using var reader = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Mod metadata is hand-written and often slightly invalid (raw newlines/tabs inside strings, trailing commas).
    /// Parse strictly first, then retry with control characters turned into spaces.
    /// </summary>
    internal static JsonDocument? ParseJson(string text)
    {
        text = text.TrimStart('﻿');
        try
        {
            return JsonDocument.Parse(text, s_json);
        }
        catch (JsonException)
        {
        }
        try
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
                sb.Append(c is '\n' or '\r' or '\t' ? ' ' : c);
            return JsonDocument.Parse(sb.ToString(), s_json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Fabric-style person list: strings or <c>{"name": …}</c> objects.</summary>
    private static List<string> People(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var list = new List<string>();
        foreach (var p in arr.EnumerateArray())
        {
            if (p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } s)
                list.Add(s);
            else if (p.ValueKind == JsonValueKind.Object && Str(p, "name") is { Length: > 0 } n)
                list.Add(n);
        }
        return list;
    }

    /// <summary>Fabric/Quilt <c>contact</c>: a key → string map, any keys, kept in the order written.</summary>
    private static List<ModLink> Contact(JsonElement obj)
    {
        var links = new List<ModLink>();
        if (obj.TryGetProperty("contact", out var contact) && contact.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in contact.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                    AddLink(links, p.Name.Trim().ToLowerInvariant(), p.Value.GetString());
            }
        }
        return links;
    }

    /// <summary>
    /// Leaves out blanks, build placeholders a dev jar kept (<c>${mod_url}</c>) and an address already listed
    /// (homepage and sources are often the same page).
    /// </summary>
    private static void AddLink(List<ModLink> links, string kind, string? value)
    {
        if (kind.Length > 0 && Clean(value) is { } v && !links.Any(l => string.Equals(l.Value, v, StringComparison.OrdinalIgnoreCase)))
            links.Add(new ModLink(kind, v));
    }

    private static string? Clean(string? value) =>
        value?.Trim() is { Length: > 0 } v && !v.Contains("${", StringComparison.Ordinal) ? v : null;

    /// <summary>Fabric: a string or a list of them. Quilt: also <c>{"name": …, "id": …}</c> objects, or a list of those.</summary>
    private static string? License(JsonElement obj)
    {
        if (!obj.TryGetProperty("license", out var license))
            return null;
        var items = license.ValueKind == JsonValueKind.Array ? license.EnumerateArray().ToList() : [license];
        var names = items.Select(e => e.ValueKind switch
            {
                JsonValueKind.String => e.GetString(),
                JsonValueKind.Object => Str(e, "id") ?? Str(e, "name"),
                _ => null,
            })
            .Select(Clean).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return names.Count > 0 ? string.Join(", ", names) : null;
    }

    /// <summary>Icon path: a string, or a size → path map (largest wins).</summary>
    private static string? Icon(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var icon))
            return null;
        if (icon.ValueKind == JsonValueKind.String)
            return icon.GetString()?.TrimStart('/');
        if (icon.ValueKind == JsonValueKind.Object)
        {
            return icon.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .OrderByDescending(p => int.TryParse(p.Name, out var size) ? size : 0)
                .Select(p => p.Value.GetString()?.TrimStart('/'))
                .FirstOrDefault();
        }
        return null;
    }
}

/// <summary>Source-generated (trim-safe) TOML model reader for mods.toml.</summary>
[Tomlyn.Serialization.TomlSerializable(typeof(TomlTable))]
internal sealed partial class ModsTomlContext : Tomlyn.Serialization.TomlSerializerContext;
