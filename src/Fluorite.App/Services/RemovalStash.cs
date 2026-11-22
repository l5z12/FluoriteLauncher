using System.Globalization;
using Fluorite.Platform.Windows.Shell;
using Microsoft.Extensions.Logging;

namespace Fluorite.App.Services;

/// <summary>
/// What was just removed (mods, worlds, screenshots…) waits in a hidden folder of the game directory, where neither
/// the game nor Fluorite's lists see it, so Undo can put it back. When the chance to undo has passed, each item goes
/// back to where it was for an instant and from there to the Recycle Bin, so the bin's Restore puts it back too.
/// </summary>
public sealed class RemovalStash
{
    private const string FolderName = ".fluorite-removed";
    private const string OriginsFile = "origins.txt";

    /// <summary>Stashes of this run that are still waiting (not leftovers of an earlier run).</summary>
    private static readonly HashSet<string> s_waiting = new(StringComparer.OrdinalIgnoreCase);

    private static ILogger Logger => App.Get<ILogger<RemovalStash>>();

    private readonly string _dir;
    private readonly List<(string Original, string Stashed)> _items;

    private RemovalStash(string dir, List<(string Original, string Stashed)> items)
    {
        _dir = dir;
        _items = items;
    }

    /// <summary>
    /// Moves the paths out of the way, all or none: on failure the ones already moved go back and it throws.
    /// The list of where each came from is written first, so even a crash midway loses nothing.
    /// </summary>
    public static RemovalStash Create(string gameDirectory, IEnumerable<string> paths)
    {
        var root = Path.Combine(gameDirectory, FolderName);
        var dir = Path.Combine(root, DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8]);
        var originals = paths.Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p))).ToList();
        var items = originals.Select((p, i) => (p, Path.Combine(dir, i.ToString(CultureInfo.InvariantCulture), Path.GetFileName(p)))).ToList();
        Directory.CreateDirectory(dir);
        TryHide(root);
        File.WriteAllLines(Path.Combine(dir, OriginsFile), originals);
        lock (s_waiting)
            s_waiting.Add(dir);
        var moved = new List<(string Original, string Stashed)>();
        try
        {
            foreach (var item in items)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(item.Item2)!);
                Move(item.Item1, item.Item2);
                moved.Add(item);
            }
        }
        catch
        {
            foreach (var (original, stashed) in moved)
                Move(stashed, original);
            Forget(dir);
            throw;
        }
        return new RemovalStash(dir, moved);
    }

    /// <summary>Puts everything back. One whose place was taken meanwhile (the same name added again) stays removed.</summary>
    public void Restore()
    {
        foreach (var (original, stashed) in _items)
        {
            if (!Exists(original) && Exists(stashed))
                Move(stashed, original);
        }
        Commit(); // Whatever couldn't go back.
    }

    /// <summary>Final: to the Recycle Bin, from where each item was (or from here when that place is taken).</summary>
    public void Commit()
    {
        foreach (var (original, stashed) in _items)
        {
            if (!Exists(stashed))
                continue;
            try
            {
                if (!Exists(original))
                {
                    Move(stashed, original);
                    RecycleBin.Send(original);
                }
                else
                {
                    RecycleBin.Send(stashed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                Logger.LogWarning(ex, "Could not finish removing {Path}", original);
            }
        }
        Forget(_dir);
    }

    /// <summary>
    /// Finishes removals an earlier run left waiting (it closed or crashed before they were final); this run's own
    /// stashes are left alone.
    /// </summary>
    public static void CommitLeftovers(string gameDirectory)
    {
        var root = Path.Combine(gameDirectory, FolderName);
        if (!Directory.Exists(root))
            return;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                lock (s_waiting)
                {
                    if (s_waiting.Contains(dir))
                        continue;
                }
                var origins = Path.Combine(dir, OriginsFile);
                var originals = File.Exists(origins) ? File.ReadAllLines(origins) : [];
                var items = originals.Select((p, i) => (p, Path.Combine(dir, i.ToString(CultureInfo.InvariantCulture), Path.GetFileName(p)))).ToList();
                new RemovalStash(dir, items).Commit();
            }
            if (!Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning(ex, "Could not finish earlier removals in {Root}", root);
        }
    }

    private static void Forget(string dir)
    {
        lock (s_waiting)
            s_waiting.Remove(dir);
        try
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
            var root = Path.GetDirectoryName(dir)!;
            if (Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);

    private static void Move(string from, string to)
    {
        if (Directory.Exists(from))
            Directory.Move(from, to);
        else
            File.Move(from, to);
    }

    private static void TryHide(string dir)
    {
        try
        {
            var info = new DirectoryInfo(dir);
            info.Attributes |= FileAttributes.Hidden;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
