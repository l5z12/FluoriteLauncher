using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fluorite.Core.Instances;

/// <summary>
/// A Prism Launcher / MultiMC data folder: <c>instances/&lt;name&gt;/instance.cfg</c> + <c>mmc-pack.json</c>, game
/// files in <c>minecraft/</c> or <c>.minecraft/</c>. Components map 1:1 onto Fluorite's (same uids); versions,
/// libraries and assets come from Fluorite's own store, so nothing is written to the Prism folder except what the
/// game writes — unless write-back is allowed, in which case settings, name, icon, versions and play time are saved to
/// <c>instance.cfg</c>/<c>mmc-pack.json</c> (and a picked image to its icons folder) the way Prism does.
/// </summary>
public sealed class MultiMCLayout(string root, ExternalLayoutContext context) : ExternalLayout(root, context)
{
    public override LayoutKind Kind => LayoutKind.MultiMC;
    protected override bool SupportsWriteBack => true;

    private static readonly HashSet<string> s_implied = new(StringComparer.Ordinal)
    {
        ComponentUids.Lwjgl2, ComponentUids.Lwjgl3, "net.fabricmc.intermediary", "org.quiltmc.hashed",
        "net.legacyfabric.intermediary", "net.legacyfabric.lwjgl",
    };

    private static readonly HashSet<string> s_supported = new(StringComparer.Ordinal)
    {
        ComponentUids.Minecraft, ComponentUids.FabricLoader, ComponentUids.QuiltLoader, ComponentUids.Forge,
        ComponentUids.NeoForge, ComponentUids.LiteLoader,
    };

    /// <summary>The folder holding instance folders (<c>InstanceDir</c> in prismlauncher.cfg/multimc.cfg, default <c>instances</c>).</summary>
    public string InstancesDirectory
    {
        get
        {
            if (LauncherSetting("InstanceDir") is { } dir)
                return Path.GetFullPath(Path.Combine(Root, dir));
            var instances = Path.Combine(Root, "instances");
            // The user may also have picked the instances folder itself.
            return Directory.Exists(instances) || !SafeDirectories(Root).Any(d => File.Exists(Path.Combine(d, "instance.cfg"))) ? instances : Root;
        }
    }

    /// <summary>The custom icons folder (<c>IconsDir</c>, default <c>icons</c>; relative to the data folder or absolute).</summary>
    public string IconsDirectory => Path.GetFullPath(Path.Combine(Root, LauncherSetting("IconsDir") ?? "icons"));

    /// <summary>A setting from the launcher's own config (Prism, MultiMC or PolyMC), or null when unset.</summary>
    private string? LauncherSetting(string key)
    {
        foreach (var cfg in new[] { "prismlauncher.cfg", "multimc.cfg", "polymc.cfg" })
        {
            var path = Path.Combine(Root, cfg);
            try
            {
                if (File.Exists(path))
                    return IniFile.Load(path).Get(key) is { Length: > 0 } value ? value : null;
            }
            catch (IOException)
            {
            }
        }
        return null;
    }

    protected override IEnumerable<NativeInstance> ReadAll()
    {
        var dir = InstancesDirectory;
        var groups = ReadGroups(dir);
        var icons = IconsDirectory;
        foreach (var folder in SafeDirectories(dir))
        {
            if (Read(Path.GetFileName(folder), groups, icons) is { } n)
                yield return n;
        }
    }

    protected override NativeInstance? Read(string localId) => Read(localId, ReadGroups(InstancesDirectory), IconsDirectory);

    private NativeInstance? Read(string localId, Dictionary<string, string> groups, string iconsDirectory)
    {
        var dir = Path.Combine(InstancesDirectory, localId);
        var cfgPath = Path.Combine(dir, "instance.cfg");
        if (!File.Exists(cfgPath))
            return null;
        IniFile cfg;
        try
        {
            cfg = IniFile.Load(cfgPath);
        }
        catch (IOException)
        {
            return null;
        }
        var type = cfg.Get("InstanceType");
        if (type is not null && type != "OneSix")
            return null; // MultiMC "Legacy" instances predate component stacks.

        var warnings = new List<string>();
        var iconKey = cfg.Get("iconKey");
        var iconFile = PrismIcons.Find(iconsDirectory, iconKey);
        var manifest = new InstanceManifest
        {
            Name = cfg.Get("name") is { Length: > 0 } name ? name : localId,
            Notes = cfg.Get("notes") is { Length: > 0 } notes ? notes : null,
            Group = groups.GetValueOrDefault(localId),
            Icon = iconFile,
            IconKey = iconFile is null && iconKey is not null && PrismIcons.BuiltIn.Contains(iconKey) ? iconKey : null,
            Created = Directory.GetCreationTime(dir),
            Components = ReadComponents(dir, warnings),
            Settings = ReadSettings(cfg),
            Stats = new InstanceStats
            {
                LastPlayed = FromUnixMs(cfg.GetLong("lastLaunchTime")),
                TotalPlaySeconds = cfg.GetLong("totalTimePlayed") ?? 0,
                LastSessionSeconds = cfg.GetLong("lastTimePlayed") ?? 0,
            },
        };
        return new NativeInstance(localId, dir, GameRoot(dir), Context.FluoriteStore, manifest, warnings);
    }

    /// <summary>Prism's rule: <c>minecraft/</c>, unless only <c>.minecraft/</c> exists.</summary>
    public static string GameRoot(string instanceDir)
    {
        var mc = Path.Combine(instanceDir, "minecraft");
        var dot = Path.Combine(instanceDir, ".minecraft");
        return Directory.Exists(dot) && !Directory.Exists(mc) ? dot : mc;
    }

    private static List<ComponentRef> ReadComponents(string dir, List<string> warnings)
    {
        var result = new List<ComponentRef>();
        var packPath = Path.Combine(dir, "mmc-pack.json");
        if (!File.Exists(packPath))
        {
            warnings.Add("mmc-pack.json is missing.");
            return result;
        }
        try
        {
            result = ParseComponents(File.ReadAllBytes(packPath), uid => File.Exists(Path.Combine(dir, "patches", uid + ".json")), warnings);
        }
        catch (JsonException ex)
        {
            warnings.Add($"mmc-pack.json is not valid JSON: {ex.Message}");
            return result;
        }
        if (Directory.Exists(Path.Combine(dir, "jarmods")) && Directory.EnumerateFileSystemEntries(Path.Combine(dir, "jarmods")).Any())
            warnings.Add("Jar mods aren't supported yet; they are not applied when Fluorite launches this instance.");
        return result;
    }

    /// <summary>Maps <c>mmc-pack.json</c> components onto Fluorite's (implied mappings/LWJGL dropped, unknown ones reported).</summary>
    public static List<ComponentRef> ParseComponents(byte[] mmcPackJson, Func<string, bool> hasPatch, List<string> warnings)
    {
        var result = new List<ComponentRef>();
        var components = JsonNode.Parse(mmcPackJson)?["components"] as JsonArray;
        var uids = components?.OfType<JsonObject>().Select(c => VersionProfileInspector.Str(c, "uid")).OfType<string>().ToHashSet() ?? [];
        var legacyFabric = uids.Contains("net.legacyfabric.intermediary");
        foreach (var c in components?.OfType<JsonObject>() ?? [])
        {
            var uid = VersionProfileInspector.Str(c, "uid");
            var version = VersionProfileInspector.Str(c, "version") ?? VersionProfileInspector.Str(c, "cachedVersion");
            if (uid is null || s_implied.Contains(uid))
                continue;
            if (!s_supported.Contains(uid) || version is null)
            {
                warnings.Add($"Component '{VersionProfileInspector.Str(c, "cachedName") ?? uid}' isn't supported by Fluorite and is ignored.");
                continue;
            }
            if (hasPatch(uid))
                warnings.Add($"'{uid}' is customised in patches/{uid}.json; Fluorite uses the standard version instead.");
            result.Add(new ComponentRef { Uid = uid == ComponentUids.FabricLoader && legacyFabric ? ComponentUids.LegacyFabric : uid, Version = version });
        }
        return result;
    }

    /// <summary>Per-instance overrides from <c>instance.cfg</c> (only those Prism marks as overridden).</summary>
    public static InstanceSettings ReadSettings(IniFile cfg)
    {
        var s = new InstanceSettings();
        if (cfg.GetBool("OverrideJavaLocation") && !cfg.GetBool("AutomaticJava") && cfg.Get("JavaPath") is { Length: > 0 } java)
            s.JavaPath = java.Replace('/', Path.DirectorySeparatorChar);
        if (cfg.GetBool("OverrideMemory"))
        {
            s.MaxMemoryMb = (int?)cfg.GetLong("MaxMemAlloc");
            s.MinMemoryMb = (int?)cfg.GetLong("MinMemAlloc");
        }
        if (cfg.GetBool("OverrideJavaArgs") && cfg.Get("JvmArgs") is { Length: > 0 } jvm)
            s.JvmArguments = jvm;
        if (cfg.GetBool("OverrideWindow"))
        {
            s.WindowWidth = (int?)cfg.GetLong("MinecraftWinWidth");
            s.WindowHeight = (int?)cfg.GetLong("MinecraftWinHeight");
            if (cfg.GetBool("LaunchMaximized"))
                s.Fullscreen = true;
        }
        if (cfg.GetBool("OverrideCommands"))
        {
            s.PreLaunchCommand = NullIfEmpty(cfg.Get("PreLaunchCommand"));
            s.WrapperCommand = NullIfEmpty(cfg.Get("WrapperCommand"));
            s.PostExitCommand = NullIfEmpty(cfg.Get("PostExitCommand"));
        }
        return s;
    }

    private static Dictionary<string, string> ReadGroups(string instancesDir)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(instancesDir, "instgroups.json");
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path))?["groups"] is JsonObject groups)
            {
                foreach (var (name, node) in groups)
                {
                    foreach (var inst in node?["instances"] as JsonArray ?? [])
                    {
                        if (inst?.GetValue<string>() is { } id)
                            result[id] = name;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
        }
        return result;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    // ── Write-back ────────────────────────────────────────────────────────

    protected override void WriteBack(GameInstance instance, NativeInstance native)
    {
        var dir = native.Directory;
        var cfgPath = Path.Combine(dir, "instance.cfg");
        var cfg = IniFile.Load(cfgPath);
        var m = instance.Manifest;
        var s = m.Settings;

        cfg.Set("name", m.Name);
        cfg.Set("notes", m.Notes ?? "");
        if (!string.Equals(m.Icon, native.Manifest.Icon, StringComparison.OrdinalIgnoreCase) || m.IconKey != native.Manifest.IconKey)
        {
            if (m.Icon is { } file && File.Exists(file))
            {
                var key = AddCustomIcon(file);
                cfg.Set("iconKey", key);
                m.Icon = PrismIcons.Find(IconsDirectory, key) ?? m.Icon; // Fluorite's copy is now unused.
            }
            else
            {
                cfg.Set("iconKey", m.IconKey ?? "default");
            }
        }
        if (s.JavaPath != native.Manifest.Settings.JavaPath)
        {
            cfg.Set("OverrideJavaLocation", s.JavaPath is null ? "false" : "true");
            cfg.Set("AutomaticJava", s.JavaPath is null ? "true" : "false");
            if (s.JavaPath is not null)
                cfg.Set("JavaPath", s.JavaPath.Replace('\\', '/'));
        }
        if (s.MaxMemoryMb != native.Manifest.Settings.MaxMemoryMb || s.MinMemoryMb != native.Manifest.Settings.MinMemoryMb)
        {
            cfg.Set("OverrideMemory", s.MaxMemoryMb is null && s.MinMemoryMb is null ? "false" : "true");
            if (s.MaxMemoryMb is { } max)
                cfg.Set("MaxMemAlloc", max.ToString(CultureInfo.InvariantCulture));
            if (s.MinMemoryMb is { } min)
                cfg.Set("MinMemAlloc", min.ToString(CultureInfo.InvariantCulture));
        }
        if (s.JvmArguments != native.Manifest.Settings.JvmArguments)
        {
            cfg.Set("OverrideJavaArgs", s.JvmArguments is null ? "false" : "true");
            cfg.Set("JvmArgs", s.JvmArguments ?? "");
        }
        var stats = m.Stats;
        if (stats.LastPlayed is { } lp)
            cfg.Set("lastLaunchTime", lp.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        cfg.Set("totalTimePlayed", stats.TotalPlaySeconds.ToString(CultureInfo.InvariantCulture));
        cfg.Set("lastTimePlayed", stats.LastSessionSeconds.ToString(CultureInfo.InvariantCulture));
        cfg.Save(cfgPath);

        WriteComponents(dir, m.Components);
    }

    /// <summary>
    /// Puts an image into Prism's icons folder, where its custom icons live (named after their key), and returns the
    /// key. Named by content (<c>fluorite-&lt;hash&gt;</c>), so the same image is added once and none of Prism's are
    /// overwritten. Replaced icons stay there, as they do in Prism, to be picked again.
    /// </summary>
    private string AddCustomIcon(string file)
    {
        var icons = IconsDirectory;
        if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(file)), icons, StringComparison.OrdinalIgnoreCase))
            return Path.GetFileNameWithoutExtension(file);
        var bytes = File.ReadAllBytes(file);
        var key = "fluorite-" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes))[..8];
        Directory.CreateDirectory(icons);
        var target = Path.Combine(icons, key + Path.GetExtension(file).ToLowerInvariant());
        if (!File.Exists(target))
            File.WriteAllBytes(target, bytes);
        return key;
    }

    /// <summary>Updates versions in <c>mmc-pack.json</c>, adding/removing the loader; leaves everything else untouched.</summary>
    private static void WriteComponents(string dir, List<ComponentRef> components)
    {
        var path = Path.Combine(dir, "mmc-pack.json");
        var root = (File.Exists(path) ? JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject : null) ?? new JsonObject { ["formatVersion"] = 1 };
        var list = root["components"] as JsonArray ?? [];
        root["components"] = list;

        var wanted = components.Where(c => s_supported.Contains(c.Uid) || c.Uid == ComponentUids.LegacyFabric)
            .ToDictionary(c => c.Uid == ComponentUids.LegacyFabric ? ComponentUids.FabricLoader : c.Uid, c => c.Version);
        var mc = wanted.GetValueOrDefault(ComponentUids.Minecraft);
        foreach (var node in list.OfType<JsonObject>().ToList())
        {
            var uid = VersionProfileInspector.Str(node, "uid");
            // Mappings follow the Minecraft version; Prism re-resolves the rest of the dependency-only components.
            if (mc is not null && uid is "net.fabricmc.intermediary" or "org.quiltmc.hashed" or "net.legacyfabric.intermediary"
                && VersionProfileInspector.Str(node, "version") != mc)
            {
                node["version"] = mc;
                node.Remove("cachedVersion");
                node.Remove("cachedRequires");
            }
            if (uid is null || !s_supported.Contains(uid))
                continue;
            if (wanted.Remove(uid, out var version))
            {
                if (VersionProfileInspector.Str(node, "version") != version)
                {
                    node["version"] = version;
                    node.Remove("cachedVersion");
                    node.Remove("cachedRequires");
                }
            }
            else
            {
                list.Remove(node); // Loader removed in Fluorite.
            }
        }
        foreach (var (uid, version) in wanted)
            list.Add((JsonNode)new JsonObject { ["uid"] = uid, ["version"] = version, ["important"] = uid == ComponentUids.Minecraft });

        var tmp = path + ".fluorite-tmp";
        File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 }));
        File.Move(tmp, path, overwrite: true);
    }
}
