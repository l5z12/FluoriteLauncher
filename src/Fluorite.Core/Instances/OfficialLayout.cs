using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fluorite.Core.Instances;

/// <summary>
/// The official launcher's <c>.minecraft</c>: every profile in <c>launcher_profiles.json</c> (or the Microsoft Store
/// launcher's <c>launcher_profiles_microsoft_store.json</c>, whichever is newer) is an instance. Its versions,
/// libraries and assets are used from that folder, so launching from Fluorite looks exactly like launching from the
/// official launcher. The profile file itself is never written.
/// </summary>
public sealed class OfficialLayout(string root, ExternalLayoutContext context) : ExternalLayout(root, context)
{
    public const string LatestRelease = "latest-release";
    public const string LatestSnapshot = "latest-snapshot";

    public override LayoutKind Kind => LayoutKind.Official;

    private static readonly string[] s_profileFiles = ["launcher_profiles_microsoft_store.json", "launcher_profiles.json"];

    private GameDirectories Store => new(Root);

    /// <summary>The profiles file the official launcher currently uses.</summary>
    public string? ProfilesFile =>
        s_profileFiles
            .Select(n => Path.Combine(Root, n))
            .Where(File.Exists)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    protected override IEnumerable<NativeInstance> ReadAll()
    {
        var profiles = ReadProfiles();
        if (profiles is null)
            yield break;
        foreach (var (key, node) in profiles)
        {
            if (node is JsonObject p && Map(key, p) is { } n)
                yield return n;
        }
    }

    protected override NativeInstance? Read(string localId) =>
        ReadProfiles()?[localId] is JsonObject p ? Map(localId, p) : null;

    private JsonObject? ReadProfiles()
    {
        if (ProfilesFile is not { } file)
            return null;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonNode.Parse(fs, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true })?["profiles"] as JsonObject;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private NativeInstance? Map(string key, JsonObject p)
    {
        var type = VersionProfileInspector.Str(p, "type") ?? "custom";
        var versionId = type switch
        {
            LatestRelease => LatestRelease,
            LatestSnapshot => LatestSnapshot,
            _ => VersionProfileInspector.Str(p, "lastVersionId"),
        };
        if (versionId is null)
            return null;

        var warnings = new List<string>();
        List<ComponentRef> components;
        if (versionId is LatestRelease or LatestSnapshot)
        {
            components = ResolveLatest(versionId) is { } latest
                ? [new ComponentRef { Uid = ComponentUids.Minecraft, Version = latest }]
                : [new ComponentRef { Uid = ComponentUids.RawVersion, Version = versionId }];
        }
        else if (File.Exists(Store.VersionJson(versionId)))
        {
            components = VersionProfileInspector.ComponentsFor(VersionProfileInspector.Inspect(Store, versionId));
        }
        else
        {
            // Not installed yet: a vanilla id is fetched on first launch; anything else can't be launched.
            components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = versionId }];
        }

        var gameDir = VersionProfileInspector.Str(p, "gameDir") is { } gd ? Path.GetFullPath(Path.Combine(Root, gd)) : Root;
        var name = VersionProfileInspector.Str(p, "name") ?? (type switch
        {
            LatestRelease => "Latest release",
            LatestSnapshot => "Latest snapshot",
            _ => versionId,
        });
        var settings = new InstanceSettings
        {
            JavaPath = VersionProfileInspector.Str(p, "javaDir"),
            JvmArguments = VersionProfileInspector.Str(p, "javaArgs"),
        };
        if (p["resolution"] is JsonObject res && res["width"] is JsonValue w && w.TryGetValue<int>(out var width) && res["height"] is JsonValue h && h.TryGetValue<int>(out var height))
        {
            settings.WindowWidth = width;
            settings.WindowHeight = height;
        }

        var manifest = new InstanceManifest
        {
            Name = name,
            Icon = WriteIcon(key, VersionProfileInspector.Str(p, "icon")),
            Created = LenientDate(VersionProfileInspector.Str(p, "created")) ?? DateTimeOffset.MinValue,
            Components = components,
            Settings = settings,
            Stats = new InstanceStats { LastPlayed = LenientDate(VersionProfileInspector.Str(p, "lastUsed")) is { Year: > 1971 } used ? used : null },
        };
        return new NativeInstance(key, Root, gameDir, Store, manifest, warnings);
    }

    /// <summary>Resolves <c>latest-release</c>/<c>latest-snapshot</c> from a cached version manifest, if one is around.</summary>
    private string? ResolveLatest(string alias)
    {
        foreach (var path in new[] { Path.Combine(Root, "versions", "version_manifest_v2.json"), Path.Combine(Context.FluoriteStore.Versions, "version_manifest_v2.json") })
        {
            try
            {
                if (File.Exists(path) && JsonNode.Parse(File.ReadAllBytes(path))?["latest"]?[alias == LatestRelease ? "release" : "snapshot"]?.GetValue<string>() is { } id)
                    return id;
            }
            catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
            {
            }
        }
        return null;
    }

    /// <summary>Profile icons are block names or <c>data:image/png;base64,…</c>; the latter is saved to the sidecar.</summary>
    private string? WriteIcon(string key, string? icon)
    {
        const string prefix = "data:image/png;base64,";
        if (icon is null || !icon.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        try
        {
            var dir = SidecarDirectory(key);
            var path = Path.Combine(dir, "icon.png");
            var bytes = Convert.FromBase64String(icon[prefix.Length..]);
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(dir);
                AtomicFile.WriteAllBytes(path, bytes);
            }
            return path;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTimeOffset? LenientDate(string? s) =>
        DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}
