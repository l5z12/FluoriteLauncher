using System.Text;
using System.Text.Json;
using Fluorite.Core.Json;

namespace Fluorite.Core.Instances;

/// <summary>
/// Fluorite's native layout: an official-compatible shared store at the root plus
/// <c>instances/&lt;id&gt;/instance.json</c> with the game directory at <c>instances/&lt;id&gt;/.minecraft</c>.
/// </summary>
public sealed class FluoriteLayout(string root) : IGameLayout
{
    public const string ManifestFileName = "instance.json";

    public LayoutKind Kind => LayoutKind.Fluorite;
    public string Root { get; } = Path.GetFullPath(root);
    public GameDirectories Store { get; } = new(root);
    public string InstancesDir => Path.Combine(Root, "instances");

    public async Task<IReadOnlyList<GameInstance>> GetInstancesAsync(CancellationToken ct = default)
    {
        var result = new List<GameInstance>();
        if (!Directory.Exists(InstancesDir))
            return result;

        foreach (var dir in Directory.EnumerateDirectories(InstancesDir))
        {
            var manifestPath = Path.Combine(dir, ManifestFileName);
            if (!File.Exists(manifestPath))
                continue;
            try
            {
                await using var fs = File.OpenRead(manifestPath);
                var manifest = await JsonSerializer.DeserializeAsync(fs, FluoriteJsonContext.Default.InstanceManifest, ct).ConfigureAwait(false);
                if (manifest is not null)
                    result.Add(ToInstance(dir, manifest));
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException)
            {
                // A corrupt manifest shouldn't hide every other instance; surfaced by diagnostics later.
            }
        }
        return result;
    }

    public async Task<GameInstance?> GetAsync(string id, CancellationToken ct = default) =>
        (await GetInstancesAsync(ct).ConfigureAwait(false)).FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task<GameInstance> CreateAsync(InstanceManifest manifest, CancellationToken ct = default)
    {
        Directory.CreateDirectory(InstancesDir);
        var id = UniqueFolderName(InstancesDir, manifest.Name);
        var dir = Path.Combine(InstancesDir, id);
        Directory.CreateDirectory(Path.Combine(dir, ".minecraft"));
        var instance = ToInstance(dir, manifest);
        await SaveAsync(instance, ct).ConfigureAwait(false);
        return instance;
    }

    /// <summary>Copies an instance (game directory included) under a new name. Natives are not copied (re-extracted on launch).</summary>
    public async Task<GameInstance> DuplicateAsync(GameInstance source, string newName, CancellationToken ct = default)
    {
        var id = UniqueFolderName(InstancesDir, newName);
        var dir = Path.Combine(InstancesDir, id);
        await Task.Run(() => CopyDirectory(source.Directory, dir, skip: IsNatives), ct).ConfigureAwait(false);

        var json = JsonSerializer.Serialize(source.Manifest, FluoriteJsonContext.Default.InstanceManifest);
        var manifest = JsonSerializer.Deserialize(json, FluoriteJsonContext.Default.InstanceManifest)!;
        manifest.Name = newName;
        manifest.Created = DateTimeOffset.Now;
        manifest.Stats = new InstanceStats();
        manifest.Hidden = false; // A copy is made to be used: it shows.
        // Its own copy of the icon file, not the original's (which a later icon change there deletes).
        if (manifest.Icon is { } icon && Path.GetRelativePath(source.Directory, icon) is var rel && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel))
            manifest.Icon = Path.Combine(dir, rel);
        var copy = ToInstance(dir, manifest);
        await SaveAsync(copy, ct).ConfigureAwait(false);
        return copy;
    }

    /// <summary>Natives are re-extracted on launch: <c>natives/</c> (old location) and <c>.fluorite/natives/</c>.</summary>
    private static bool IsNatives(string relative) =>
        relative.Equals("natives", StringComparison.OrdinalIgnoreCase)
        || relative.Equals(Path.Combine(".fluorite", "natives"), StringComparison.OrdinalIgnoreCase);

    /// <summary>Copies a directory tree; <paramref name="skip"/> gets paths relative to <paramref name="source"/>.</summary>
    public static void CopyDirectory(string source, string target, Func<string, bool>? skip = null, string relative = "")
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            var rel = Path.Combine(relative, Path.GetFileName(file));
            if (skip?.Invoke(rel) != true)
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            var rel = Path.Combine(relative, name);
            if (skip?.Invoke(rel) != true && !new DirectoryInfo(dir).Attributes.HasFlag(FileAttributes.ReparsePoint))
                CopyDirectory(dir, Path.Combine(target, name), skip, rel);
        }
    }

    public async Task SaveAsync(GameInstance instance, CancellationToken ct = default)
    {
        var path = Path.Combine(instance.Directory, ManifestFileName);
        // The manifest as it is now (it may change while this writes), through a temp file no other save shares.
        var json = JsonSerializer.SerializeToUtf8Bytes(instance.Manifest, FluoriteJsonContext.Default.InstanceManifest);
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllBytesAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            File.Delete(tmp); // Only there when the write or move failed.
        }
    }

    private GameInstance ToInstance(string dir, InstanceManifest manifest) => new()
    {
        Id = Path.GetFileName(dir),
        Layout = LayoutKind.Fluorite,
        Directory = dir,
        GameDirectory = Path.Combine(dir, ".minecraft"),
        Store = Store,
        Manifest = manifest,
        LaunchVersionId = ComponentResolver.LaunchVersionId(manifest),
        MetadataDirectory = Path.Combine(dir, ".fluorite"),
    };

    /// <summary>
    /// Folder-safe, short, ASCII-preferring id. Non-ASCII names are kept readable in <see cref="InstanceManifest.Name"/>
    /// but the folder avoids characters that break old Forge/LaunchWrapper.
    /// </summary>
    public static string UniqueFolderName(string parent, string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name.Trim())
        {
            if (c < 128 && (char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
                sb.Append(c);
            else if (c == ' ' && sb.Length > 0 && sb[^1] != '-')
                sb.Append('-');
        }
        var baseName = sb.ToString().Trim('-', '.');
        if (baseName.Length == 0)
            baseName = "instance";
        if (baseName.Length > 40)
            baseName = baseName[..40];

        var candidate = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(parent, candidate)); i++)
            candidate = $"{baseName}-{i}";
        return candidate;
    }
}
