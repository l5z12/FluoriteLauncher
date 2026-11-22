using Fluorite.Modding.Content;

namespace Fluorite.Modding.Platforms;

public enum ContentPlatform
{
    Modrinth,
    CurseForge,
}

public enum ReleaseChannel
{
    Release,
    Beta,
    Alpha,
}

public enum ContentSort
{
    Relevance,
    Downloads,
    Follows,
    Newest,
    Updated,
}

public enum RemoteDependencyKind
{
    Required,
    Optional,
    Incompatible,

    /// <summary>Bundled inside the file already (Modrinth <c>embedded</c>, CurseForge embedded library/include).</summary>
    Embedded,
}

/// <summary>What content is being looked for: Minecraft version and platform loader names (lower-case: fabric, forge…).</summary>
/// <param name="Loaders">Empty = don't filter by loader.</param>
public sealed record ContentTarget(ContentKind Kind, string? MinecraftVersion, IReadOnlyList<string> Loaders);

public sealed record ContentSearchQuery(ContentTarget Target, string? Text, ContentSort Sort, int Offset, int Limit)
{
    /// <summary>Platform-specific category id/slug.</summary>
    public string? Category { get; init; }
}

public sealed record ContentSearchResult(IReadOnlyList<RemoteProject> Hits, int TotalHits);

public sealed record RemoteCategory(string Id, string Name, string? IconUrl);

public sealed record RemoteProject(ContentPlatform Platform, string Id, string Slug, string Title)
{
    public string? Summary { get; init; }
    public string? Author { get; init; }
    public string? IconUrl { get; init; }
    public long Downloads { get; init; }
    public long Follows { get; init; }
    public DateTimeOffset? Updated { get; init; }
    public ContentKind Kind { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>The project's page on the platform's website.</summary>
    public string? WebUrl { get; init; }

    public string? SourceUrl { get; init; }
    public string? IssuesUrl { get; init; }
    public string? WikiUrl { get; init; }

    /// <summary>Long description if the endpoint returned it (Modrinth: Markdown).</summary>
    public string? Body { get; init; }

    public IReadOnlyList<string> GalleryUrls { get; init; } = [];
}

public sealed record RemoteFile(string FileName, long Size)
{
    /// <summary>Null when the author disabled third-party downloads (CurseForge) — see <see cref="ManualDownloadUrl"/>.</summary>
    public string? Url { get; init; }
    public string? Sha1 { get; init; }
    public string? Sha512 { get; init; }
    public bool Primary { get; init; }

    /// <summary>Page the user can download the file from themselves when <see cref="Url"/> is null.</summary>
    public string? ManualDownloadUrl { get; init; }
}

public sealed record RemoteDependency(RemoteDependencyKind Kind, string? ProjectId, string? VersionId)
{
    /// <summary>Modrinth: a file outside the platform (can't be resolved automatically).</summary>
    public string? FileName { get; init; }
}

public sealed record RemoteVersion(ContentPlatform Platform, string ProjectId, string Id, string Name, string VersionNumber)
{
    public ReleaseChannel Channel { get; init; }
    public DateTimeOffset Published { get; init; }
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    /// <summary>Lower-case loader names (fabric, quilt, forge, neoforge, liteloader, datapack, iris, optifine…).</summary>
    public IReadOnlyList<string> Loaders { get; init; } = [];

    public IReadOnlyList<RemoteFile> Files { get; init; } = [];
    public IReadOnlyList<RemoteDependency> Dependencies { get; init; } = [];

    /// <summary>Changelog if included in the response (Modrinth: Markdown); CurseForge needs a separate request.</summary>
    public string? Changelog { get; init; }

    public long Downloads { get; init; }

    public RemoteFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? (Files.Count > 0 ? Files[0] : null);
}

public enum TextFormat
{
    Markdown,
    Html,
}

/// <summary>The local file a hash lookup was made for, and what the platform says it is.</summary>
public sealed record IdentifiedFile(string Sha1, uint Murmur2, RemoteVersion Version);
