using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Loaders;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Content;

/// <summary>Something that works now and wouldn't with the new files: an error the pre-launch check would report.</summary>
/// <param name="Cause">The planned file behind it (skipping what it came with makes the problem go away); null when unclear.</param>
public sealed record UpdateProblem(ModIssue Issue, PlannedFile? Cause);

/// <summary>A newer mod loader version that the new files need and every other mod accepts.</summary>
public sealed record LoaderChange(string Uid, string From, string To)
{
    /// <summary>Titles of the mods that need it.</summary>
    public IReadOnlyList<string> NeededBy { get; init; } = [];
}

/// <summary>A turned-off mod that provides something a new file needs.</summary>
/// <param name="NeededBy">Project keys of the requested files it's turned on for.</param>
public sealed record ModToEnable(ContentFile File, IReadOnlyList<string> NeededBy);

/// <summary>
/// A planned install or update whose mods are downloaded (not placed) and checked with their own manifests against the
/// instance's other mods: the version ranges platforms don't publish (loader, Minecraft and dependency versions,
/// declared breaks). Says what would stop working, and what can be left out or changed so it doesn't.
/// </summary>
public sealed class InstallReview
{
    private readonly ContentManager _manager;
    private readonly IReadOnlyList<ContentFile> _current;
    private readonly HashSet<(ModIssueKind, string, string?)> _before;
    private readonly Dictionary<PlannedFile, ContentFile> _files = new(ReferenceEqualityComparer.Instance);
    private readonly string? _loaderUid;
    private readonly string? _minecraftVersion;

    internal InstallReview(ContentManager manager, InstallPlan plan, IReadOnlyList<ContentFile> current, string? loaderUid, string? loaderVersion, string? minecraftVersion)
    {
        _manager = manager;
        Plan = plan;
        _current = current;
        _loaderUid = loaderUid;
        LoaderVersion = loaderVersion;
        _minecraftVersion = minecraftVersion;
        // What's wrong already isn't the new files' doing.
        _before = ModChecker.Check(current, loaderUid, loaderVersion, minecraftVersion).Select(Identity).ToHashSet();
    }

    public InstallPlan Plan { get; }

    /// <summary>The instance's loader version now.</summary>
    public string? LoaderVersion { get; }

    /// <summary>Turned-off mods the new files need, turned back on with them.</summary>
    public List<ModToEnable> Enable { get; } = [];

    /// <summary>A loader version that clears every "needs a newer loader" problem, if there's one.</summary>
    public LoaderChange? Loader { get; internal set; }

    /// <summary>Files the review added: newer versions of installed mods, and dependencies only the manifests declare.</summary>
    public List<PlannedFile> Added { get; } = [];

    /// <summary>What would stop working, with the loader moved to <see cref="Loader"/> or left as it is.</summary>
    public IReadOnlyList<UpdateProblem> Problems(bool withLoader) => Compute(Plan.Files, Enable, VersionFor(withLoader));

    /// <summary>
    /// What "skip the problems" leaves out: the requests with problems and everything that came with them, then any
    /// request that breaks without those, until what's left works.
    /// </summary>
    public IReadOnlyList<PlannedFile> SkipSet(bool withLoader) => Skipping(withLoader).Files;

    /// <summary>Leaves <see cref="SkipSet"/> out of the plan (deleting its downloads) and returns it.</summary>
    public IReadOnlyList<PlannedFile> Skip(bool withLoader)
    {
        var (files, roots) = Skipping(withLoader);
        foreach (var f in files)
            Drop(f);
        Enable.RemoveAll(e => e.NeededBy.Count > 0 && e.NeededBy.All(roots.Contains));
        // What needed the newer loader may be what was left out.
        if (Loader is not null && !Compute(Plan.Files, Enable, LoaderVersion).Any(p => p.Issue.Kind == ModIssueKind.WrongLoaderVersion))
            Loader = null;
        return files;
    }

    private (List<PlannedFile> Files, HashSet<string> Roots) Skipping(bool withLoader)
    {
        var keep = new List<PlannedFile>(Plan.Files);
        var enable = new List<ModToEnable>(Enable);
        var roots = new HashSet<string>(StringComparer.Ordinal);
        var version = VersionFor(withLoader);
        // Leaving one request out can break another that needed it: repeat until nothing new breaks.
        for (var round = 0; round < 32; round++)
        {
            var more = false;
            foreach (var problem in Compute(keep, enable, version))
            {
                foreach (var root in problem.Cause?.Roots ?? [])
                    more |= roots.Add(root);
            }
            if (!more)
                break;
            keep.RemoveAll(f => f.Roots.All(roots.Contains));
            enable.RemoveAll(e => e.NeededBy.Count > 0 && e.NeededBy.All(roots.Contains));
        }
        var kept = new HashSet<PlannedFile>(keep, ReferenceEqualityComparer.Instance);
        return (Plan.Files.Where(f => !kept.Contains(f)).ToList(), roots);
    }

    private string? VersionFor(bool withLoader) => withLoader && Loader is { } change ? change.To : LoaderVersion;

    /// <summary>Reads a downloaded mod's manifest, as the file it will be once in place.</summary>
    internal void Load(PlannedFile f)
    {
        if (f.Location.Kind != ContentKind.Mod || !Plan.Staged.Contains(f) || _files.ContainsKey(f))
            return;
        var old = f.ReplacesKey is { } key ? Find(key) : null;
        var target = f.TargetPath(_manager.GameDirectory);
        _files[f] = new ContentFile
        {
            // A replaced mod that's turned off stays off.
            Path = old is { Enabled: false } ? target + ContentLocation.DisabledSuffix : target,
            Key = ContentManager.KeyFor(f.Location, f.File.FileName),
            Location = f.Location,
            Metadata = ContentMetadataReader.Read(ContentService.PartPath(_manager, f)),
            Record = new ContentRecord { Title = f.Project?.Title ?? old?.Record.Title },
            Size = f.File.Size,
        };
    }

    /// <summary>Takes a file out of the plan and deletes its download.</summary>
    internal void Drop(PlannedFile f)
    {
        Plan.Files.Remove(f);
        Plan.Staged.Remove(f);
        _files.Remove(f);
        Added.Remove(f);
        ContentService.Discard(_manager, f);
    }

    internal bool Provides(PlannedFile f, string id) =>
        _files.TryGetValue(f, out var file) && file.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    internal bool IsReplaced(ContentFile file) =>
        Plan.Files.Any(f => string.Equals(f.ReplacesKey, file.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The party of a two-mod problem that stays as it is: an installed mod no planned file replaces.</summary>
    internal ContentFile? Unchanged(ModIssue issue)
    {
        ContentFile? Installed(string? name) => name is null ? null
            : _current.FirstOrDefault(m => !IsReplaced(m) && string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase));
        return Installed(issue.File) ?? Installed(issue.OtherFile);
    }

    internal IReadOnlyList<ContentFile> Current => _current;

    internal List<UpdateProblem> Compute(IReadOnlyCollection<PlannedFile> files, IReadOnlyCollection<ModToEnable> enable, string? loaderVersion)
    {
        var staged = files.Where(_files.ContainsKey).ToList();
        var replaced = staged.Select(f => f.ReplacesKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var on = enable.Select(e => e.File.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var after = new List<ContentFile>(_current.Count + staged.Count);
        foreach (var m in _current)
        {
            if (!replaced.Contains(m.Key))
                after.Add(on.Contains(m.Key) && !m.Enabled ? AsEnabled(m) : m);
        }
        after.AddRange(staged.Select(f => _files[f]));

        // A file still to be downloaded by hand can't be read yet: what it provides is unknown until it's in.
        var unsure = files.Any(f => f.IsBlocked && f.Location.Kind == ContentKind.Mod);
        var problems = new List<UpdateProblem>();
        foreach (var issue in ModChecker.Check(after, _loaderUid, loaderVersion, _minecraftVersion))
        {
            if (issue.Severity != ModIssueSeverity.Error || _before.Contains(Identity(issue)))
                continue;
            if (unsure && issue.Kind is ModIssueKind.MissingDependency or ModIssueKind.DependencyVersion)
                continue;
            problems.Add(new UpdateProblem(issue, CauseOf(issue, staged)));
        }
        return problems;
    }

    private PlannedFile? CauseOf(ModIssue issue, List<PlannedFile> staged)
    {
        PlannedFile? New(string? name) => name is null ? null
            : staged.FirstOrDefault(f => string.Equals(_files[f].FileName, name, StringComparison.OrdinalIgnoreCase));
        if ((New(issue.File) ?? New(issue.OtherFile)) is { } cause)
            return cause;
        // A new version no longer brings something another mod needs (a library it used to bundle).
        if (issue.Kind == ModIssueKind.MissingDependency && issue.Other is { } id)
        {
            return staged.FirstOrDefault(f => f.ReplacesKey is { } key && Find(key) is { Enabled: true } old
                && old.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase));
        }
        return null;
    }

    private ContentFile? Find(string key) => _current.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The same problem before and after (versions and file names change; the mods involved don't).</summary>
    private static (ModIssueKind, string, string?) Identity(ModIssue issue) => (issue.Kind, issue.Subject, issue.Other);

    private static ContentFile AsEnabled(ContentFile m) => new()
    {
        Path = ContentLocation.StripDisabled(m.Path),
        Key = m.Key,
        Location = m.Location,
        Metadata = m.Metadata,
        Record = m.Record,
        IsDirectory = m.IsDirectory,
        Size = m.Size,
        ModifiedUtc = m.ModifiedUtc,
    };
}

public sealed partial class ContentService
{
    private const int ReviewRounds = 3;
    private const int MaxLoaderCandidates = 120;

    /// <summary>
    /// Checks a staged plan's mods (see <see cref="StageAsync"/>) with their own manifests against the instance's other
    /// mods, and fixes what it can before anything is placed:
    /// <list type="bullet">
    /// <item>a newer version of an installed mod that a new file needs (or that only accepted the old version of one);</item>
    /// <item>a turned-off mod that provides something a new file needs;</item>
    /// <item>a dependency only the manifest declares, found on Modrinth by its mod id;</item>
    /// <item>the newest loader version every mod accepts, when new files need a newer one (<paramref name="loaders"/>).</item>
    /// </list>
    /// What's left is in <see cref="InstallReview.Problems"/>.
    /// </summary>
    /// <param name="loaders">The instance's loader, when its version may be changed.</param>
    public async Task<InstallReview> ReviewAsync(ContentManager manager, InstallPlan plan, ContentTarget target, InstanceManifest manifest,
        ILoaderProvider? loaders = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var current = await manager.ScanAsync(new ContentLocation(ContentKind.Mod), ct).ConfigureAwait(false);
        var loader = ComponentResolver.PrimaryLoader(manifest);
        var review = new InstallReview(manager, plan, current, loader?.Uid, loader?.Version, manifest.MinecraftVersion);
        foreach (var f in plan.Files)
            review.Load(f);

        var tried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var round = 0; round < ReviewRounds; round++)
        {
            var enabled = false;
            var added = new List<PlannedFile>();
            foreach (var problem in review.Problems(withLoader: false))
            {
                var roots = problem.Cause?.Roots ?? [];
                switch (problem.Issue.Kind)
                {
                    case ModIssueKind.DependencyVersion or ModIssueKind.Incompatible:
                        if (await NewerForAsync(review, problem, roots, target, tried, ct).ConfigureAwait(false) is { } newer)
                        {
                            added.Add(newer);
                            plan.Files.Add(newer);
                        }
                        break;
                    case ModIssueKind.MissingDependency when problem.Issue.Other is { } id && tried.Add("id:" + id):
                        if (review.Current.FirstOrDefault(m => !m.Enabled && !m.IsDirectory && !review.IsReplaced(m)
                            && m.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase)) is { } off)
                        {
                            review.Enable.Add(new ModToEnable(off, roots));
                            enabled = true;
                        }
                        else if (await FindByModIdAsync(id, target, plan, roots, ct).ConfigureAwait(false) is { } found)
                        {
                            added.Add(found);
                            plan.Files.Add(found);
                        }
                        break;
                }
            }
            if (added.Count > 0)
            {
                await StageAsync(manager, plan, progress, ct).ConfigureAwait(false);
                foreach (var f in added)
                {
                    review.Load(f);
                    // Found by name only: kept if it really is the mod that was missing.
                    if (f.MustProvide is { } id && !review.Provides(f, id))
                        review.Drop(f);
                    else
                        review.Added.Add(f);
                }
            }
            if (!enabled && added.Count == 0)
                break;
        }

        if (loaders is not null && loader is not null && manifest.MinecraftVersion is { } mc)
            review.Loader = await SuggestLoaderAsync(review, loaders, loader, mc, ct).ConfigureAwait(false);
        return review;
    }

    /// <summary>
    /// Of two mods that don't get along after the change, the one that isn't changing may have a newer version that
    /// does: a library the new version needs a newer build of, or a mod that only accepted the library's old version.
    /// </summary>
    private async Task<PlannedFile?> NewerForAsync(InstallReview review, UpdateProblem problem, IReadOnlyList<string> roots, ContentTarget target,
        HashSet<string> tried, CancellationToken ct)
    {
        if (review.Unchanged(problem.Issue) is not { Record: { Pinned: false, Source: { } source } } other
            || !tried.Add(other.Key)
            || !_platforms.TryGetValue(source.Platform, out var platform)
            || review.Plan.Files.Any(f => f.Version.Platform == source.Platform && f.Version.ProjectId == source.ProjectId))
            return null;
        IReadOnlyList<RemoteVersion> versions;
        try
        {
            versions = await platform.GetVersionsAsync(source.ProjectId, target, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        var newer = Compatible(versions, target).Where(v => v.Id != source.VersionId && (source.Published is not { } at || v.Published > at)).ToList();
        // As stable as the installed file when that's enough.
        var pool = newer.Where(v => v.Channel <= source.Channel).ToList();
        if (pool.Count == 0)
            pool = newer;
        // When the other mod states the version it needs of this one, a build whose number matches comes first.
        if (problem.Issue is { Range: { } range, Loader: { } kind } && string.Equals(problem.Issue.OtherFile, other.FileName, StringComparison.OrdinalIgnoreCase))
        {
            var fits = pool.Where(v => VersionPredicate.Matches(range, v.VersionNumber, kind) == true).ToList();
            if (fits.Count > 0)
                pool = fits;
        }
        if (Newest(pool, target) is not { PrimaryFile: { } file } pick)
            return null;
        return new PlannedFile(pick, file, other.Location, IsDependency: true)
        {
            ReplacesKey = other.Key,
            Project = new RemoteProject(source.Platform, source.ProjectId, "", other.DisplayName) { IconUrl = other.Record.IconUrl, WebUrl = other.Record.ProjectUrl },
            NeededBy = roots,
        };
    }

    /// <summary>A dependency only the manifest declares, by mod id: on Modrinth the project's slug usually is the id.</summary>
    private async Task<PlannedFile?> FindByModIdAsync(string id, ContentTarget target, InstallPlan plan, IReadOnlyList<string> roots, CancellationToken ct)
    {
        if (!_platforms.TryGetValue(ContentPlatform.Modrinth, out var modrinth))
            return null;
        foreach (var slug in new[] { id, id.Replace('_', '-') }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var project = await modrinth.GetProjectAsync(slug, ct).ConfigureAwait(false);
                if (project.Kind != ContentKind.Mod || plan.Files.Any(f => f.Version.Platform == project.Platform && f.Version.ProjectId == project.Id))
                    continue;
                var best = PickBest(await modrinth.GetVersionsAsync(project.Id, target, ct).ConfigureAwait(false), target);
                if (best?.PrimaryFile is { } file)
                    return new PlannedFile(best, file, new ContentLocation(ContentKind.Mod), IsDependency: true) { Project = project, NeededBy = roots, MustProvide = id };
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or System.Text.Json.JsonException)
            {
                // No project by that name.
            }
        }
        return null;
    }

    /// <summary>
    /// When new files need a newer loader: the newest stable build that every mod accepts (an old mod may cap it),
    /// else the newest preview that does.
    /// </summary>
    private static async Task<LoaderChange?> SuggestLoaderAsync(InstallReview review, ILoaderProvider loaders, ComponentRef loader, string minecraftVersion, CancellationToken ct)
    {
        var needing = review.Problems(withLoader: false).Where(p => p.Issue.Kind == ModIssueKind.WrongLoaderVersion).ToList();
        if (needing.Count == 0)
            return null;
        IReadOnlyList<LoaderVersion> versions;
        try
        {
            versions = await loaders.GetVersionsAsync(minecraftVersion, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
        var candidates = versions
            .Where(v => LoaderVersionComparer.Instance.Compare(v.Version, loader.Version) > 0)
            .OrderBy(v => IsPreview(v.Version))
            .ThenByDescending(v => v.Version, LoaderVersionComparer.Instance)
            .Take(MaxLoaderCandidates);
        foreach (var v in candidates)
        {
            if (!review.Compute(review.Plan.Files, review.Enable, v.Version).Any(p => p.Issue.Kind == ModIssueKind.WrongLoaderVersion))
                return new LoaderChange(loader.Uid, loader.Version, v.Version) { NeededBy = needing.Select(p => p.Issue.Subject).Distinct().ToList() };
        }
        return null;
    }

    private static bool IsPreview(string version) =>
        version.Contains("beta", StringComparison.OrdinalIgnoreCase) || version.Contains("alpha", StringComparison.OrdinalIgnoreCase)
        || version.Contains("-pre", StringComparison.OrdinalIgnoreCase) || version.Contains("-rc", StringComparison.OrdinalIgnoreCase);
}
