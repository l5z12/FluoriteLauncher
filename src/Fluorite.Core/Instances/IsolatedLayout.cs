using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fluorite.Core.Instances;

/// <summary>
/// An HMCL/PCL-style <c>.minecraft</c>: every <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> is an instance, and with "version
/// isolation" its game directory is <c>versions/&lt;id&gt;/</c> itself. Per-version settings are read from HMCL's
/// <c>hmclversion.cfg</c> (and the newer <c>.hmcl/config/instance-game-settings.json</c>); isolation is taken from
/// those files or, failing that, from whether the version folder holds game data. Nothing is written back.
/// </summary>
public sealed class IsolatedLayout(string root, ExternalLayoutContext context) : ExternalLayout(root, context)
{
    public override LayoutKind Kind => LayoutKind.Isolated;

    private GameDirectories Store => new(Root);

    private static readonly string[] s_gameData = ["saves", "mods", "config", "resourcepacks", "shaderpacks", "options.txt", "logs", "screenshots"];

    protected override IEnumerable<NativeInstance> ReadAll()
    {
        foreach (var dir in SafeDirectories(Store.Versions))
        {
            if (Read(Path.GetFileName(dir)) is { } n)
                yield return n;
        }
    }

    protected override NativeInstance? Read(string localId)
    {
        var store = Store;
        var versionDir = store.VersionDir(localId);
        if (!File.Exists(store.VersionJson(localId)))
            return null;

        var settingsJson = LoadSettings(versionDir);
        var gameDir = GameDirectory(versionDir, settingsJson);
        var profile = VersionProfileInspector.Inspect(store, localId);
        var manifest = new InstanceManifest
        {
            Name = localId,
            Icon = File.Exists(Path.Combine(versionDir, "icon.png")) ? Path.Combine(versionDir, "icon.png") : null,
            Created = Directory.GetCreationTime(versionDir),
            Components = VersionProfileInspector.ComponentsFor(profile),
            Settings = settingsJson is null ? new InstanceSettings() : MapSettings(settingsJson),
        };
        var warnings = new List<string>();
        if (profile.MinecraftVersion is null)
            warnings.Add($"Couldn't tell which Minecraft version '{localId}' is based on.");
        return new NativeInstance(localId, versionDir, gameDir, store, manifest, warnings);
    }

    /// <summary>HMCL legacy <c>hmclversion.cfg</c>, else the newer per-instance settings file.</summary>
    private static JsonObject? LoadSettings(string versionDir)
    {
        foreach (var path in new[] { Path.Combine(versionDir, "hmclversion.cfg"), Path.Combine(versionDir, ".hmcl", "config", "instance-game-settings.json") })
        {
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true }) is JsonObject o)
                    return o;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
        }
        return null;
    }

    private string GameDirectory(string versionDir, JsonObject? settings)
    {
        if (settings is not null && !Bool(settings, "usesGlobal"))
        {
            // Legacy: gameDirType 0 = root, 1 = version folder, 2 = custom ("gameDir").
            var type = settings["gameDirType"] switch
            {
                JsonValue v when v.TryGetValue<int>(out var i) => i,
                JsonValue v when v.TryGetValue<string>(out var s) => s switch { "ROOT_FOLDER" => 0, "VERSION_FOLDER" => 1, "CUSTOM" => 2, _ => -1 },
                _ => -1,
            };
            if (type == 2 && Text(settings, "gameDir") is { } custom)
                return Path.GetFullPath(Path.Combine(Root, custom));
            if (type == 1)
                return versionDir;
            if (type == 0)
                return Root;
            if (Text(settings, "runningDirectory") is { } running)
                return Path.GetFullPath(Path.Combine(Root, running));
        }
        // No explicit setting: isolated if the version folder already holds game data.
        return s_gameData.Any(n => Path.Exists(Path.Combine(versionDir, n))) ? versionDir : Root;
    }

    private static InstanceSettings MapSettings(JsonObject s)
    {
        var settings = new InstanceSettings();
        if (Bool(s, "usesGlobal"))
            return settings;
        if (!Bool(s, "autoMemory", defaultValue: true) && Int(s, "maxMemory") is { } max)
            settings.MaxMemoryMb = max;
        settings.MinMemoryMb = Bool(s, "autoMemory", defaultValue: true) ? null : Int(s, "minMemory");
        settings.JvmArguments = Text(s, "javaArgs") ?? Text(s, "jvmOptions");
        settings.GameArguments = Text(s, "minecraftArgs") ?? Text(s, "gameArguments");
        settings.WrapperCommand = Text(s, "wrapper") ?? Text(s, "commandWrapper");
        settings.PreLaunchCommand = Text(s, "precalledCommand") ?? Text(s, "preLaunchCommand");
        settings.PostExitCommand = Text(s, "postExitCommand");
        if (Int(s, "width") is > 0 and var w && Int(s, "height") is > 0 and var h)
        {
            settings.WindowWidth = w;
            settings.WindowHeight = h;
        }
        if (Bool(s, "fullscreen") || Text(s, "windowType") == "FULLSCREEN")
            settings.Fullscreen = true;
        if ((Text(s, "java") == "Custom" || Text(s, "javaType") == "CUSTOM") && (Text(s, "javaDir") ?? Text(s, "customJavaPath")) is { } java)
            settings.JavaPath = java;
        return settings;
    }

    /// <summary>HMCL's newer settings wrap inheritable values as <c>{"value": …}</c>; accept both shapes.</summary>
    private static JsonNode? Value(JsonObject o, string key) => o[key] is JsonObject wrapped && wrapped.ContainsKey("value") ? wrapped["value"] : o[key];

    private static string? Text(JsonObject o, string key) =>
        Value(o, key) is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int? Int(JsonObject o, string key) => Value(o, key) switch
    {
        JsonValue v when v.TryGetValue<int>(out var i) => i,
        JsonValue v when v.TryGetValue<double>(out var d) => (int)d,
        JsonValue v when v.TryGetValue<string>(out var s) && int.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, out var p) => p,
        _ => null,
    };

    private static bool Bool(JsonObject o, string key, bool defaultValue = false) => Value(o, key) switch
    {
        JsonValue v when v.TryGetValue<bool>(out var b) => b,
        JsonValue v when v.TryGetValue<string>(out var s) => bool.TryParse(s, out var p) ? p : defaultValue,
        _ => defaultValue,
    };
}
