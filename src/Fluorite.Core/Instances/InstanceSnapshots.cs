using System.Globalization;
using System.Text.Json;
using Fluorite.Core.Json;

namespace Fluorite.Core.Instances;

/// <summary>What a snapshot was taken of, written last (a folder without it is unfinished).</summary>
public sealed class SnapshotManifest
{
    public int FormatVersion { get; set; } = 1;
    public DateTimeOffset Created { get; set; }

    /// <summary>The user's note ("Before shaders"), or why it was taken automatically ("Before updating 3 mods").</summary>
    public string? Label { get; set; }

    /// <summary>Taken by Fluorite (before an update or a restore): only the newest few are kept.</summary>
    public bool Automatic { get; set; }

    /// <summary>The Minecraft and loader setup then.</summary>
    public List<ComponentRef> Components { get; set; } = [];

    /// <summary>The included paths that existed (relative to the game directory).</summary>
    public List<string> Included { get; set; } = [];

    /// <summary>Other files saved with it (absolute paths), e.g. what Fluorite knew about the mods.</summary>
    public List<string> Extra { get; set; } = [];

    public int ModCount { get; set; }
}

public sealed record InstanceSnapshot(string Directory, SnapshotManifest Manifest);

/// <summary>
/// Snapshots of an instance's mods, configs, resource and shader packs and loader setup, to go back to after an
/// update gone wrong. Worlds aren't in them (they have their own backups). Jars and zips are hard-linked when the
/// file system allows (instant, no extra space; updates replace them, never edit them in place); the rest is copied.
/// </summary>
public sealed class InstanceSnapshots(string root, Func<string, string, bool>? tryHardLink = null)
{
    /// <summary>What a snapshot holds, relative to the game directory.</summary>
    public static readonly IReadOnlyList<string> IncludedPaths =
        ["mods", "config", "defaultconfigs", "kubejs", "scripts", "resourcepacks", "shaderpacks", "options.txt"];

    private const string ManifestFile = "snapshot.json";
    private const string FilesFolder = "files";
    private const string ExtraFolder = "extra";

    public string DirectoryFor(GameInstance instance) => Path.Combine(root, Safe(instance.Id));

    /// <summary>Newest first; unfinished ones (no manifest yet) are left out.</summary>
    public IReadOnlyList<InstanceSnapshot> List(GameInstance instance)
    {
        var dir = DirectoryFor(instance);
        if (!Directory.Exists(dir))
            return [];
        var list = new List<InstanceSnapshot>();
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            var file = Path.Combine(folder, ManifestFile);
            if (Path.GetFileName(folder).StartsWith('.') || !File.Exists(file))
                continue;
            try
            {
                if (JsonSerializer.Deserialize(File.ReadAllText(file), FluoriteJsonContext.Default.SnapshotManifest) is { } manifest)
                    list.Add(new InstanceSnapshot(folder, manifest));
            }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
            }
        }
        return list.OrderByDescending(s => s.Manifest.Created).ToList();
    }

    /// <param name="extraFiles">Files outside the game directory to keep with it (put back on restore).</param>
    public async Task<InstanceSnapshot> CreateAsync(GameInstance instance, string? label, bool automatic, IReadOnlyList<string>? extraFiles = null, CancellationToken ct = default)
    {
        var created = DateTimeOffset.Now;
        var dir = Path.Combine(DirectoryFor(instance), created.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        try
        {
            var manifest = await Task.Run(() =>
            {
                var m = new SnapshotManifest { Created = created, Label = label, Automatic = automatic, Components = Clone(instance.Manifest.Components) };
                var files = Path.Combine(dir, FilesFolder);
                Directory.CreateDirectory(files);
                foreach (var relative in IncludedPaths)
                {
                    var source = Path.Combine(instance.GameDirectory, relative);
                    if (IsLink(source))
                        continue; // A folder shared from elsewhere isn't the instance's to snapshot (or restore).
                    if (Directory.Exists(source))
                        CopyTree(source, Path.Combine(files, relative), ct);
                    else if (File.Exists(source))
                        CopyFile(source, Path.Combine(files, relative));
                    else
                        continue;
                    m.Included.Add(relative);
                }
                var mods = Path.Combine(instance.GameDirectory, "mods");
                m.ModCount = Directory.Exists(mods) ? Directory.EnumerateFiles(mods, "*.jar").Count() : 0;
                for (var i = 0; i < (extraFiles?.Count ?? 0); i++)
                {
                    var extra = extraFiles![i];
                    if (!File.Exists(extra))
                        continue;
                    Directory.CreateDirectory(Path.Combine(dir, ExtraFolder));
                    File.Copy(extra, Path.Combine(dir, ExtraFolder, i.ToString(CultureInfo.InvariantCulture)));
                    m.Extra.Add(extra);
                }
                return m;
            }, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Combine(dir, ManifestFile), JsonSerializer.Serialize(manifest, FluoriteJsonContext.Default.SnapshotManifest), ct).ConfigureAwait(false);
            return new InstanceSnapshot(dir, manifest);
        }
        catch
        {
            TryDelete(dir);
            throw;
        }
    }

    /// <summary>
    /// Puts the snapshot's files in place of the current ones (only what snapshots hold: worlds, screenshots… stay
    /// as they are) and returns its loader setup for the caller to apply. Take a snapshot first to make it undoable.
    /// </summary>
    public Task<List<ComponentRef>> RestoreAsync(GameInstance instance, InstanceSnapshot snapshot, CancellationToken ct = default) => Task.Run(() =>
    {
        var files = Path.Combine(snapshot.Directory, FilesFolder);
        foreach (var relative in IncludedPaths)
        {
            var target = Path.Combine(instance.GameDirectory, relative);
            if (IsLink(target))
                continue;
            if (Directory.Exists(target))
                DeleteTree(target);
            else if (File.Exists(target))
                File.Delete(target);
            var saved = Path.Combine(files, relative);
            if (Directory.Exists(saved))
                CopyTree(saved, target, ct);
            else if (File.Exists(saved))
                CopyFile(saved, target);
        }
        for (var i = 0; i < snapshot.Manifest.Extra.Count; i++)
        {
            var saved = Path.Combine(snapshot.Directory, ExtraFolder, i.ToString(CultureInfo.InvariantCulture));
            var target = snapshot.Manifest.Extra[i];
            if (File.Exists(saved) && Path.GetDirectoryName(target) is { } parent && Directory.Exists(parent))
                File.Copy(saved, target, overwrite: true);
        }
        return Clone(snapshot.Manifest.Components);
    }, ct);

    /// <summary>Keeps the newest <paramref name="keep"/> automatic snapshots (the user's own stay); drops unfinished ones.</summary>
    public void Prune(GameInstance instance, int keep)
    {
        foreach (var old in List(instance).Where(s => s.Manifest.Automatic).Skip(keep))
            TryDelete(old.Directory);
        var dir = DirectoryFor(instance);
        if (!Directory.Exists(dir))
            return;
        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            // Unfinished (no manifest) and older than a minute: a snapshot that failed or was interrupted.
            if (!Path.GetFileName(folder).StartsWith('.') && !File.Exists(Path.Combine(folder, ManifestFile))
                && Directory.GetCreationTimeUtc(folder) < DateTime.UtcNow.AddMinutes(-1))
                TryDelete(folder);
        }
    }

    private void CopyTree(string source, string target, CancellationToken ct)
    {
        Directory.CreateDirectory(target);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            ct.ThrowIfCancellationRequested();
            // Links out of the folder (a shared mods folder…) aren't followed into: their target isn't the instance's.
            if (new DirectoryInfo(dir).LinkTarget is not null)
                continue;
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)), ct);
        }
        foreach (var file in Directory.EnumerateFiles(source))
        {
            ct.ThrowIfCancellationRequested();
            CopyFile(file, Path.Combine(target, Path.GetFileName(file)));
        }
    }

    /// <summary>Jars and zips are linked when they can be (they're replaced, never changed in place); the rest copied.</summary>
    private void CopyFile(string source, string target)
    {
        var ext = Path.GetExtension(source);
        var linkable = ext.Equals(".jar", StringComparison.OrdinalIgnoreCase) || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".disabled", StringComparison.OrdinalIgnoreCase);
        if (linkable && tryHardLink is not null && tryHardLink(target, source))
            return;
        File.Copy(source, target, overwrite: true);
    }

    private static bool IsLink(string path) => Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is not null;

    /// <summary>Deletes a folder, read-only files too.</summary>
    private static void DeleteTree(string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var attributes = File.GetAttributes(file);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(dir, recursive: true);
    }

    private static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir))
                DeleteTree(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static List<ComponentRef> Clone(List<ComponentRef> components) =>
        JsonSerializer.Deserialize(JsonSerializer.Serialize(components, FluoriteJsonContext.Default.ListComponentRef), FluoriteJsonContext.Default.ListComponentRef) ?? [];

    private static string Safe(string id) => string.Concat(id.Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == ':' ? '_' : c));
}
