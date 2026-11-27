using System.Text.Json;
using System.Text.Json.Nodes;
using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Modding.Metadata;

namespace Fluorite.Modding.Modpacks;

/// <summary>An instance of a launcher whose folders can't be used in place (CurseForge app, Modrinth app, GDLauncher, ATLauncher, FTB app).</summary>
/// <param name="MinecraftVersion">Null when the launcher's metadata couldn't be read; the user must pick one.</param>
public sealed record ImportCandidate(OtherLauncher Launcher, string Name, string GameDirectory, string? MinecraftVersion, string? LoaderUid, string? LoaderVersion)
{
    public string? IconPath { get; init; }
    public long PlaySeconds { get; init; }
}

/// <summary>
/// Finds instances of other launchers and copies them into Fluorite (game folder + a manifest built from their
/// metadata). Also makes a Fluorite copy of an instance used in place from a Prism/official/HMCL folder.
/// </summary>
public static class InstanceImporter
{
    private static readonly JsonDocumentOptions s_json = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static IReadOnlyList<ImportCandidate> Scan(OtherLauncher launcher, string root)
    {
        var result = new List<ImportCandidate>();
        switch (launcher)
        {
            case OtherLauncher.CurseForge:
                foreach (var dir in Dirs(Path.Combine(root, "Instances")))
                {
                    var json = Load(Path.Combine(dir, "minecraftinstance.json"));
                    var loader = Str(json?["baseModLoader"] as JsonObject, "name");
                    var (uid, version) = ModpackReader.ParseCurseForgeLoader(loader, Str(json, "gameVersion"));
                    result.Add(new ImportCandidate(launcher, Str(json, "name") ?? Path.GetFileName(dir), dir, Str(json, "gameVersion"), uid, version));
                }
                break;

            case OtherLauncher.ModrinthApp:
                foreach (var dir in Dirs(Path.Combine(root, "profiles")))
                {
                    var meta = Load(Path.Combine(dir, "profile.json"))?["metadata"] as JsonObject;
                    var loaderName = Str(meta, "loader");
                    var uid = LoaderUid(loaderName);
                    var version = Str(meta?["loader_version"] as JsonObject, "id");
                    if (meta is null)
                        (uid, version) = (GuessLoaderFromMods(dir), null); // Newer Modrinth app versions keep this in a database.
                    result.Add(new ImportCandidate(launcher, Str(meta, "name") ?? Path.GetFileName(dir), dir, Str(meta, "game_version"), uid, version)
                    {
                        IconPath = File.Exists(Path.Combine(dir, "icon.png")) ? Path.Combine(dir, "icon.png") : null,
                    });
                }
                break;

            case OtherLauncher.GDLauncher:
                foreach (var dir in Dirs(Path.Combine(root, "instances")))
                {
                    if (Load(Path.Combine(dir, "instance.json")) is { } carbon)
                    {
                        var v = carbon["game_configuration"]?["version"] as JsonObject;
                        var ml = (v?["modloaders"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                        result.Add(new ImportCandidate(launcher, Str(carbon, "name") ?? Path.GetFileName(dir),
                            Directory.Exists(Path.Combine(dir, "instance")) ? Path.Combine(dir, "instance") : dir,
                            Str(v, "release"), LoaderUid(Str(ml, "type")), Str(ml, "version")));
                    }
                    else if (Load(Path.Combine(dir, "config.json"))?["loader"] is JsonObject legacy)
                    {
                        var mc = Str(legacy, "mcVersion");
                        var lv = Str(legacy, "loaderVersion");
                        result.Add(new ImportCandidate(launcher, Path.GetFileName(dir), dir, mc, LoaderUid(Str(legacy, "loaderType")),
                            lv is null ? null : VersionProfileInspector.StripMinecraftPrefix(lv)));
                    }
                }
                break;

            case OtherLauncher.ATLauncher:
                foreach (var dir in Dirs(Path.Combine(root, "instances")))
                {
                    var json = Load(Path.Combine(dir, "instance.json"));
                    if (json is null)
                        continue;
                    var l = json["launcher"] as JsonObject;
                    var lv = l?["loaderVersion"] as JsonObject;
                    result.Add(new ImportCandidate(launcher, Str(l, "name") ?? Path.GetFileName(dir), dir, Str(json, "id"), LoaderUid(Str(lv, "type")), Str(lv, "version")));
                }
                break;

            case OtherLauncher.FtbApp:
                foreach (var dir in Dirs(Path.Combine(root, "instances")))
                {
                    var json = Load(Path.Combine(dir, "instance.json"));
                    if (json is null)
                        continue;
                    var (uid, version) = ModpackReader.ParseCurseForgeLoader(Str(json, "modLoader"), Str(json, "mcVersion"));
                    result.Add(new ImportCandidate(launcher, Str(json, "name") ?? Path.GetFileName(dir), dir, Str(json, "mcVersion"), uid, version)
                    {
                        IconPath = File.Exists(Path.Combine(dir, "folder.jpg")) ? Path.Combine(dir, "folder.jpg") : null,
                    });
                }
                break;
        }
        return result;
    }

    /// <summary>Copies a candidate's game folder into a new Fluorite instance.</summary>
    public static async Task<GameInstance> ImportAsync(FluoriteLayout layout, ImportCandidate candidate, string minecraftVersion, string? loaderUid, string? loaderVersion,
        string? name = null, CancellationToken ct = default)
    {
        var manifest = new InstanceManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? candidate.Name : name.Trim(),
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = minecraftVersion }],
        };
        if (loaderUid is not null && loaderVersion is not null)
            manifest.Components.Add(new ComponentRef { Uid = loaderUid, Version = loaderVersion });
        var instance = await layout.CreateAsync(manifest, ct).ConfigureAwait(false);
        await Task.Run(() => FluoriteLayout.CopyDirectory(candidate.GameDirectory, instance.GameDirectory, SkipLauncherFiles), ct).ConfigureAwait(false);
        if (candidate.IconPath is { } icon && File.Exists(icon))
        {
            var target = Path.Combine(instance.Directory, "icon" + Path.GetExtension(icon));
            AtomicFile.Copy(icon, target);
            instance.Manifest.Icon = target;
            await layout.SaveAsync(instance, ct).ConfigureAwait(false);
        }
        return instance;
    }

    /// <summary>
    /// A Fluorite-owned copy of an instance used in place: the game folder is copied and the loader reinstalled into
    /// Fluorite's store (another launcher's loader profile may depend on files only that launcher generated).
    /// Custom version JSONs without a known loader are copied into Fluorite's store as they are.
    /// </summary>
    public static async Task<GameInstance> CopyAsync(FluoriteLayout layout, GameInstance source, string? name = null, CancellationToken ct = default)
    {
        var manifest = new InstanceManifest
        {
            Name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim(),
            Notes = source.Manifest.Notes,
            Group = source.Manifest.Group,
            IconKey = source.Manifest.IconKey,
            Components = source.Manifest.Components.Select(c => new ComponentRef { Uid = c.Uid, Version = c.Version, ProfileId = null }).ToList(),
        };
        InstanceSettingsDiff.Overlay(manifest.Settings, source.Manifest.Settings);
        if (manifest.Components.FirstOrDefault(c => c.Uid == ComponentUids.RawVersion) is { } raw)
            await Task.Run(() => CopyVersionChain(source.Store, layout.Store, raw.Version), ct).ConfigureAwait(false);

        var instance = await layout.CreateAsync(manifest, ct).ConfigureAwait(false);
        await Task.Run(() => FluoriteLayout.CopyDirectory(source.GameDirectory, instance.GameDirectory, rel =>
            SkipLauncherFiles(rel)
            // An isolated HMCL version folder also holds the version JSON/jar and natives.
            || (source.Layout == LayoutKind.Isolated && source.GameDirectory == source.Directory
                && (rel.Equals(source.LocalId + ".json", StringComparison.OrdinalIgnoreCase) || rel.Equals(source.LocalId + ".jar", StringComparison.OrdinalIgnoreCase)
                    || rel.StartsWith("natives", StringComparison.OrdinalIgnoreCase) || rel.Equals(".hmcl", StringComparison.OrdinalIgnoreCase) || rel.Equals("hmclversion.cfg", StringComparison.OrdinalIgnoreCase)))
            // The official launcher's shared .minecraft: never copy the store itself.
            || (source.Layout == LayoutKind.Official && source.GameDirectory == source.Store.Root
                && rel is "versions" or "libraries" or "assets" or "runtime" or "launcher_profiles.json" or "launcher_profiles_microsoft_store.json" or "launcher_accounts.json" or "launcher_accounts_microsoft_store.json" or "webcache2" or "bin")), ct).ConfigureAwait(false);
        if (source.Manifest.Icon is { } icon && File.Exists(icon))
        {
            var target = Path.Combine(instance.Directory, "icon" + Path.GetExtension(icon));
            AtomicFile.Copy(icon, target);
            instance.Manifest.Icon = target;
            await layout.SaveAsync(instance, ct).ConfigureAwait(false);
        }
        return instance;
    }

    private static void CopyVersionChain(GameDirectories from, GameDirectories to, string id)
    {
        for (var depth = 0; depth < 8 && id is { Length: > 0 }; depth++)
        {
            var src = from.VersionDir(id);
            if (!Directory.Exists(src) || Directory.Exists(to.VersionDir(id)))
                return;
            FluoriteLayout.CopyDirectory(src, to.VersionDir(id), rel => rel.StartsWith("natives", StringComparison.OrdinalIgnoreCase));
            id = Load(from.VersionJson(id)) is { } json ? Str(json, "inheritsFrom") ?? "" : "";
        }
    }

    /// <summary>Other launchers' bookkeeping inside the game folder.</summary>
    private static bool SkipLauncherFiles(string rel) =>
        rel is "minecraftinstance.json" or "profile.json" or "instance.json" or "config.json" or ".fluorite" or "natives" or ".ftbapp" or "logs" or "crash-reports"
            or "folder.jpg" or "instance.cfg" or "mmc-pack.json" or "patches" or "jarmods";

    private static string? GuessLoaderFromMods(string gameDir)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(mods))
            return null;
        foreach (var jar in Directory.EnumerateFiles(mods, "*.jar").Take(30))
        {
            try
            {
                var meta = ContentMetadataReader.Read(jar);
                if (meta.Loaders.FirstOrDefault() is var first && meta.Loaders.Any())
                {
                    return first switch
                    {
                        ModLoaderKind.Fabric => ComponentUids.FabricLoader,
                        ModLoaderKind.Quilt => ComponentUids.QuiltLoader,
                        ModLoaderKind.NeoForge => ComponentUids.NeoForge,
                        ModLoaderKind.Forge or ModLoaderKind.LegacyForge => ComponentUids.Forge,
                        _ => null,
                    };
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private static string? LoaderUid(string? name) => name?.ToLowerInvariant() switch
    {
        "forge" => ComponentUids.Forge,
        "neoforge" or "neoforged" => ComponentUids.NeoForge,
        "fabric" => ComponentUids.FabricLoader,
        "quilt" => ComponentUids.QuiltLoader,
        "liteloader" => ComponentUids.LiteLoader,
        _ => null,
    };

    private static string[] Dirs(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.GetDirectories(path) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static JsonObject? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path), documentOptions: s_json) as JsonObject : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Str(JsonObject? o, string key) => ModpackReader.Str(o, key);
}
