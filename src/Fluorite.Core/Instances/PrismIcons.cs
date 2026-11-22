namespace Fluorite.Core.Instances;

/// <summary>
/// Prism Launcher / MultiMC instance icons (<c>iconKey</c> in <c>instance.cfg</c>). A custom icon is a file in the
/// launcher's icons folder (<c>IconsDir</c>, default <c>icons</c>) or one subfolder down, named after its key; the
/// others are compiled into Prism and have no file anywhere.
/// </summary>
public static class PrismIcons
{
    /// <summary>The types Prism accepts, in the order Fluorite prefers them when one key has several files.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".svg", ".png", ".webp", ".jpg", ".jpeg", ".gif", ".ico"];

    /// <summary>Prism's built-in instance icons (current set and MultiMC's older "_legacy" one).</summary>
    public static IReadOnlySet<string> BuiltIn { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "bee", "bee_legacy", "brick", "brick_legacy", "chicken", "chicken_legacy", "creeper", "creeper_legacy",
        "diamond", "diamond_legacy", "dirt", "dirt_legacy", "enderman", "enderman_legacy", "enderpearl", "enderpearl_legacy",
        "fabricmc", "flame", "flame_legacy", "forge", "fox", "fox_legacy", "ftb_glow", "ftb_logo", "ftb_logo_legacy",
        "gear", "gear_legacy", "gold", "gold_legacy", "grass", "grass_legacy", "herobrine", "herobrine_legacy",
        "infinity_legacy", "iron", "iron_legacy", "liteloader", "magitech", "magitech_legacy", "meat", "meat_legacy",
        "modrinth", "neoforged", "netherstar", "netherstar_legacy", "planks", "planks_legacy", "prismlauncher", "quiltmc",
        "skeleton", "skeleton_legacy", "squarecreeper", "squarecreeper_legacy", "steve", "steve_legacy", "stone",
        "stone_legacy", "tnt", "tnt_legacy",
    };

    /// <summary>"default" (and an empty key) mean the launcher's generic icon.</summary>
    public static bool IsDefault(string? key) => string.IsNullOrEmpty(key) || key == "default";

    /// <summary>The file for <paramref name="key"/> in an icons folder (its top level first, then one subfolder down), or null.</summary>
    public static string? Find(string iconsDirectory, string? key)
    {
        if (IsDefault(key) || !Directory.Exists(iconsDirectory))
            return null;
        try
        {
            return Best(Directory.EnumerateFiles(iconsDirectory), key!)
                ?? Best(Directory.EnumerateDirectories(iconsDirectory).SelectMany(Directory.EnumerateFiles), key!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> is the icon for <paramref name="key"/>: named after the key (any accepted
    /// type), or the key already includes the extension. Keys are case-sensitive, as in Prism.
    /// </summary>
    public static bool Matches(string fileName, string key) =>
        IsIconFile(fileName)
        && (string.Equals(Path.GetFileNameWithoutExtension(fileName), key, StringComparison.Ordinal) || string.Equals(fileName, key, StringComparison.Ordinal));

    public static bool IsIconFile(string fileName) => Rank(fileName) < Extensions.Count;

    /// <summary>The best of several candidate files (by <see cref="Extensions"/> order).</summary>
    public static string? Best(IEnumerable<string> files, string key) =>
        files.Where(f => Matches(Path.GetFileName(f), key)).OrderBy(Rank).FirstOrDefault();

    private static int Rank(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        for (var i = 0; i < Extensions.Count; i++)
        {
            if (string.Equals(Extensions[i], ext, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return Extensions.Count;
    }
}
