using System.Globalization;

namespace Fluorite.App.Services;

/// <summary>Compact numbers and relative dates for cards and lists.</summary>
public static class Formats
{
    public static string Count(long n)
    {
        var c = CultureInfo.CurrentCulture;
        if (CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return n switch
            {
                >= 100_000_000 => (n / 100_000_000d).ToString("0.#", c) + "亿",
                >= 10_000 => (n / 10_000d).ToString("0.#", c) + "万",
                _ => n.ToString("N0", c),
            };
        }
        return n switch
        {
            >= 1_000_000_000 => (n / 1_000_000_000d).ToString("0.#", c) + "B",
            >= 1_000_000 => (n / 1_000_000d).ToString("0.#", c) + "M",
            >= 1_000 => (n / 1_000d).ToString("0.#", c) + "K",
            _ => n.ToString("N0", c),
        };
    }

    public static string Ago(DateTimeOffset? when)
    {
        if (when is not { } t)
            return "";
        var span = DateTimeOffset.Now - t;
        if (span.TotalHours < 24)
        {
            return span.TotalMinutes switch
            {
                < 1 => Loc.Get("Ago_JustNow"), // Also a clock a little ahead.
                < 2 => Loc.Get("Ago_Minute"),
                < 60 => Loc.Format("Ago_Minutes", (int)span.TotalMinutes),
                < 120 => Loc.Get("Ago_Hour"),
                _ => Loc.Format("Ago_Hours", (int)span.TotalHours),
            };
        }
        return span.TotalDays switch
        {
            < 2 => Loc.Get("Ago_Yesterday"),
            < 30 => Loc.Format("Ago_Days", (int)span.TotalDays),
            < 60 => Loc.Get("Ago_Month"),
            < 365 => Loc.Format("Ago_Months", (int)(span.TotalDays / 30)),
            < 730 => Loc.Get("Ago_Year"),
            _ => Loc.Format("Ago_Years", (int)(span.TotalDays / 365)),
        };
    }

    /// <summary>The exact local date and time, for the tooltip behind a relative one.</summary>
    public static string When(DateTimeOffset? when) =>
        when is { } t ? t.LocalDateTime.ToString("f", CultureInfo.CurrentCulture) : "";

    /// <summary>"3 h 12 min", "45 min"; empty under a minute.</summary>
    public static string Duration(TimeSpan t) =>
        t < TimeSpan.FromMinutes(1) ? ""
        : t.TotalHours >= 1 ? Loc.Format("Duration_HoursMinutes", (int)t.TotalHours, t.Minutes)
        : Loc.Format("Duration_Minutes", t.Minutes);

    /// <summary>"4 GB", "4.5 GB", from megabytes.</summary>
    public static string Gigabytes(double mb) => (mb / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

    private static readonly Dictionary<string, string> s_tagNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fabric"] = "Fabric", ["forge"] = "Forge", ["neoforge"] = "NeoForge", ["quilt"] = "Quilt",
        ["liteloader"] = "LiteLoader", ["legacy-fabric"] = "Legacy Fabric", ["optifine"] = "OptiFine",
        ["datapack"] = "Data Pack", ["minecraft"] = "Minecraft", ["iris"] = "Iris", ["canvas"] = "Canvas",
        ["modloader"] = "ModLoader", ["rift"] = "Rift", ["babric"] = "Babric", ["ornithe"] = "Ornithe",
    };

    private static readonly HashSet<string> s_loaderTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "liteloader", "legacy-fabric", "babric", "ornithe", "rift", "modloader",
        "optifine", "iris", "canvas", "vanilla", "minecraft", "datapack",
    };

    /// <summary>Loader/platform tags (Modrinth mixes them into categories); cards list real categories first.</summary>
    public static bool IsLoaderTag(string tag) => s_loaderTags.Contains(tag);

    /// <summary>
    /// A display name for a category or loader tag. Modrinth hands out lowercase slugs ("game-mechanics", "neoforge");
    /// CurseForge names are already human-readable and pass through.
    /// </summary>
    public static string Tag(string tag)
    {
        if (s_tagNames.TryGetValue(tag, out var known))
            return known;
        if (tag.Any(char.IsUpper))
            return tag;
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(tag.Replace('-', ' '));
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1 << 30 => (bytes / (double)(1 << 30)).ToString("0.0", CultureInfo.CurrentCulture) + " GB",
        >= 1 << 20 => (bytes / (double)(1 << 20)).ToString("0.0", CultureInfo.CurrentCulture) + " MB",
        >= 1 << 10 => (bytes / (double)(1 << 10)).ToString("0", CultureInfo.CurrentCulture) + " KB",
        _ => bytes.ToString(CultureInfo.CurrentCulture) + " B",
    };
}
