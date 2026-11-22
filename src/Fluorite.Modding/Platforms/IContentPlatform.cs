using Fluorite.Modding.Util;

namespace Fluorite.Modding.Platforms;

/// <summary>A content host (Modrinth, CurseForge) behind one shape, so the UI and installer don't care which.</summary>
public interface IContentPlatform
{
    ContentPlatform Platform { get; }

    /// <summary>Format of <see cref="GetDescriptionAsync"/> and changelog text.</summary>
    TextFormat TextFormat { get; }

    Task<ContentSearchResult> SearchAsync(ContentSearchQuery query, CancellationToken ct = default);

    Task<RemoteProject> GetProjectAsync(string projectId, CancellationToken ct = default);

    Task<IReadOnlyList<RemoteProject>> GetProjectsAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct = default);

    /// <summary>Long description (Markdown for Modrinth, HTML for CurseForge).</summary>
    Task<string?> GetDescriptionAsync(RemoteProject project, CancellationToken ct = default);

    /// <summary>Versions compatible with <paramref name="target"/> (null = all), newest first.</summary>
    Task<IReadOnlyList<RemoteVersion>> GetVersionsAsync(string projectId, ContentTarget? target, CancellationToken ct = default);

    Task<RemoteVersion> GetVersionAsync(string projectId, string versionId, CancellationToken ct = default);

    Task<string?> GetChangelogAsync(RemoteVersion version, CancellationToken ct = default);

    Task<IReadOnlyList<RemoteCategory>> GetCategoriesAsync(Content.ContentKind kind, CancellationToken ct = default);

    /// <summary>Looks files up by hash; returns entries only for files the platform knows.</summary>
    Task<IReadOnlyList<IdentifiedFile>> IdentifyAsync(IReadOnlyCollection<ContentHashes> files, CancellationToken ct = default);

    /// <summary>
    /// Newest version compatible with <paramref name="target"/> for each installed version (keyed by current version id).
    /// Entries whose newest version is the installed one are omitted.
    /// </summary>
    Task<IReadOnlyDictionary<string, RemoteVersion>> GetUpdatesAsync(IReadOnlyCollection<InstalledRef> installed, ContentTarget target, CancellationToken ct = default);
}

/// <summary>An installed file's identity on a platform, for update checks.</summary>
public sealed record InstalledRef(string ProjectId, string VersionId, string Sha1);
