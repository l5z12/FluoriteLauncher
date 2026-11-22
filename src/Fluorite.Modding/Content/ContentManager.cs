using Fluorite.Modding.Metadata;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Content;

/// <summary>A content file (or pack folder) found in an instance.</summary>
public sealed class ContentFile
{
    /// <summary>Current full path; ends in <c>.disabled</c> when disabled.</summary>
    public required string Path { get; init; }

    /// <summary>Index key: path relative to the game directory, forward slashes, without <c>.disabled</c>.</summary>
    public required string Key { get; init; }

    public required ContentLocation Location { get; init; }
    public required ContentMetadata Metadata { get; init; }
    public required ContentRecord Record { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime ModifiedUtc { get; init; }

    public bool Enabled => !ContentLocation.IsDisabled(Path);

    /// <summary>File name without <c>.disabled</c>.</summary>
    public string FileName => System.IO.Path.GetFileName(ContentLocation.StripDisabled(Path));

    public string DisplayName =>
        Record.Title ?? Metadata.Name ?? (IsDirectory ? FileName : System.IO.Path.GetFileNameWithoutExtension(FileName));

    public string? Version => Record.Source?.VersionNumber is { } remote && Metadata.Version is null ? remote : Metadata.Version ?? Record.Source?.VersionNumber;

    public ContentHashes? Hashes =>
        Record is { Sha1: { } sha1, Sha512: { } sha512, Murmur2: { } m } ? new ContentHashes(sha1, sha512, m) : null;
}

/// <summary>
/// Content of one instance: scans its folders (hashing with a cache in <c>.fluorite/content.json</c>), toggles,
/// deletes and imports files. Thread-safe; one instance per game directory (see <see cref="ContentService"/>).
/// </summary>
public sealed class ContentManager : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ContentIndex? _index;

    /// <summary>
    /// Parsed jar/pack metadata by path, reused while size and time are unchanged. Scans run often (each visit to a
    /// tab, after identifying, after installs) and parsing opens every jar and the jars nested in it.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Size, DateTime Modified, ContentMetadata Metadata)> _metadata =
        new(StringComparer.OrdinalIgnoreCase);

    private ContentMetadata MetadataFor(FileSystemInfo info, long size, DateTime modified)
    {
        if (info is DirectoryInfo)
            return ContentMetadataReader.Read(info.FullName); // A folder's time doesn't follow edits inside it.
        if (_metadata.TryGetValue(info.FullName, out var cached) && cached.Size == size && cached.Modified == modified)
            return cached.Metadata;
        var metadata = ContentMetadataReader.Read(info.FullName);
        _metadata[info.FullName] = (size, modified, metadata);
        return metadata;
    }

    public ContentManager(string gameDirectory, string metadataDirectory)
    {
        GameDirectory = gameDirectory;
        IndexPath = System.IO.Path.Combine(metadataDirectory, "content.json");
    }

    public string GameDirectory { get; }
    public string IndexPath { get; }

    public static string KeyFor(ContentLocation location, string fileName) => $"{location.RelativeFolder}/{ContentLocation.StripDisabled(fileName)}";

    /// <summary>Worlds that can hold data packs (folders under <c>saves/</c> with a <c>level.dat</c>), newest first.</summary>
    public IReadOnlyList<string> GetWorlds()
    {
        var saves = System.IO.Path.Combine(GameDirectory, "saves");
        if (!Directory.Exists(saves))
            return [];
        return new DirectoryInfo(saves).EnumerateDirectories()
            .Where(d => File.Exists(System.IO.Path.Combine(d.FullName, "level.dat")))
            .OrderByDescending(d => d.LastWriteTimeUtc)
            .Select(d => d.Name)
            .ToList();
    }

    public async Task<IReadOnlyList<ContentFile>> ScanAsync(ContentLocation location, CancellationToken ct = default)
    {
        var folder = location.FullPath(GameDirectory);
        var entries = new List<FileSystemInfo>();
        if (Directory.Exists(folder))
        {
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                if (info.Name.StartsWith('.'))
                    continue;
                if (info is DirectoryInfo ? location.AcceptsDirectories : location.Accepts(info.Name))
                    entries.Add(info);
            }
        }

        // Hash and parse outside the lock (I/O heavy), then merge into the index.
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        Dictionary<string, ContentRecord> snapshot;
        try
        {
            var index = await LoadIndexAsync(ct).ConfigureAwait(false);
            snapshot = new Dictionary<string, ContentRecord>(index.Files, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.Release();
        }

        var results = new ContentFile[entries.Count];
        var changed = new System.Collections.Concurrent.ConcurrentDictionary<string, ContentRecord>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Count), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 }, async (i, token) =>
        {
            var info = entries[i];
            var key = KeyFor(location, info.Name);
            var isDir = info is DirectoryInfo;
            var size = isDir ? 0 : ((FileInfo)info).Length;
            var modified = info.LastWriteTimeUtc;
            var record = snapshot.TryGetValue(key, out var existing) ? existing : new ContentRecord();

            if (!isDir && (record.Sha1 is null || record.Sha512 is null || record.Murmur2 is null || record.Size != size || record.ModifiedUtc != modified))
            {
                var contentChanged = record.Sha1 is not null && (record.Size != size || record.ModifiedUtc != modified);
                try
                {
                    var hashes = await ContentHashes.ComputeAsync(info.FullName, token).ConfigureAwait(false);
                    if (contentChanged && hashes.Sha1 != record.Sha1)
                    {
                        // Replaced by something else under the same name: forget what it was.
                        record = new ContentRecord { Pinned = record.Pinned };
                    }
                    record.Sha1 = hashes.Sha1;
                    record.Sha512 = hashes.Sha512;
                    record.Murmur2 = hashes.Murmur2;
                    record.Size = size;
                    record.ModifiedUtc = modified;
                    changed[key] = record;
                }
                catch (IOException)
                {
                    // Locked by the running game; hash it next time.
                }
            }
            else if (existing is null)
            {
                changed[key] = record;
            }

            results[i] = new ContentFile
            {
                Path = info.FullName,
                Key = key,
                Location = location,
                IsDirectory = isDir,
                Size = size,
                ModifiedUtc = modified,
                Record = record,
                Metadata = MetadataFor(info, size, modified),
            };
        }).ConfigureAwait(false);

        await UpdateAsync(index =>
        {
            foreach (var (key, record) in changed)
                index.Files[key] = record;
            // Drop entries for files that disappeared from this folder.
            var prefix = location.RelativeFolder + "/";
            var present = results.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var stale = index.Files.Keys
                .Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !k[prefix.Length..].Contains('/', StringComparison.Ordinal) && !present.Contains(k))
                .ToList();
            foreach (var k in stale)
                index.Files.Remove(k);
            return !changed.IsEmpty || stale.Count > 0;
        }, ct).ConfigureAwait(false);

        return results.OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public async Task<ContentFile> SetEnabledAsync(ContentFile file, bool enabled, CancellationToken ct = default)
    {
        if (file.Enabled == enabled)
            return file;
        var target = enabled ? ContentLocation.StripDisabled(file.Path) : file.Path + ContentLocation.DisabledSuffix;
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (file.IsDirectory)
                Directory.Move(file.Path, target);
            else
                File.Move(file.Path, target, overwrite: false);
        }
        finally
        {
            _lock.Release();
        }
        // Renaming keeps the timestamp, and the key ignores ".disabled", so the record still applies.
        return new ContentFile
        {
            Path = target,
            Key = file.Key,
            Location = file.Location,
            IsDirectory = file.IsDirectory,
            Size = file.Size,
            ModifiedUtc = file.ModifiedUtc,
            Record = file.Record,
            Metadata = file.Metadata,
        };
    }

    /// <param name="delete">Deletes a path (e.g. to the Recycle Bin); defaults to a permanent delete.</param>
    public async Task DeleteAsync(ContentFile file, Action<string>? delete = null, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (delete is not null)
                delete(file.Path);
            else if (file.IsDirectory)
                Directory.Delete(file.Path, recursive: true);
            else
                File.Delete(file.Path);
        }
        finally
        {
            _lock.Release();
        }
        await UpdateAsync(index => index.Files.Remove(file.Key), ct).ConfigureAwait(false);
    }

    /// <summary>The index file was put back from a snapshot: read it again next time instead of the one in memory.</summary>
    public async Task ReloadIndexAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _index = null;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// After files that were moved away come back (an undone removal): their records (source project, pin…), which
    /// the scan dropped while they were gone.
    /// </summary>
    public Task RestoreRecordsAsync(IEnumerable<ContentFile> files, CancellationToken ct = default) =>
        UpdateAsync(index =>
        {
            foreach (var file in files)
                index.Files[file.Key] = file.Record;
            return true;
        }, ct);

    // ── Editing a mod's manifest ──────────────────────────────────────────

    /// <summary>Jars as they were before Fluorite edited their manifest, named by that hash.</summary>
    public string OriginalsDirectory => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(IndexPath)!, "mod-originals");

    /// <summary>The kept original of a jar whose manifest was edited, if it's still there.</summary>
    public string? OriginalOf(ContentFile file) =>
        file.Record.OriginalSha1 is { } sha1 && System.IO.Path.Combine(OriginalsDirectory, sha1 + ".jar") is var path && File.Exists(path) ? path : null;

    /// <summary>
    /// Writes an edited manifest into the jar (see <see cref="ModManifest.Write"/>), keeping the original to put back.
    /// What Fluorite knows about the file stays (its project, so updates still come), under its new hash. Returns
    /// true when the jar's signature had to go.
    /// </summary>
    public async Task<bool> EditManifestAsync(ContentFile file, string entryName, string text, CancellationToken ct = default)
    {
        var original = file.Record.OriginalSha1 ?? file.Record.Sha1 ?? (await ContentHashes.ComputeAsync(file.Path, ct).ConfigureAwait(false)).Sha1;
        var unsigned = await Task.Run(() => ModManifest.Write(file.Path, entryName, text, System.IO.Path.Combine(OriginalsDirectory, original + ".jar")), ct).ConfigureAwait(false);
        await RecordRewriteAsync(file, original, ct).ConfigureAwait(false);
        return unsigned;
    }

    /// <summary>Puts back the jar as it was before its manifest was edited.</summary>
    public async Task RestoreOriginalAsync(ContentFile file, CancellationToken ct = default)
    {
        if (OriginalOf(file) is not { } original)
            throw new FileNotFoundException("The original file isn't kept any more.");
        var temp = file.Path + ".fluorite-restore";
        File.Copy(original, temp, overwrite: true);
        File.Move(temp, file.Path, overwrite: true);
        await RecordRewriteAsync(file, null, ct).ConfigureAwait(false);
        File.Delete(original);
    }

    /// <summary>The file changed on purpose: new hashes, same record (a scan would otherwise take it for another file).</summary>
    private async Task RecordRewriteAsync(ContentFile file, string? originalSha1, CancellationToken ct)
    {
        var hashes = await ContentHashes.ComputeAsync(file.Path, ct).ConfigureAwait(false);
        var info = new FileInfo(file.Path);
        await UpdateAsync(index =>
        {
            var record = index.Files.GetValueOrDefault(file.Key) ?? file.Record;
            record.Sha1 = hashes.Sha1;
            record.Sha512 = hashes.Sha512;
            record.Murmur2 = hashes.Murmur2;
            record.Size = info.Length;
            record.ModifiedUtc = info.LastWriteTimeUtc;
            record.OriginalSha1 = originalSha1;
            index.Files[file.Key] = record;
            return true;
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Copies a local file into the location (replacing a same-named file) and returns its new path.</summary>
    public async Task<string> ImportAsync(string sourcePath, ContentLocation location, CancellationToken ct = default)
    {
        var folder = location.FullPath(GameDirectory);
        Directory.CreateDirectory(folder);
        var target = System.IO.Path.Combine(folder, System.IO.Path.GetFileName(sourcePath));
        if (string.Equals(System.IO.Path.GetFullPath(sourcePath), System.IO.Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
            return target;
        var tmp = target + ".part";
        await using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
        await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        File.Move(tmp, target, overwrite: true);
        // A previously disabled copy of the same file would load twice once re-enabled; drop it.
        if (File.Exists(target + ContentLocation.DisabledSuffix))
            File.Delete(target + ContentLocation.DisabledSuffix);
        await UpdateAsync(index => index.Files.Remove(KeyFor(location, System.IO.Path.GetFileName(target))), ct).ConfigureAwait(false);
        return target;
    }

    public async Task<ContentRecord?> GetRecordAsync(string key, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return (await LoadIndexAsync(ct).ConfigureAwait(false)).Files.GetValueOrDefault(key);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>All records (a copy), e.g. to find what's installed from a project.</summary>
    public async Task<IReadOnlyDictionary<string, ContentRecord>> GetRecordsAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return new Dictionary<string, ContentRecord>((await LoadIndexAsync(ct).ConfigureAwait(false)).Files, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Mutates the index under the lock and saves it when <paramref name="update"/> returns true.</summary>
    public async Task UpdateAsync(Func<ContentIndex, bool> update, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var index = await LoadIndexAsync(ct).ConfigureAwait(false);
            if (update(index))
                await index.SaveAsync(IndexPath, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<ContentIndex> LoadIndexAsync(CancellationToken ct) =>
        _index ??= await ContentIndex.LoadAsync(IndexPath, ct).ConfigureAwait(false);

    public void Dispose() => _lock.Dispose();
}
