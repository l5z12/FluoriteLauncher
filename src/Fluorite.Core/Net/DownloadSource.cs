namespace Fluorite.Core.Net;

public enum DownloadSourceKind
{
    /// <summary>Official upstream hosts first, BMCLAPI as fallback.</summary>
    Official,

    /// <summary>BMCLAPI mirror first, official as fallback.</summary>
    Bmclapi,

    /// <summary>Order decided by a speed test (see <see cref="MirrorTable.Prefer"/>).</summary>
    Auto,
}

/// <summary>
/// Rewrites upstream URLs to mirror URLs and yields candidate URLs in preference order.
/// Every download carries all candidates so a 404/hash mismatch on one source falls back to the next.
/// </summary>
public sealed class MirrorTable
{
    private const string Bmcl = "https://bmclapi2.bangbang93.com";

    // Prefix rewrites (upstream → BMCLAPI). Order matters: longer/more specific prefixes first.
    private static readonly (string From, string To)[] s_bmclapi =
    [
        ("https://piston-meta.mojang.com", Bmcl),
        ("https://piston-data.mojang.com", Bmcl),
        ("https://launchermeta.mojang.com", Bmcl),
        ("https://launcher.mojang.com", Bmcl),
        ("https://libraries.minecraft.net", Bmcl + "/maven"),
        ("https://resources.download.minecraft.net", Bmcl + "/assets"),
        ("https://files.minecraftforge.net/maven", Bmcl + "/maven"),
        ("https://maven.minecraftforge.net", Bmcl + "/maven"),
        ("https://maven.neoforged.net/releases", Bmcl + "/maven"),
        ("https://maven.fabricmc.net", Bmcl + "/maven"),
        ("https://meta.fabricmc.net", Bmcl + "/fabric-meta"),
        ("https://maven.quiltmc.org/repository/release", Bmcl + "/maven"),
        ("https://authlib-injector.yushi.moe", Bmcl + "/mirrors/authlib-injector"),

        // Content platforms via MCIM (mod.mcimirror.top), the community mirror of Modrinth/CurseForge for users in China.
        ("https://api.modrinth.com", Mcim + "/modrinth"),
        ("https://cdn.modrinth.com", Mcim),
        ("https://api.curseforge.com", Mcim + "/curseforge"),
        ("https://edge.forgecdn.net", Mcim),
        ("https://mediafilez.forgecdn.net", Mcim),
    ];

    private const string Mcim = "https://mod.mcimirror.top";

    // Dead or moved upstream hosts that should always be rewritten, regardless of source.
    private static readonly (string From, string To)[] s_canonical =
    [
        ("http://files.minecraftforge.net/maven", "https://maven.minecraftforge.net"),
        ("https://files.minecraftforge.net/maven", "https://maven.minecraftforge.net"),
        ("http://", "https://"),
    ];

    public MirrorTable(DownloadSourceKind kind) => Kind = kind;

    public DownloadSourceKind Kind { get; }

    /// <summary>For <see cref="DownloadSourceKind.Auto"/>: whether the mirror won the speed test.</summary>
    public bool Prefer { get; set; }

    public static MirrorTable Official { get; } = new(DownloadSourceKind.Official);

    public IReadOnlyList<Uri> Candidates(string url)
    {
        var canonical = Canonicalize(url);
        var mirrored = ToBmclapi(canonical);
        var original = new Uri(canonical);
        if (mirrored is null)
            return [original];

        var mirrorFirst = Kind == DownloadSourceKind.Bmclapi || (Kind == DownloadSourceKind.Auto && Prefer);
        return mirrorFirst ? [mirrored, original] : [original, mirrored];
    }

    public static string Canonicalize(string url)
    {
        foreach (var (from, to) in s_canonical)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
            {
                // Only upgrade http→https for hosts known to support it.
                if (from == "http://" && !IsKnownHttpsHost(url))
                    continue;
                return to + url[from.Length..];
            }
        }
        return url;
    }

    private static bool IsKnownHttpsHost(string url) =>
        url.Contains("minecraft.net", StringComparison.OrdinalIgnoreCase)
        || url.Contains("mojang.com", StringComparison.OrdinalIgnoreCase)
        || url.Contains("minecraftforge.net", StringComparison.OrdinalIgnoreCase)
        || url.Contains("fabricmc.net", StringComparison.OrdinalIgnoreCase);

    public static Uri? ToBmclapi(string url)
    {
        foreach (var (from, to) in s_bmclapi)
        {
            if (url.StartsWith(from, StringComparison.OrdinalIgnoreCase))
                return new Uri(to + url[from.Length..]);
        }
        return null;
    }
}
