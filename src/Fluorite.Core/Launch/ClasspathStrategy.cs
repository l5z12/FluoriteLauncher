using System.IO.Compression;
using System.Text;

namespace Fluorite.Core.Launch;

/// <summary>
/// Keeps launches under the Windows 32,767-character command-line limit (heavily modded packs exceed it).
/// Java 9+ gets an <c>@argfile</c>; Java 8 gets a "pathing jar" whose manifest <c>Class-Path</c> lists the libraries.
/// </summary>
public static class ClasspathStrategy
{
    /// <summary>Writes a Java @argfile. Every argument is quoted; backslashes and quotes are escaped as Java requires.</summary>
    public static string WriteArgFile(IEnumerable<string> args, string path)
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    /// <summary>Creates a jar containing only a manifest whose <c>Class-Path</c> references <paramref name="entries"/>.</summary>
    public static string WritePathingJar(IEnumerable<string> entries, string jarPath)
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

        Directory.CreateDirectory(Path.GetDirectoryName(jarPath)!);
        using var fs = new FileStream(jarPath, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("META-INF/MANIFEST.MF", CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(manifest.ToString());
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
