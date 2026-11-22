using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Fluorite.Core.Nbt;

namespace Fluorite.Core.Worlds;

/// <summary>
/// The worlds of one game directory (<c>saves/</c>) and their backups (<c>backups/</c>, the folder Minecraft's own
/// "Make Backup" writes to, so backups made in game and here show up in both places). Methods do file I/O
/// synchronously; call them off the UI thread.
/// </summary>
public sealed partial class WorldStore(string gameDirectory)
{
    /// <summary>Zip comment that marks backups made automatically after playing (only those are pruned).</summary>
    public const string AutomaticBackupComment = "fluorite:auto-backup";

    private const string TimestampFormat = "yyyy-MM-dd_HH-mm-ss";

    public string GameDirectory { get; } = gameDirectory;
    public string SavesDirectory => Path.Combine(GameDirectory, "saves");
    public string BackupsDirectory => Path.Combine(GameDirectory, "backups");

    [GeneratedRegex(@"^(?<stamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})_(?<name>.+?)(?: \(\d+\))?\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupNameRegex();

    /// <summary>Worlds, most recently played first.</summary>
    public IReadOnlyList<WorldInfo> GetWorlds()
    {
        if (!Directory.Exists(SavesDirectory))
            return [];
        return Directory.EnumerateDirectories(SavesDirectory)
            .Where(d => File.Exists(Path.Combine(d, "level.dat")))
            .Select(WorldInfo.Read)
            .OrderByDescending(w => w.LastPlayed ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public WorldInfo? Find(string folderName)
    {
        var dir = Path.Combine(SavesDirectory, folderName);
        return File.Exists(Path.Combine(dir, "level.dat")) ? WorldInfo.Read(dir) : null;
    }

    /// <summary>Worlds saved after <paramref name="since"/> (those a play session touched).</summary>
    public IReadOnlyList<WorldInfo> GetWorldsSavedSince(DateTimeOffset since) =>
        GetWorlds().Where(w => File.GetLastWriteTimeUtc(Path.Combine(w.Path, "level.dat")) > since.UtcDateTime).ToList();

    public static long GetSize(string directory)
    {
        if (!Directory.Exists(directory))
            return 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(directory).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    // ── Backups ───────────────────────────────────────────────────────────

    /// <summary>World backups in <c>backups/</c>, newest first; optionally only those of one world folder.</summary>
    public IReadOnlyList<WorldBackup> GetBackups(string? worldFolder = null)
    {
        if (!Directory.Exists(BackupsDirectory))
            return [];
        var result = new List<WorldBackup>();
        foreach (var file in new DirectoryInfo(BackupsDirectory).EnumerateFiles("*.zip"))
        {
            if (Describe(file) is not { } backup)
                continue;
            if (worldFolder is null || string.Equals(backup.WorldFolder, worldFolder, StringComparison.OrdinalIgnoreCase))
                result.Add(backup);
        }
        return result.OrderByDescending(b => b.Created).ToList();
    }

    private static WorldBackup? Describe(FileInfo file)
    {
        string? root;
        bool automatic;
        try
        {
            using var zip = ZipFile.OpenRead(file.FullName);
            root = FindWorldRoot(zip.Entries.Select(e => e.FullName));
            automatic = zip.Comment == AutomaticBackupComment;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return null;
        }
        if (root is null)
            return null;

        var match = BackupNameRegex().Match(file.Name);
        var created = match.Success && DateTime.TryParseExact(match.Groups["stamp"].Value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var stamp)
            ? new DateTimeOffset(stamp)
            : new DateTimeOffset(file.LastWriteTime);
        // The zip's own top folder is the world folder; a backup with the world at its root goes by its file name.
        var folder = root.Length > 0 ? root.TrimEnd('/').Split('/')[^1]
            : match.Success ? match.Groups["name"].Value
            : Path.GetFileNameWithoutExtension(file.Name);
        return new WorldBackup(file.FullName, folder, created, file.Length) { IsAutomatic = automatic };
    }

    /// <summary>
    /// Zips the world into <c>backups/&lt;yyyy-MM-dd_HH-mm-ss&gt;_&lt;folder&gt;.zip</c> with the world folder at the top,
    /// as Minecraft does. Files the running game holds open are read shared; <c>session.lock</c> is skipped.
    /// </summary>
    public WorldBackup Backup(WorldInfo world, bool automatic = false, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(BackupsDirectory);
        var stamp = DateTime.Now.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var path = AvailablePath(BackupsDirectory, $"{stamp}_{world.FolderName}", ".zip");
        var temp = path + ".partial";
        var root = new DirectoryInfo(world.Path);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var files = root.EnumerateFiles("*", options).Where(f => !IsSessionLock(root, f)).ToList();
        var total = Math.Max(1, files.Sum(f => f.Length));
        long done = 0;
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                if (automatic)
                    zip.Comment = AutomaticBackupComment;
                foreach (var dir in root.EnumerateDirectories("*", options).Where(d => !d.EnumerateFileSystemInfos().Any()))
                    zip.CreateEntry($"{world.FolderName}/{Relative(root, dir.FullName)}/");
                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry($"{world.FolderName}/{Relative(root, file.FullName)}", CompressionLevel.Fastest);
                    entry.LastWriteTime = file.LastWriteTime;
                    using (var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var dst = entry.Open())
                        src.CopyTo(dst);
                    done += file.Length;
                    progress?.Report((double)done / total);
                }
            }
            File.Move(temp, path);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        return new WorldBackup(path, world.FolderName, DateTimeOffset.Now, new FileInfo(path).Length) { IsAutomatic = automatic };
    }

    /// <summary>Deletes all but the newest <paramref name="keep"/> automatic backups of a world. Returns how many went.</summary>
    public int PruneAutomaticBackups(string worldFolder, int keep)
    {
        var removed = 0;
        foreach (var old in GetBackups(worldFolder).Where(b => b.IsAutomatic).Skip(Math.Max(0, keep)))
        {
            if (TryDelete(old.Path))
                removed++;
        }
        return removed;
    }

    /// <summary>Extracts a backup into <c>saves/&lt;targetFolder&gt;</c>, which must not exist yet.</summary>
    public string Restore(WorldBackup backup, string targetFolder, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(backup.Path);
        var target = Path.Combine(SavesDirectory, targetFolder);
        ExtractWorld(zip, target, ct);
        return target;
    }

    /// <summary>
    /// Adds a world from a zip (a backup or a downloaded map, the world at any depth) or a folder. Returns the new
    /// world's folder name, made unique among the existing worlds.
    /// </summary>
    public string Import(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(SavesDirectory);
        if (Directory.Exists(path))
        {
            if (!File.Exists(Path.Combine(path, "level.dat")))
                throw new InvalidDataException("The folder is not a Minecraft world (it has no level.dat).");
            var folder = AvailableName(SavesDirectory, Path.GetFileName(path.TrimEnd('\\', '/')), "");
            CopyDirectory(path, Path.Combine(SavesDirectory, folder), ct);
            return folder;
        }

        using var zip = ZipFile.OpenRead(path);
        var root = FindWorldRoot(zip.Entries.Select(e => e.FullName))
            ?? throw new InvalidDataException("The archive doesn't contain a Minecraft world (no level.dat).");
        var baseName = root.Length > 0 ? root.TrimEnd('/').Split('/')[^1] : Path.GetFileNameWithoutExtension(path);
        var name = AvailableName(SavesDirectory, baseName, "");
        ExtractWorld(zip, Path.Combine(SavesDirectory, name), ct);
        return name;
    }

    /// <summary>Copies a world to a new folder named after <paramref name="newName"/> and sets its in-game name.</summary>
    public string Copy(WorldInfo world, string newName, CancellationToken ct = default)
    {
        var folder = AvailableName(SavesDirectory, newName, "");
        var target = Path.Combine(SavesDirectory, folder);
        CopyDirectory(world.Path, target, ct);
        SetLevelName(target, newName);
        return folder;
    }

    /// <summary>Changes the name shown in game (the folder keeps its name).</summary>
    public static void Rename(WorldInfo world, string newName) => SetLevelName(world.Path, newName);

    private static void SetLevelName(string worldDirectory, string name)
    {
        var levelDat = Path.Combine(worldDirectory, "level.dat");
        var root = NbtFile.Read(levelDat, out var compression);
        var data = root.GetCompound("Data") ?? throw new InvalidDataException("level.dat has no Data compound.");
        data.Set("LevelName", name);
        NbtFile.WriteSafely(levelDat, root, compression == NbtCompression.None ? NbtCompression.GZip : compression);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// The entry prefix of the world in an archive: the folder holding the shallowest <c>level.dat</c> (<c>""</c> when it
    /// is at the root), or null when there is none.
    /// </summary>
    public static string? FindWorldRoot(IEnumerable<string> entryNames)
    {
        string? best = null;
        var bestDepth = int.MaxValue;
        foreach (var raw in entryNames)
        {
            var name = raw.Replace('\\', '/');
            if (name.StartsWith("__MACOSX/", StringComparison.Ordinal))
                continue;
            if (!(name == "level.dat" || name.EndsWith("/level.dat", StringComparison.Ordinal)))
                continue;
            var prefix = name[..^"level.dat".Length];
            var depth = prefix.Count(c => c == '/');
            if (depth < bestDepth)
            {
                best = prefix;
                bestDepth = depth;
            }
        }
        return best;
    }

    private static void ExtractWorld(ZipArchive zip, string target, CancellationToken ct)
    {
        if (Directory.Exists(target) || File.Exists(target))
            throw new IOException($"'{target}' already exists.");
        var root = FindWorldRoot(zip.Entries.Select(e => e.FullName))
            ?? throw new InvalidDataException("The archive doesn't contain a Minecraft world (no level.dat).");
        var parent = Path.GetDirectoryName(Path.GetFullPath(target))!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, $".restoring-{Guid.NewGuid():N}");
        var tempFull = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
        try
        {
            Directory.CreateDirectory(temp);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(root, StringComparison.Ordinal) || name.Length == root.Length)
                    continue;
                var relative = name[root.Length..];
                var dest = Path.GetFullPath(Path.Combine(temp, relative));
                if (!dest.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"The archive entry '{entry.FullName}' points outside the world folder.");
                if (name.EndsWith('/'))
                {
                    Directory.CreateDirectory(dest);
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
            }
            Directory.Move(temp, target);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }
    }

    private static void CopyDirectory(string source, string target, CancellationToken ct)
    {
        var root = new DirectoryInfo(source);
        var temp = target + ".copying";
        try
        {
            Directory.CreateDirectory(temp);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var dir in root.EnumerateDirectories("*", options))
                Directory.CreateDirectory(Path.Combine(temp, Path.GetRelativePath(root.FullName, dir.FullName)));
            foreach (var file in root.EnumerateFiles("*", options))
            {
                ct.ThrowIfCancellationRequested();
                if (IsSessionLock(root, file))
                    continue;
                var dest = Path.Combine(temp, Path.GetRelativePath(root.FullName, file.FullName));
                using var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var dst = new FileStream(dest, FileMode.CreateNew, FileAccess.Write);
                src.CopyTo(dst);
                dst.Close();
                File.SetLastWriteTimeUtc(dest, file.LastWriteTimeUtc);
            }
            Directory.Move(temp, target);
        }
        catch
        {
            TryDeleteDirectory(temp);
            throw;
        }
    }

    private static bool IsSessionLock(DirectoryInfo root, FileInfo file) =>
        string.Equals(file.Name, "session.lock", StringComparison.OrdinalIgnoreCase)
        && string.Equals(file.DirectoryName, root.FullName.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static string Relative(DirectoryInfo root, string path) => Path.GetRelativePath(root.FullName, path).Replace('\\', '/');

    /// <summary>A file or folder name not taken in <paramref name="directory"/>: <c>name</c>, <c>name (1)</c>, <c>name (2)</c>… like Minecraft.</summary>
    public static string AvailableName(string directory, string name, string extension)
    {
        var baseName = SafeFileName(name);
        for (var i = 0; ; i++)
        {
            var candidate = i == 0 ? baseName : $"{baseName} ({i.ToString(CultureInfo.InvariantCulture)})";
            var full = Path.Combine(directory, candidate + extension);
            if (!File.Exists(full) && !Directory.Exists(full))
                return candidate;
        }
    }

    private static string AvailablePath(string directory, string name, string extension) =>
        Path.Combine(directory, AvailableName(directory, name, extension) + extension);

    /// <summary>Makes a world name usable as a Windows folder name.</summary>
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd('.', ' ');
        if (safe.Length == 0)
            safe = "World";
        if (ReservedNameRegex().IsMatch(safe))
            safe = "_" + safe + "_";
        return safe.Length > 120 ? safe[..120] : safe;
    }

    [GeneratedRegex(@"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\..*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex ReservedNameRegex();

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
