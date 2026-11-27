using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Fluorite.Core.Launch;

/// <summary>
/// Keeps launches under the Windows 32,767-character command-line limit (heavily modded packs exceed it).
/// Java 9+ gets an <c>@argfile</c>; Java 8 gets a "pathing jar" whose manifest <c>Class-Path</c> lists the libraries.
/// </summary>
public static class ClasspathStrategy
{
    /// <summary>
    /// Writes a Java @argfile into <paramref name="directory"/> and returns its path. Each launch gets one of its own:
    /// the arguments hold the game folder and the account's token, so two launches of one version mustn't share a file
    /// one could rewrite before the other's Java has read it. Files left by launches more than a few minutes ago are
    /// deleted first (Java reads its argfile only as it starts). Every argument is quoted; backslashes and quotes are
    /// escaped as Java requires.
    /// </summary>
    public static string WriteArgFile(IEnumerable<string> args, string directory)
    {
        var sb = new StringBuilder();
        foreach (var a in args)
        {
            sb.Append('"');
            foreach (var c in a)
            {
                switch (c)
                {
                    case '\\': sb.Append(@"\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append(@"\n"); break;
                    case '\r': sb.Append(@"\r"); break;
                    case '\t': sb.Append(@"\t"); break;
                    default: sb.Append(c); break;
                }
            }
            sb.Append('"').Append('\n');
        }
        Directory.CreateDirectory(directory);
        foreach (var old in Directory.EnumerateFiles(directory, ArgFilePrefix + "*.txt"))
        {
            if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow - TimeSpan.FromMinutes(10))
                DeleteArgFile(old);
        }
        var path = Path.Combine(directory, $"{ArgFilePrefix}-{Guid.NewGuid().ToString("N")[..8]}.txt");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private const string ArgFilePrefix = "launch-args";

    /// <summary>Deletes an argfile its game no longer needs, if it can (one left is deleted by a later launch).</summary>
    public static void DeleteArgFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Writes a jar containing only a manifest whose <c>Class-Path</c> references <paramref name="entries"/> into
    /// <paramref name="directory"/>, named after what it holds, and returns its path. A running Java 8 game keeps its
    /// jar open, so a jar is never written again: launches with the same classpath share it, one with another gets
    /// its own.
    /// </summary>
    public static string WritePathingJar(IEnumerable<string> entries, string directory)
    {
        var urls = entries.Select(e =>
        {
            var full = Path.GetFullPath(e);
            var uri = new Uri(Directory.Exists(full) ? full.TrimEnd('\\') + "\\" : full).AbsoluteUri;
            return uri;
        });

        var manifest = new StringBuilder();
        AppendManifestLine(manifest, "Manifest-Version: 1.0");
        AppendManifestLine(manifest, "Class-Path: " + string.Join(' ', urls));
        AppendManifestLine(manifest, "Created-By: " + FluoriteInfo.LauncherName);
        manifest.Append("\r\n");

        var text = manifest.ToString();
        var jarPath = Path.Combine(directory, $"classpath-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16]}.jar");
        if (File.Exists(jarPath))
            return jarPath;
        Directory.CreateDirectory(directory);
        try
        {
            AtomicFile.Write(jarPath, tmp =>
            {
                using var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
                var entry = zip.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.Optimal);
                using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                w.Write(text);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(jarPath))
        {
            // Another launch with this classpath made the same jar a moment ago, and its game has it open.
        }
        return jarPath;
    }

    /// <summary>Manifest lines are limited to 72 bytes; continuations start with a single space.</summary>
    private static void AppendManifestLine(StringBuilder sb, string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        var offset = 0;
        var first = true;
        while (offset < bytes.Length)
        {
            var max = first ? 72 : 71;
            var len = Math.Min(max, bytes.Length - offset);
            // Don't split a UTF-8 multi-byte sequence.
            while (len > 0 && offset + len < bytes.Length && (bytes[offset + len] & 0xC0) == 0x80)
                len--;
            if (!first)
                sb.Append(' ');
            sb.Append(Encoding.UTF8.GetString(bytes, offset, len)).Append("\r\n");
            offset += len;
            first = false;
        }
    }
}
