using System.IO.Compression;

namespace Fluorite.Modding.Util;

internal static class JarTools
{
    /// <summary>Reads <c>Main-Class</c> from <c>META-INF/MANIFEST.MF</c> (handles 72-byte continuation lines).</summary>
    public static string? ReadMainClass(string jarPath)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        var entry = zip.GetEntry("META-INF/MANIFEST.MF");
        if (entry is null)
            return null;
        using var reader = new StreamReader(entry.Open());
        var text = reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n ", "", StringComparison.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                return line["Main-Class:".Length..].Trim();
        }
        return null;
    }

    public static bool HasEntry(string jarPath, string entryName)
    {
        using var zip = ZipFile.OpenRead(jarPath);
        return zip.GetEntry(entryName) is not null;
    }

    public static string? ReadText(ZipArchive zip, string entryName)
    {
        var entry = zip.GetEntry(entryName.TrimStart('/'));
        if (entry is null)
            return null;
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

    /// <summary>Extracts an entry to <paramref name="target"/> unless an identical-size file is already there.</summary>
    public static void Extract(ZipArchive zip, string entryName, string target)
    {
        var entry = zip.GetEntry(entryName.TrimStart('/'))
            ?? throw new InvalidDataException($"Installer is missing '{entryName}'.");
        var fi = new FileInfo(target);
        if (fi.Exists && fi.Length == entry.Length)
            return;
        Directory.CreateDirectory(fi.DirectoryName!);
        entry.ExtractToFile(target, overwrite: true);
    }
}
