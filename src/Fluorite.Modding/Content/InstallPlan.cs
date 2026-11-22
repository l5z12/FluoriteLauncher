using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Content;

/// <summary>One file to download into an instance.</summary>
public sealed record PlannedFile(RemoteVersion Version, RemoteFile File, ContentLocation Location, bool IsDependency)
{
    public RemoteProject? Project { get; init; }

    /// <summary>Index key of the installed file this replaces (update / switching versions), if any.</summary>
    public string? ReplacesKey { get; init; }

    public string Title => Project?.Title ?? Version.Name;

    /// <summary>The author disabled third-party downloads; the user must fetch it from <see cref="RemoteFile.ManualDownloadUrl"/>.</summary>
    public bool IsBlocked => File.Url is null;

    public string TargetPath(string gameDirectory) => Path.Combine(Location.FullPath(gameDirectory), File.FileName);

    /// <summary><c>Platform:ProjectId</c>, the same for every version of the project.</summary>
    public string ProjectKey => $"{Version.Platform}:{Version.ProjectId}";

    /// <summary>
    /// <see cref="ProjectKey"/>s of the requested files this one comes with (a dependency, or a newer version of an
    /// installed mod they need). Empty for a requested file itself.
    /// </summary>
    public IReadOnlyList<string> NeededBy { get; init; } = [];

    /// <summary>The requested files this one stands or falls with: skipping all of them leaves it out too.</summary>
    public IReadOnlyList<string> Roots => NeededBy.Count > 0 ? NeededBy : [ProjectKey];

    /// <summary>Found by a mod id rather than declared by the platform: kept only if the downloaded jar provides it.</summary>
    internal string? MustProvide { get; init; }
}

/// <summary>What installing a version (plus its required dependencies) will do.</summary>
public sealed class InstallPlan
{
    public List<PlannedFile> Files { get; } = [];

    /// <summary>Optional dependencies that aren't installed (shown, not installed).</summary>
    public List<RemoteProject> Optional { get; } = [];

    /// <summary>Required dependencies with no compatible version (names or file names).</summary>
    public List<string> Unresolved { get; } = [];

    /// <summary>Installed content the new files declare themselves incompatible with (titles).</summary>
    public List<string> Incompatible { get; } = [];

    /// <summary>Requested versions that are already installed (titles); nothing to do for them.</summary>
    public List<string> AlreadyInstalled { get; } = [];

    public IEnumerable<PlannedFile> Dependencies => Files.Where(f => f.IsDependency);
    public IEnumerable<PlannedFile> Blocked => Files.Where(f => f.IsBlocked);

    /// <summary>Downloaded next to their place (<c>.part</c>) and not placed yet; see <see cref="ContentService.StageAsync"/>.</summary>
    internal HashSet<PlannedFile> Staged { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The titles of the planned files with these <see cref="PlannedFile.ProjectKey"/>s (see <see cref="PlannedFile.NeededBy"/>).</summary>
    public IReadOnlyList<string> TitlesOf(IEnumerable<string> projectKeys) =>
        projectKeys.Select(k => Files.FirstOrDefault(f => f.ProjectKey == k)?.Title).OfType<string>().Distinct().ToList();

    public void Merge(InstallPlan other)
    {
        foreach (var f in other.Files)
            Add(f);
        Optional.AddRange(other.Optional.Where(o => !Optional.Any(x => x.Id == o.Id) && !Files.Any(f => f.Version.ProjectId == o.Id)));
        Unresolved.AddRange(other.Unresolved.Except(Unresolved));
        Incompatible.AddRange(other.Incompatible.Except(Incompatible));
        AlreadyInstalled.AddRange(other.AlreadyInstalled.Except(AlreadyInstalled));
    }

    /// <summary>
    /// Adds a file, or combines it with the one already planned for the same project: a requested file wins over a
    /// dependency (an update asked for is never held back to the version another mod pins), otherwise the newer one.
    /// </summary>
    public void Add(PlannedFile file)
    {
        var i = Files.FindIndex(x => x.ProjectKey == file.ProjectKey);
        if (i < 0)
        {
            Files.Add(file);
            return;
        }
        var existing = Files[i];
        var keep = existing.IsDependency != file.IsDependency
            ? (file.IsDependency ? existing : file)
            : (file.Version.Published > existing.Version.Published ? file : existing);
        var dependency = existing.IsDependency && file.IsDependency;
        Files[i] = keep with
        {
            IsDependency = dependency,
            NeededBy = dependency ? existing.NeededBy.Union(file.NeededBy).ToList() : [],
            ReplacesKey = keep.ReplacesKey ?? existing.ReplacesKey ?? file.ReplacesKey,
            Project = keep.Project ?? existing.Project ?? file.Project,
        };
    }
}

public sealed record ContentUpdate(ContentFile File, RemoteVersion Latest)
{
    public string ProjectKey => $"{Latest.Platform}:{Latest.ProjectId}";
}
