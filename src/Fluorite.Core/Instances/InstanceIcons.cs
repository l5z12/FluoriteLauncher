using System.Security.Cryptography;

namespace Fluorite.Core.Instances;

/// <summary>
/// Icon files Fluorite keeps for instances. A picked image is copied in as <c>icon-&lt;hash&gt;.&lt;ext&gt;</c>, named by
/// its content, so a new icon never reuses the file name of the one it replaces (image caches key on the path).
/// </summary>
public static class InstanceIcons
{
    /// <summary>The image types an icon can be: Prism's, so one picked in Fluorite works there too.</summary>
    public static IReadOnlyList<string> Extensions => PrismIcons.Extensions;

    /// <summary>Bigger than any icon needs to be; a mistake (a photo, a screenshot) more likely than a choice.</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    public static bool IsImage(string path) => PrismIcons.IsIconFile(Path.GetFileName(path));

    /// <summary>Copies <paramref name="source"/> into <paramref name="directory"/> and returns the copy's path.</summary>
    public static string Import(string source, string directory)
    {
        var bytes = File.ReadAllBytes(source);
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"icon-{Convert.ToHexStringLower(SHA256.HashData(bytes))[..8]}{Path.GetExtension(source).ToLowerInvariant()}");
        if (!File.Exists(path))
            AtomicFile.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>
    /// Deletes an icon file Fluorite made in <paramref name="directory"/> once nothing uses it: its <c>icon-*</c>
    /// copies, and with <paramref name="includeIconFiles"/> also <c>icon.png</c>/<c>icon.jpg</c> (a modpack's icon in a
    /// Fluorite instance; in a sidecar folder that name is the official launcher's own icon, re-made on every read).
    /// </summary>
    public static void DeleteUnused(string? path, string directory, bool includeIconFiles)
    {
        if (path is null || !IsImage(path)
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return;
        var name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("icon-", StringComparison.OrdinalIgnoreCase) && !(includeIconFiles && name.Equals("icon", StringComparison.OrdinalIgnoreCase)))
            return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still open somewhere; it's only a small file left behind.
        }
    }
}
