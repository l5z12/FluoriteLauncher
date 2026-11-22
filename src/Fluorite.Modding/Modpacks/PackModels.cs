using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Modpacks;

public enum PackFormat
{
    Modrinth,
    CurseForge,
    MultiMC,
    Packwiz,
    Technic,
    Ftb,
}

/// <summary>Whether the client needs a file (Modrinth <c>env.client</c>, packwiz <c>side</c>, FTB <c>serveronly</c>).</summary>
public enum PackSide
{
    Required,
    Optional,
    Unsupported,
}

/// <summary>A file a pack downloads into the game directory.</summary>
/// <param name="Path">Relative to the game directory, <c>/</c>-separated, already validated.</param>
public sealed record PackFile(string Path)
{
    public IReadOnlyList<string> Urls { get; init; } = [];
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }

    /// <summary>Other hashes some formats use (packwiz: sha256/md5/murmur2), as <c>(algorithm, hex)</c>.</summary>
    public (string Algorithm, string Value)? OtherHash { get; init; }

    public long? Size { get; init; }
    public PackSide Client { get; init; } = PackSide.Required;

    /// <summary>Identity on Modrinth/CurseForge, when the format says (CurseForge, FTB, packwiz).</summary>
    public RemoteRef? Source { get; init; }

    /// <summary>For CurseForge files that can't be downloaded by launchers: the page to download it from by hand.</summary>
    public string? ManualDownloadUrl { get; init; }

    /// <summary>CurseForge "world" files are zips extracted into <c>saves/</c>.</summary>
    public bool ExtractToSaves { get; init; }

    public string? Title { get; init; }

    public bool IsBlocked => Urls.Count == 0 && ManualDownloadUrl is not null;
}

/// <summary>A file copied from the pack archive (<c>overrides/</c> etc.) into the game directory.</summary>
public sealed record PackOverride(string Entry, string Path);

/// <summary>Where a managed pack comes from, for updates.</summary>
public sealed record PackSource(PackFormat Format, string? ProjectId, string? VersionId, string? VersionName)
{
    /// <summary>packwiz <c>pack.toml</c> URL, Technic slug, FTB pack id.</summary>
    public string? Url { get; init; }
}

/// <summary>A modpack, normalised from any supported format.</summary>
public sealed class PackManifest
{
    public required PackFormat Format { get; init; }
    public required string Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Summary { get; set; }

    public required string MinecraftVersion { get; set; }
    public string? LoaderUid { get; set; }
    public string? LoaderVersion { get; set; }

    public List<PackFile> Files { get; } = [];
    public List<PackOverride> Overrides { get; } = [];

    /// <summary>The archive <see cref="Overrides"/> are read from.</summary>
    public string? ArchivePath { get; set; }

    /// <summary>Settings carried by the pack (Prism <c>instance.cfg</c>, CurseForge <c>recommendedRam</c>).</summary>
    public InstanceSettings Settings { get; } = new();

    public string? IconUrl { get; set; }

    /// <summary>Icon inside the archive (Prism zips, Technic).</summary>
    public string? IconEntry { get; set; }

    /// <summary>A Prism built-in icon named by a Prism zip that carries no icon file (see <see cref="InstanceManifest.IconKey"/>).</summary>
    public string? IconKey { get; set; }

    public PackSource? Source { get; set; }

    /// <summary>Things that won't come across exactly.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Files still waiting for resolution (CurseForge <c>manifest.json</c> lists only project/file ids).</summary>
    public List<(long ProjectId, long FileId, bool Required)> UnresolvedCurseForge { get; } = [];

    public List<ComponentRef> Components()
    {
        var list = new List<ComponentRef> { new() { Uid = ComponentUids.Minecraft, Version = MinecraftVersion } };
        if (LoaderUid is not null && LoaderVersion is not null)
            list.Add(new ComponentRef { Uid = LoaderUid, Version = LoaderVersion });
        return list;
    }
}

public static class PackPaths
{
    /// <summary>
    /// Normalises a pack-relative path and rejects anything that could escape the game directory (absolute paths,
    /// drive letters, <c>..</c>, device names). Returns null when unsafe.
    /// </summary>
    public static string? Sanitize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        var p = path.Replace('\\', '/').Trim();
        while (p.StartsWith("./", StringComparison.Ordinal))
            p = p[2..];
        if (p.StartsWith('/') || p.Contains(':', StringComparison.Ordinal) || p.Contains('\0', StringComparison.Ordinal))
            return null;
        var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsAsciiDigit(stem[3])))
                return null;
        }
        return string.Join('/', parts);
    }

    /// <summary>Full path under <paramref name="root"/>; throws if it would land outside.</summary>
    public static string Resolve(string root, string relative)
    {
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{relative}' points outside the instance.");
        return full;
    }

    /// <summary>The content folder a path belongs to (for provenance records), if any.</summary>
    public static ContentLocation? LocationOf(string path)
    {
        var folder = path.Contains('/', StringComparison.Ordinal) ? path[..path.IndexOf('/', StringComparison.Ordinal)] : "";
        return folder switch
        {
            "mods" when path.Count(c => c == '/') == 1 => new ContentLocation(ContentKind.Mod),
            "resourcepacks" when path.Count(c => c == '/') == 1 => new ContentLocation(ContentKind.ResourcePack),
            "shaderpacks" when path.Count(c => c == '/') == 1 => new ContentLocation(ContentKind.ShaderPack),
            _ => null,
        };
    }
}
