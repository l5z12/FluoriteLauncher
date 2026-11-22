using System.IO.Compression;

namespace Fluorite.Core.Libraries;

public static class NativesExtractor
{
    /// <summary>
    /// Extracts legacy native archives into <paramref name="targetDir"/>, honouring <c>extract.exclude</c>.
    /// Files that already exist with the same size are left untouched (they may be locked by a running game).
    /// </summary>
    public static void Extract(IEnumerable<NativeArchive> archives, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        var root = Path.GetFullPath(targetDir) + Path.DirectorySeparatorChar;

        foreach (var archive in archives)
        {
            using var zip = ZipFile.OpenRead(archive.Path);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Name.Length == 0)
                    continue;
                if (archive.ExcludePrefixes.Any(p => entry.FullName.StartsWith(p, StringComparison.Ordinal)))
                    continue;

                var dest = Path.GetFullPath(Path.Combine(targetDir, entry.FullName));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue; // Zip-slip guard.

                var fi = new FileInfo(dest);
                if (fi.Exists && fi.Length == entry.Length)
                    continue;

                Directory.CreateDirectory(fi.DirectoryName!);
                try
                {
                    entry.ExtractToFile(dest, overwrite: true);
                }
                catch (IOException) when (fi.Exists)
                {
                    // Locked by a running instance; the existing copy is used.
                }
            }
        }
    }
}
