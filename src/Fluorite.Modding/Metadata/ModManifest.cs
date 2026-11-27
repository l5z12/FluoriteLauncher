using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Fluorite.Core;
using Tomlyn;
using Tomlyn.Model;

namespace Fluorite.Modding.Metadata;

/// <summary>
/// A mod jar's own manifest (<c>fabric.mod.json</c>, <c>mods.toml</c>…), to read, check and write back into the
/// jar: for a mod that declares stricter versions than it really needs ("requires Minecraft 1.21.1" when it runs on
/// 1.21.4 too). The original jar is kept by the caller so the edit can be undone.
/// </summary>
public static partial class ModManifest
{
    /// <summary>The manifests Fluorite can edit, in the order they're offered.</summary>
    public static IReadOnlyList<string> EntryNames { get; } =
        ["fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info", "litemod.json"];

    /// <summary>The manifest a mod for <paramref name="loader"/> is read from, when the jar has several.</summary>
    public static string EntryFor(ModLoaderKind loader) => loader switch
    {
        ModLoaderKind.Quilt => "quilt.mod.json",
        ModLoaderKind.NeoForge => "META-INF/neoforge.mods.toml",
        ModLoaderKind.Forge => "META-INF/mods.toml",
        ModLoaderKind.LegacyForge => "mcmod.info",
        ModLoaderKind.LiteLoader => "litemod.json",
        _ => "fabric.mod.json",
    };

    /// <summary>Which of <see cref="EntryNames"/> the jar has (their names as stored in it).</summary>
    public static IReadOnlyList<string> Find(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        return EntryNames.Select(n => FindEntry(zip, n)?.FullName).OfType<string>().ToList();
    }

    /// <summary>Signed: its signature covers the manifest, so an edit breaks it (and saving removes it).</summary>
    public static bool IsSigned(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        return zip.Entries.Any(IsSignature);
    }

    public static string Read(string jarPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = FindEntry(zip, entryName) ?? throw new FileNotFoundException($"{entryName} isn't in {Path.GetFileName(jarPath)}.");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static readonly JsonDocumentOptions s_jsonRead = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>Why <paramref name="text"/> isn't a valid manifest of its kind, or null when it is.</summary>
    public static string? Validate(string entryName, string text)
    {
        if (IsToml(entryName))
        {
            try
            {
                TomlSerializer.Deserialize<TomlTable>(text, ModsTomlContext.Default);
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return ex.Message;
            }
        }
        try
        {
            using var _ = JsonDocument.Parse(text, s_jsonRead);
            return null;
        }
        catch (JsonException ex)
        {
            return ex.LineNumber is { } line ? $"Line {line + 1}, column {(ex.BytePositionInLine ?? 0) + 1}: {FirstSentence(ex.Message)}" : ex.Message;
        }
    }

    private static string FirstSentence(string message) => message.Split(" LineNumber:", 2)[0].TrimEnd(' ', '.') + ".";

    /// <summary>
    /// <paramref name="text"/> with the dependency on <paramref name="dependencyId"/> (<c>minecraft</c>,
    /// <c>fabricloader</c>, <c>forge</c>…) widened to also accept <paramref name="version"/>; null when it already
    /// does, has no such dependency, or the manifest can't be edited this way (mcmod.info: Forge 1.12 doesn't check it).
    /// </summary>
    public static string? AllowVersion(string entryName, string text, string dependencyId, string version)
    {
        try
        {
            return entryName.ToLowerInvariant() switch
            {
                "fabric.mod.json" => AllowFabric(text, dependencyId, version),
                "quilt.mod.json" => AllowQuilt(text, dependencyId, version),
                _ when IsToml(entryName) => AllowToml(text, dependencyId, version),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null; // Not valid yet; the editor says why.
        }
    }

    private static readonly JsonSerializerOptions s_jsonWrite = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string? AllowFabric(string text, string dependencyId, string version)
    {
        if (JsonNode.Parse(text, documentOptions: s_jsonRead) is not JsonObject root
            || root["depends"] is not JsonObject depends || depends[dependencyId] is not { } predicate
            || Widen(predicate, version) is not { } widened)
            return null;
        depends[dependencyId] = widened;
        return root.ToJsonString(s_jsonWrite);
    }

    private static string? AllowQuilt(string text, string dependencyId, string version)
    {
        if (JsonNode.Parse(text, documentOptions: s_jsonRead) is not JsonObject root
            || root["quilt_loader"]?["depends"] is not JsonArray depends)
            return null;
        var changed = false;
        foreach (var dep in depends.OfType<JsonObject>().Where(d => d["id"]?.GetValueKind() == JsonValueKind.String && (string?)d["id"] == dependencyId))
        {
            if (dep["versions"] is not { } versions)
                continue; // Any version.
            JsonNode? widened = versions is JsonObject
                ? Accepts(versions, version) ? null : new JsonObject { ["any"] = new JsonArray(versions.DeepClone(), version) }
                : Widen(versions, version);
            if (widened is null)
                continue;
            dep["versions"] = widened;
            changed = true;
        }
        return changed ? root.ToJsonString(s_jsonWrite) : null;
    }

    /// <summary>A Fabric/Quilt predicate (string or any-of array) that also accepts <paramref name="version"/>, or null if it already does.</summary>
    private static JsonArray? Widen(JsonNode predicate, string version)
    {
        if (Accepts(predicate, version))
            return null;
        var any = predicate is JsonArray a ? a.Select(n => n?.DeepClone()).ToArray() : [predicate.DeepClone()];
        return new JsonArray([.. any, version]);
    }

    private static bool Accepts(JsonNode predicate, string version) => predicate switch
    {
        JsonValue v when v.GetValueKind() == JsonValueKind.String => VersionPredicate.MatchesFabric((string?)v, version) == true,
        JsonArray a => a.Any(n => n is not null && Accepts(n, version)),
        JsonObject o => o["any"] is JsonArray any ? any.Any(n => n is not null && Accepts(n, version))
            : o["all"] is JsonArray all && all.All(n => n is not null && Accepts(n, version)),
        _ => false,
    };

    /// <summary>
    /// mods.toml: in each <c>[[dependencies.*]]</c> block on <paramref name="dependencyId"/>, its <c>versionRange</c>
    /// gets <c>,[version]</c> (a Maven range may be a union of ranges). Edited as text, so the rest stays as written.
    /// </summary>
    private static string? AllowToml(string text, string dependencyId, string version)
    {
        var lines = text.Split('\n');
        var changed = false;
        var inDependency = false;
        var isTarget = false;
        var rangeLine = -1;

        void EndBlock()
        {
            if (inDependency && isTarget && rangeLine >= 0
                && TomlKeyValue().Match(lines[rangeLine]) is { Success: true } m
                && m.Groups["value"].Value is { Length: > 0 } range && range != "*"
                && VersionPredicate.MatchesMaven(range, version) != true)
            {
                var value = m.Groups["value"];
                lines[rangeLine] = lines[rangeLine][..value.Index] + range + ",[" + version + "]" + lines[rangeLine][(value.Index + value.Length)..];
                changed = true;
            }
            (isTarget, rangeLine) = (false, -1);
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith('['))
            {
                EndBlock();
                inDependency = trimmed.StartsWith("[[dependencies.", StringComparison.Ordinal);
                continue;
            }
            if (!inDependency || TomlKeyValue().Match(lines[i]) is not { Success: true } kv)
                continue;
            if (kv.Groups["key"].Value == "modId" && kv.Groups["value"].Value == dependencyId)
                isTarget = true;
            else if (kv.Groups["key"].Value == "versionRange")
                rangeLine = i;
        }
        EndBlock();
        return changed ? string.Join('\n', lines) : null;
    }

    [GeneratedRegex("""^\s*(?<key>[A-Za-z]+)\s*=\s*(?<q>["'])(?<value>[^"']*)\k<q>""")]
    private static partial Regex TomlKeyValue();

    /// <summary>
    /// Writes <paramref name="text"/> as <paramref name="entryName"/> into the jar (via a copy, swapped in at the end),
    /// keeping the jar as it was at <paramref name="originalCopy"/> first unless a copy is already there. A signed
    /// jar loses its signature, which the edit breaks anyway; returns true when that happened. Throws
    /// <see cref="InvalidDataException"/> when the edited jar no longer declares a mod (an id deleted, say).
    /// </summary>
    public static bool Write(string jarPath, string entryName, string text, string originalCopy)
    {
        var temp = jarPath + ".fluorite-edit";
        var unsigned = false;
        try
        {
            File.Copy(jarPath, temp, overwrite: true);
            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                var old = FindEntry(zip, entryName) ?? throw new FileNotFoundException($"{entryName} isn't in {Path.GetFileName(jarPath)}.");
                var name = old.FullName;
                old.Delete();
                using (var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false)))
                    writer.Write(text);
                foreach (var signature in zip.Entries.Where(IsSignature).ToList())
                {
                    signature.Delete();
                    unsigned = true;
                }
            }
            if (ContentMetadataReader.Read(temp).Mods.Count == 0 && ContentMetadataReader.Read(jarPath).Mods.Count > 0)
                throw new InvalidDataException("With this change the file no longer declares a mod. Is its id still there?");

            var copied = false;
            if (!File.Exists(originalCopy))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(originalCopy)!);
                AtomicFile.Copy(jarPath, originalCopy);
                copied = true;
            }
            try
            {
                File.Move(temp, jarPath, overwrite: true); // Fails while the game has the jar open.
            }
            catch when (copied)
            {
                File.Delete(originalCopy);
                throw;
            }
            return unsigned;
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static bool IsToml(string entryName) => entryName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase);

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A jar signature's files: <c>META-INF/*.SF</c> and the signature blocks.</summary>
    private static bool IsSignature(ZipArchiveEntry e) =>
        e.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) && !e.FullName[9..].Contains('/', StringComparison.Ordinal)
        && Path.GetExtension(e.FullName).ToUpperInvariant() is ".SF" or ".RSA" or ".DSA" or ".EC";
}
