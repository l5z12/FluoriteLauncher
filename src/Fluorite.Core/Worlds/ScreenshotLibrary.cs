namespace Fluorite.Core.Worlds;

public sealed record Screenshot(string Path, DateTimeOffset Taken, long Size)
{
    public string FileName => System.IO.Path.GetFileName(Path);
}

/// <summary>The images in a game directory's <c>screenshots/</c> folder (F2 in game), newest first.</summary>
public static class ScreenshotLibrary
{
    private static readonly string[] s_extensions = [".png", ".jpg", ".jpeg", ".webp"];

    public static string DirectoryOf(string gameDirectory) => Path.Combine(gameDirectory, "screenshots");

    public static IReadOnlyList<Screenshot> List(string gameDirectory)
    {
        var dir = DirectoryOf(gameDirectory);
        if (!Directory.Exists(dir))
            return [];
        return new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => s_extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase))
            .Select(f => new Screenshot(f.FullName, new DateTimeOffset(f.LastWriteTime), f.Length))
            .OrderByDescending(s => s.Taken)
            .ToList();
    }
}
