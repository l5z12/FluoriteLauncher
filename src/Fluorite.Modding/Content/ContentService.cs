using System.Collections.Concurrent;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Content;

/// <summary>
/// Content operations across platforms: identify local files by hash, check for updates, plan installs with
/// dependency resolution, and execute them (including files the user downloaded by hand).
/// </summary>
public sealed partial class ContentService : IDisposable
{
    private static readonly TimeSpan s_identifyRetry = TimeSpan.FromDays(1);
    private const int MaxDependencyDepth = 8;

    private readonly Dictionary<ContentPlatform, IContentPlatform> _platforms;
    private readonly DownloadEngine _downloader;
    private readonly MirrorTable _mirrors;
    private readonly ConcurrentDictionary<string, ContentManager> _managers = new(StringComparer.OrdinalIgnoreCase);

    public ContentService(IEnumerable<IContentPlatform> platforms, DownloadEngine downloader, MirrorTable mirrors)
    {
        Platforms = platforms.ToList();
        _platforms = Platforms.ToDictionary(p => p.Platform);
        _downloader = downloader;
        _mirrors = mirrors;
    }

    public IReadOnlyList<IContentPlatform> Platforms { get; }

    public IContentPlatform Get(ContentPlatform platform) =>
        _platforms.TryGetValue(platform, out var p) ? p
        : throw new InvalidOperationException($"{platform} isn't available in this build (no API key configured).");

    public bool Has(ContentPlatform platform) => _platforms.ContainsKey(platform);

    public ContentManager For(GameInstance instance) =>
        _managers.GetOrAdd(instance.GameDirectory, dir => new ContentManager(dir, instance.ContentDirectory));

    // ── Targets ─────────────────────────────────────────────────────────

    /// <summary>Platform loader names (lower-case) whose mods run on the instance's loader.</summary>
    public static IReadOnlyList<string> ModLoaderNames(InstanceManifest manifest) =>
        ModLoaderNames(ComponentResolver.PrimaryLoader(manifest)?.Uid, manifest.MinecraftVersion);

    /// <summary>Platform loader names (lower-case) whose mods run on the loader <paramref name="loaderUid"/>.</summary>
    public static IReadOnlyList<string> ModLoaderNames(string? loaderUid, string? mc)
    {
        return loaderUid switch
        {
            ComponentUids.FabricLoader => ["fabric"],
            ComponentUids.QuiltLoader => ["quilt", "fabric"],
            ComponentUids.Forge => ["forge"],
            // NeoForge for 1.20.1 is a Forge fork and loads Forge mods.
            ComponentUids.NeoForge => mc == "1.20.1" ? ["neoforge", "forge"] : ["neoforge"],
            ComponentUids.LegacyFabric => ["legacy-fabric", "fabric"],
            ComponentUids.LiteLoader => ["liteloader"],
            _ => [],
        };
    }

    public static ContentTarget TargetFor(InstanceManifest manifest, ContentKind kind) => new(
        kind,
        manifest.MinecraftVersion,
        kind switch
        {
            ContentKind.Mod => ModLoaderNames(manifest),
            ContentKind.DataPack => ["datapack"],
            _ => [],
        });

    /// <summary>Best version for the target: newest release, else newest beta, else newest alpha.</summary>
    public static RemoteVersion? PickBest(IEnumerable<RemoteVersion> versions, ContentTarget target) =>
        // Ties (separate files uploaded together) go to the instance's own loader over compatible fallbacks.
        Compatible(versions, target)
            .OrderBy(v => v.Channel)
            .ThenByDescending(v => v.Published.UtcDateTime.Date)
            .ThenBy(v => LoaderRank(v, target))
            .ThenByDescending(v => v.Published)
            .FirstOrDefault();

    /// <summary>The newest of <paramref name="versions"/> for the target, whatever its channel (same-day ties as in <see cref="PickBest"/>).</summary>
    internal static RemoteVersion? Newest(IEnumerable<RemoteVersion> versions, ContentTarget target) =>
        Compatible(versions, target)
            .OrderByDescending(v => v.Published.UtcDateTime.Date)
            .ThenBy(v => LoaderRank(v, target))
            .ThenByDescending(v => v.Published)
            .FirstOrDefault();

    internal static IEnumerable<RemoteVersion> Compatible(IEnumerable<RemoteVersion> versions, ContentTarget target) => versions
        .Where(v => target.MinecraftVersion is null || v.GameVersions.Count == 0 || v.GameVersions.Contains(target.MinecraftVersion))
        .Where(v => target.Loaders.Count == 0 || v.Loaders.Count == 0 || v.Loaders.Any(target.Loaders.Contains))
        .Where(v => v.PrimaryFile is not null);

    /// <summary>0 for a build for the instance's own loader, higher for loaders it runs mods of too.</summary>
    private static int LoaderRank(RemoteVersion v, ContentTarget target) =>
        target.Loaders.Count == 0 ? 0 : v.Loaders.Select(l => target.Loaders.ToList().IndexOf(l)).Where(i => i >= 0).DefaultIfEmpty(int.MaxValue).Min();

    // ── Identification ──────────────────────────────────────────────────

    /// <summary>
    /// Looks up files without a known source on Modrinth (SHA-1), then CurseForge (fingerprint); records matches with
    /// the project's title and icon. Returns the number of newly identified files.
    /// </summary>
    public async Task<int> IdentifyAsync(ContentManager manager, IReadOnlyList<ContentFile> files, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = files
            .Where(f => !f.IsDirectory && f.Record.Source is null && f.Hashes is not null)
            .Where(f => f.Record.IdentifiedAt is not { } at || now - at > s_identifyRetry)
            .ToList();
        if (pending.Count == 0)
            return 0;

        var found = new Dictionary<string, (RemoteVersion Version, RemoteProject? Project)>(StringComparer.Ordinal); // by sha1
        foreach (var platform in Platforms)
        {
            var remaining = pending.Where(f => !found.ContainsKey(f.Record.Sha1!)).Select(f => f.Hashes!).ToList();
            if (remaining.Count == 0)
                break;
            try
            {
                var matches = await platform.IdentifyAsync(remaining, ct).ConfigureAwait(false);
                var projects = matches.Count == 0 ? [] : await platform.GetProjectsAsync(matches.Select(m => m.Version.ProjectId).ToList(), ct).ConfigureAwait(false);
                foreach (var m in matches)
                    found[m.Sha1] = (m.Version, projects.FirstOrDefault(p => p.Id == m.Version.ProjectId));
            }
            catch (HttpRequestException)
            {
                // Offline or platform down: try again on the next scan.
                return 0;
            }
        }

        await manager.UpdateAsync(index =>
        {
            foreach (var f in pending)
            {
                if (!index.Files.TryGetValue(f.Key, out var record))
                    continue;
                record.IdentifiedAt = now;
                if (found.TryGetValue(f.Record.Sha1!, out var hit))
                    Apply(record, hit.Version, hit.Project);
            }
            return true;
        }, ct).ConfigureAwait(false);
        return pending.Count(f => found.ContainsKey(f.Record.Sha1!));
    }

    private static void Apply(ContentRecord record, RemoteVersion version, RemoteProject? project)
    {
        record.Source = new RemoteRef
        {
            Platform = version.Platform,
            ProjectId = version.ProjectId,
            VersionId = version.Id,
            VersionNumber = version.VersionNumber,
            Published = version.Published,
            Channel = version.Channel,
        };
        if (project is not null)
        {
            record.Title = project.Title;
            record.IconUrl = project.IconUrl;
            record.ProjectUrl = project.WebUrl;
        }
    }

    // ── Updates ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ContentUpdate>> CheckUpdatesAsync(IReadOnlyList<ContentFile> files, ContentTarget target, CancellationToken ct = default)
    {
        var result = new List<ContentUpdate>();
        foreach (var group in files.Where(f => f.Record is { Source: not null, Pinned: false, Sha1: not null }).GroupBy(f => f.Record.Source!.Platform))
        {
            if (!_platforms.TryGetValue(group.Key, out var platform))
                continue;
            var refs = group.Select(f => new InstalledRef(f.Record.Source!.ProjectId, f.Record.Source.VersionId, f.Record.Sha1!)).ToList();
            var updates = await platform.GetUpdatesAsync(refs, target, ct).ConfigureAwait(false);
            foreach (var f in group)
            {
                var source = f.Record.Source!;
                if (!updates.TryGetValue(source.VersionId, out var newest))
                    continue;
                RemoteVersion? latest = newest;
                // Platforms report the newest build of any channel; a release stays on releases (beta on beta+release).
                // A loader that also runs another loader's mods (Quilt, NeoForge for 1.20.1) gets a build for itself
                // when one came out the same day.
                var unstable = newest.Channel > source.Channel;
                var foreign = target.Loaders.Count > 1 && newest.Loaders.Count > 0 && !newest.Loaders.Contains(target.Loaders[0]);
                if (unstable || foreign)
                {
                    try
                    {
                        var versions = await platform.GetVersionsAsync(source.ProjectId, target, ct).ConfigureAwait(false);
                        latest = Newest(versions.Where(v => v.Channel <= source.Channel && v.Id != source.VersionId), target);
                    }
                    catch (HttpRequestException)
                    {
                        latest = null;
                    }
                    latest ??= unstable ? null : newest;
                    if (latest is null)
                        continue;
                }
                // Never "update" to something older (e.g. the file was installed for another Minecraft version).
                if (source.Published is { } current && latest.Published <= current)
                    continue;
                result.Add(new ContentUpdate(f, latest));
            }
        }
        return result;
    }

    // ── Planning ────────────────────────────────────────────────────────

    /// <summary>
    /// Plans installing <paramref name="version"/> into <paramref name="location"/>, replacing any file installed from the
    /// same project, plus its required dependencies (recursively) that aren't installed yet.
    /// </summary>
    /// <param name="installedIds">Mod ids already present (from metadata), to avoid re-installing a dependency that was added by hand.</param>
    public async Task<InstallPlan> PlanAsync(ContentManager manager, ContentTarget target, RemoteVersion version, ContentLocation location,
        RemoteProject? project = null, IReadOnlySet<string>? installedIds = null, CancellationToken ct = default)
    {
        var plan = new InstallPlan();
        var platform = Get(version.Platform);
        var records = await manager.GetRecordsAsync(ct).ConfigureAwait(false);
        var folder = location.RelativeFolder + "/";
        var inFolder = records.Where(r => r.Key.StartsWith(folder, StringComparison.OrdinalIgnoreCase) && Exists(manager, r.Key)).ToList();
        var installedProjects = inFolder
            .Where(r => r.Value.Source is { } s && s.Platform == version.Platform)
            .GroupBy(r => r.Value.Source!.ProjectId)
            .ToDictionary(g => g.Key, g => g.First().Key);
        // The same file can come from either platform (most mods are on both): match by content too.
        var installedSha1 = inFolder.Where(r => r.Value.Sha1 is not null).GroupBy(r => r.Value.Sha1!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string> { version.ProjectId };
        var rootKey = $"{version.Platform}:{version.ProjectId}";
        var queue = new Queue<(RemoteVersion Version, RemoteProject? Project, bool IsDependency, int Depth)>();
        queue.Enqueue((version, project, false, 0));
        var optionalIds = new HashSet<string>();

        while (queue.Count > 0)
        {
            var (v, p, isDep, depth) = queue.Dequeue();
            if (v.PrimaryFile is not { } file)
            {
                plan.Unresolved.Add(p?.Title ?? v.Name);
                continue;
            }
            var replaces = installedProjects.GetValueOrDefault(v.ProjectId);
            var sameFile = file.Sha1 is { } sha1 ? installedSha1.GetValueOrDefault(sha1) : null;
            if ((replaces is not null && records[replaces].Source?.VersionId == v.Id) || sameFile is not null)
            {
                // This exact version is already there: nothing to download (its dependencies were handled then).
                plan.AlreadyInstalled.Add(p?.Title ?? records[replaces ?? sameFile!].Title ?? v.Name);
                continue;
            }
            plan.Files.Add(new PlannedFile(v, file, location, isDep)
            {
                Project = p,
                ReplacesKey = replaces,
                NeededBy = isDep ? [rootKey] : [],
            });

            if (location.Kind != ContentKind.Mod || depth >= MaxDependencyDepth)
                continue;

            foreach (var dep in v.Dependencies)
            {
                if (dep.Kind == RemoteDependencyKind.Incompatible)
                {
                    if (dep.ProjectId is { } bad && installedProjects.TryGetValue(bad, out var badKey))
                        plan.Incompatible.Add(records[badKey].Title ?? Path.GetFileName(badKey));
                    continue;
                }
                if (dep.Kind == RemoteDependencyKind.Optional)
                {
                    if (dep.ProjectId is { } opt && !installedProjects.ContainsKey(opt) && !seen.Contains(opt))
                        optionalIds.Add(opt);
                    continue;
                }
                if (dep.Kind != RemoteDependencyKind.Required)
                    continue;

                var depProjectId = dep.ProjectId;
                RemoteVersion? pinned = null;
                if (depProjectId is null && dep.VersionId is { } pinnedId)
                {
                    pinned = await TryGetVersionAsync(platform, "", pinnedId, ct).ConfigureAwait(false);
                    depProjectId = pinned?.ProjectId;
                }
                if (depProjectId is null)
                {
                    plan.Unresolved.Add(dep.FileName ?? dep.VersionId ?? "?");
                    continue;
                }
                if (!seen.Add(depProjectId))
                    continue;
                if (installedProjects.TryGetValue(depProjectId, out var installedKey))
                {
                    // Installed already. A pinned version newer than that one means the new file needs it: update
                    // that too (unless the user pinned what they have).
                    var record = records[installedKey];
                    if (dep.VersionId is { } wanted && record is { Pinned: false, Source: { Published: { } have } source } && source.VersionId != wanted)
                    {
                        pinned ??= await TryGetVersionAsync(platform, depProjectId, wanted, ct).ConfigureAwait(false);
                        if (pinned?.PrimaryFile is not null && pinned.Published > have)
                        {
                            var known = new RemoteProject(pinned.Platform, depProjectId, "", record.Title ?? pinned.Name) { IconUrl = record.IconUrl, WebUrl = record.ProjectUrl };
                            queue.Enqueue((pinned, known, true, depth + 1));
                        }
                    }
                    continue;
                }

                RemoteProject? depProject = null;
                try
                {
                    depProject = await platform.GetProjectAsync(depProjectId, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                }
                // Added by hand (or from the other platform): the slug usually is (a variant of) the mod id.
                if (depProject is not null && installedIds is not null && SlugCandidates(depProject.Slug).Any(installedIds.Contains))
                    continue;

                var best = PickBest(await platform.GetVersionsAsync(depProjectId, target, ct).ConfigureAwait(false), target);
                if (dep.VersionId is { } vid)
                {
                    // A pinned dependency means "at least this": take it when it's newer than the best release.
                    pinned ??= await TryGetVersionAsync(platform, depProjectId, vid, ct).ConfigureAwait(false);
                    if (pinned is not null && (best is null || pinned.Published > best.Published))
                        best = pinned;
                }
                if (best?.PrimaryFile?.Sha1 is { } depSha1 && installedSha1.ContainsKey(depSha1))
                    continue;
                if (best is null)
                {
                    plan.Unresolved.Add(depProject?.Title ?? depProjectId);
                    continue;
                }
                queue.Enqueue((best, depProject, true, depth + 1));
            }
        }

        optionalIds.ExceptWith(seen);
        if (optionalIds.Count > 0)
        {
            try
            {
                plan.Optional.AddRange(await platform.GetProjectsAsync(optionalIds, ct).ConfigureAwait(false));
            }
            catch (HttpRequestException)
            {
            }
        }
        return plan;
    }

    /// <summary>
    /// File operations on freshly written jars can fail briefly while antivirus or the search indexer has them open.
    /// </summary>
    private static async Task RetryAsync(Action action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is (IOException and not FileNotFoundException and not DirectoryNotFoundException) or UnauthorizedAccessException && attempt < 8)
            {
                await Task.Delay(150 * attempt, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>"architectury-api" → architectury-api, architectury_api, architectury (mod ids rarely carry -api/-lib/-fabric…).</summary>
    internal static IEnumerable<string> SlugCandidates(string slug)
    {
        if (string.IsNullOrEmpty(slug))
            yield break;
        yield return slug;
        yield return slug.Replace('-', '_');
        foreach (var suffix in new[] { "-api", "-lib", "-library", "-fabric", "-forge", "-neoforge", "-quilt", "-mod" })
        {
            if (slug.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && slug.Length > suffix.Length + 2)
            {
                var stem = slug[..^suffix.Length];
                yield return stem;
                yield return stem.Replace('-', '_');
            }
        }
    }

    private static bool Exists(ContentManager manager, string key)
    {
        var path = Path.Combine(manager.GameDirectory, key.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) || File.Exists(path + ContentLocation.DisabledSuffix);
    }

    private static async Task<RemoteVersion?> TryGetVersionAsync(IContentPlatform platform, string projectId, string versionId, CancellationToken ct)
    {
        try
        {
            return await platform.GetVersionAsync(projectId, versionId, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Plans updating several files at once (each with its new required dependencies).</summary>
    public async Task<InstallPlan> PlanUpdatesAsync(ContentManager manager, ContentTarget target, IReadOnlyList<ContentUpdate> updates,
        IReadOnlySet<string>? installedIds = null, CancellationToken ct = default)
    {
        var plan = new InstallPlan();
        foreach (var u in updates)
        {
            var single = await PlanAsync(manager, target, u.Latest, u.File.Location, project: null, installedIds, ct).ConfigureAwait(false);
            // The update replaces exactly this file (its project lookup may be ambiguous).
            if (single.Files.Count > 0 && single.Files[0].Version.Id == u.Latest.Id)
                single.Files[0] = single.Files[0] with { ReplacesKey = u.File.Key, Project = single.Files[0].Project ?? new RemoteProject(u.Latest.Platform, u.Latest.ProjectId, "", u.File.DisplayName) { IconUrl = u.File.Record.IconUrl, WebUrl = u.File.Record.ProjectUrl } };
            plan.Merge(single);
        }
        return plan;
    }

    // ── Execution ───────────────────────────────────────────────────────

    /// <summary>
    /// Downloads every non-blocked file of the plan, swaps out replaced files (keeping their enabled state) and
    /// records provenance. Blocked files are returned for <see cref="CompleteManualAsync"/>.
    /// </summary>
    public async Task<IReadOnlyList<PlannedFile>> ExecuteAsync(ContentManager manager, InstallPlan plan, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        await StageAsync(manager, plan, progress, ct).ConfigureAwait(false);
        return await CommitAsync(manager, plan).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads the plan's files that aren't downloaded yet next to where they go (<c>.part</c>, which no loader
    /// reads). Nothing in the instance changes until <see cref="CommitAsync"/>; cancelling removes the plan's downloads.
    /// </summary>
    public async Task StageAsync(ContentManager manager, InstallPlan plan, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var pending = plan.Files.Where(f => !f.IsBlocked && !plan.Staged.Contains(f)).ToList();
        if (pending.Count == 0)
            return;
        var items = pending.Select(f => new DownloadItem(_mirrors.Candidates(f.File.Url!), PartPath(manager, f), f.File.Sha1, f.File.Size > 0 ? f.File.Size : null)
        {
            DisplayName = f.File.FileName,
        }).ToList();
        try
        {
            await _downloader.DownloadAllAsync(items, progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancelled before anything was placed: nothing changed, and no half-downloaded files stay behind.
            Discard(manager, plan);
            throw;
        }
        foreach (var f in pending)
            plan.Staged.Add(f);
    }

    /// <summary>
    /// Puts the downloaded files in place (swapping out replaced files, keeping their enabled state), turns on
    /// <paramref name="enable"/>, and records provenance. Blocked files are returned for <see cref="CompleteManualAsync"/>.
    /// </summary>
    public static async Task<IReadOnlyList<PlannedFile>> CommitAsync(ContentManager manager, InstallPlan plan, IEnumerable<ContentFile>? enable = null)
    {
        // Placing is quick and local; it isn't cancelled halfway, so a mod never lands without its dependencies.
        foreach (var f in plan.Files.Where(plan.Staged.Contains).ToList())
        {
            await PlaceAsync(manager, f, PartPath(manager, f), move: true, CancellationToken.None).ConfigureAwait(false);
            plan.Staged.Remove(f);
        }
        foreach (var file in enable ?? [])
        {
            if (!file.Enabled && File.Exists(file.Path))
                await manager.SetEnabledAsync(file, true).ConfigureAwait(false);
        }
        return plan.Files.Where(f => f.IsBlocked).ToList();
    }

    /// <summary>Deletes the plan's downloads that weren't put in place (cancelled, or the user changed their mind).</summary>
    public static void Discard(ContentManager manager, InstallPlan plan)
    {
        foreach (var f in plan.Files)
            Discard(manager, f);
        plan.Staged.Clear();
    }

    internal static void Discard(ContentManager manager, PlannedFile file)
    {
        try
        {
            File.Delete(PartPath(manager, file));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static string PartPath(ContentManager manager, PlannedFile file) => file.TargetPath(manager.GameDirectory) + ".part";

    /// <summary>
    /// Installs a blocked file the user downloaded themselves. Returns false when it isn't the expected file
    /// (hash mismatch), in which case nothing changes.
    /// </summary>
    public static async Task<bool> CompleteManualAsync(ContentManager manager, PlannedFile file, string downloadedPath, CancellationToken ct = default)
    {
        if (file.File.Sha1 is { } expected)
        {
            var actual = await Core.Net.HashUtil.Sha1FileAsync(downloadedPath, ct).ConfigureAwait(false);
            if (!Core.Net.HashUtil.HashEquals(actual, expected))
                return false;
        }
        var tmp = file.TargetPath(manager.GameDirectory) + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
        File.Copy(downloadedPath, tmp, overwrite: true);
        await PlaceAsync(manager, file, tmp, move: true, ct).ConfigureAwait(false);
        return true;
    }

    private static async Task PlaceAsync(ContentManager manager, PlannedFile f, string source, bool move, CancellationToken ct)
    {
        var target = f.TargetPath(manager.GameDirectory);
        var key = ContentManager.KeyFor(f.Location, f.File.FileName);
        var disabled = false;
        ContentRecord? previous = null;

        if (f.ReplacesKey is { } oldKey)
        {
            previous = await manager.GetRecordAsync(oldKey, ct).ConfigureAwait(false);
            var oldPath = Path.Combine(manager.GameDirectory, oldKey.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(oldPath + ContentLocation.DisabledSuffix))
            {
                disabled = !File.Exists(oldPath);
                await RetryAsync(() => File.Delete(oldPath + ContentLocation.DisabledSuffix), ct).ConfigureAwait(false);
            }
            if (File.Exists(oldPath) && !string.Equals(oldPath, target, StringComparison.OrdinalIgnoreCase))
                await RetryAsync(() => File.Delete(oldPath), ct).ConfigureAwait(false);
        }

        var final = disabled ? target + ContentLocation.DisabledSuffix : target;
        if (File.Exists(final))
            File.SetAttributes(final, FileAttributes.Normal);
        await RetryAsync(() =>
        {
            if (move)
                File.Move(source, final, overwrite: true);
            else
                File.Copy(source, final, overwrite: true);
        }, ct).ConfigureAwait(false);
        if (!disabled && File.Exists(target + ContentLocation.DisabledSuffix))
            await RetryAsync(() => File.Delete(target + ContentLocation.DisabledSuffix), ct).ConfigureAwait(false);

        var hashes = await ContentHashes.ComputeAsync(final, ct).ConfigureAwait(false);
        var info = new FileInfo(final);
        await manager.UpdateAsync(index =>
        {
            if (f.ReplacesKey is { } oldKey2 && !string.Equals(oldKey2, key, StringComparison.OrdinalIgnoreCase))
                index.Files.Remove(oldKey2);
            var record = new ContentRecord
            {
                Sha1 = hashes.Sha1,
                Sha512 = hashes.Sha512,
                Murmur2 = hashes.Murmur2,
                Size = info.Length,
                ModifiedUtc = info.LastWriteTimeUtc,
                IdentifiedAt = DateTimeOffset.UtcNow,
                Pinned = previous?.Pinned ?? false,
                IsDependency = previous?.IsDependency ?? f.IsDependency,
                Title = f.Project?.Title ?? previous?.Title,
                IconUrl = f.Project?.IconUrl ?? previous?.IconUrl,
                ProjectUrl = f.Project?.WebUrl ?? previous?.ProjectUrl,
            };
            Apply(record, f.Version, null);
            index.Files[key] = record;
            return true;
        }, ct).ConfigureAwait(false);
    }

    public void Dispose()
    {
        foreach (var m in _managers.Values)
            m.Dispose();
        _managers.Clear();
    }
}
