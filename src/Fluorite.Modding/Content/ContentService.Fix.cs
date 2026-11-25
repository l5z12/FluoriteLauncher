using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Loaders;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Content;

/// <summary>What a <see cref="ModFix"/> does.</summary>
public enum ModFixKind
{
    /// <summary>Turns on a turned-off mod that provides what's missing.</summary>
    Enable,

    /// <summary>Installs a missing dependency.</summary>
    Install,

    /// <summary>Swaps a mod for a version of it that fits (Minecraft, the loader, another mod's version).</summary>
    Replace,

    /// <summary>Turns off one of <see cref="ModFix.Choices"/>: a mod that can't work here, or a side of a conflict.</summary>
    Disable,

    /// <summary>Keeps one of <see cref="ModFix.Choices"/> (copies of a mod) and turns off the others.</summary>
    KeepOne,

    /// <summary>Moves the loader to a version every mod accepts.</summary>
    LoaderVersion,

    /// <summary>Adds the loader the mods are for, to an instance that has none.</summary>
    AddLoader,
}

/// <summary>One change that clears mod problems (see <see cref="ModFixPlan"/>).</summary>
public sealed class ModFix
{
    public required ModFixKind Kind { get; init; }

    /// <summary>The problems it's for.</summary>
    public IReadOnlyList<ModIssue> Issues { get; init; } = [];

    /// <summary><see cref="ModFixKind.Enable"/>: the mod turned on. <see cref="ModFixKind.Replace"/>: the mod swapped out.</summary>
    public ContentFile? File { get; init; }

    /// <summary><see cref="ModFixKind.Install"/> and <see cref="ModFixKind.Replace"/>: the download (made already, next to its place).</summary>
    public PlannedFile? Download { get; init; }

    /// <summary>
    /// <see cref="ModFixKind.Disable"/>: the mods one of which is turned off (two for a conflict, else one).
    /// <see cref="ModFixKind.KeepOne"/>: the copies, one of which stays on.
    /// </summary>
    public IReadOnlyList<ContentFile> Choices { get; init; } = [];

    /// <summary>Index in <see cref="Choices"/>: the one turned off (Disable) or kept (KeepOne).</summary>
    public int Choice { get; set; }

    /// <summary>The loader's new version; <see cref="LoaderChange.From"/> is empty when it's added.</summary>
    public LoaderChange? Loader { get; init; }

    /// <summary>Part of the fix: the user can leave any change out.</summary>
    public bool Selected { get; set; } = true;

    /// <summary>The mods this turns off, as <see cref="Choice"/> is now.</summary>
    public IEnumerable<ContentFile> TurnedOff => Kind switch
    {
        ModFixKind.Disable => [Choices[Choice]],
        ModFixKind.KeepOne => Choices.Where((_, i) => i != Choice),
        _ => [],
    };
}

/// <summary>
/// Changes that clear an instance's mod problems (<see cref="ContentService.PlanFixesAsync"/>), with their downloads
/// made and read, and what would still be wrong with the changes the user keeps.
/// </summary>
public sealed class ModFixPlan
{
    private readonly List<ModFix> _fixes = [];

    /// <summary><see cref="Identity"/> of every problem found before, and of those to fix.</summary>
    private readonly HashSet<string> _before;
    private readonly HashSet<string> _targets;

    internal ModFixPlan(ContentManager manager, IReadOnlyList<ContentFile> current, string? loaderUid, string? loaderVersion, string? minecraftVersion,
        Func<ModIssue, bool>? include)
    {
        Manager = manager;
        Current = current;
        LoaderUid = loaderUid;
        LoaderVersion = loaderVersion;
        MinecraftVersion = minecraftVersion;
        var issues = ModChecker.Check(current, loaderUid, loaderVersion, minecraftVersion);
        Targets = issues.Where(i => Counts(i) && (include?.Invoke(i) ?? true)).ToList();
        _before = issues.Select(Identity).ToHashSet(StringComparer.Ordinal);
        _targets = Targets.Select(Identity).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>The problems to fix.</summary>
    public IReadOnlyList<ModIssue> Targets { get; }

    public IReadOnlyList<ModFix> Fixes => _fixes;

    /// <summary>The selected change of the loader, if there's one.</summary>
    public LoaderChange? Loader => _fixes.LastOrDefault(f => f.Selected && f.Loader is not null)?.Loader;

    /// <summary>What's still wrong with the selected changes: the problems they don't clear, and any they'd bring.</summary>
    public IReadOnlyList<ModIssue> Remaining() => Evaluate(_fixes.Where(f => f.Selected));

    /// <summary>How many of <see cref="Targets"/> the selected changes clear.</summary>
    public int FixedCount() => FixedCount(Remaining());

    /// <summary>How many of <see cref="Targets"/> aren't among <paramref name="left"/>.</summary>
    public int FixedCount(IEnumerable<ModIssue> left)
    {
        var still = left.Select(Identity).ToHashSet(StringComparer.Ordinal);
        return Targets.Count(t => !still.Contains(Identity(t)));
    }

    /// <summary>
    /// What's wrong once the changes are made, counted as <see cref="Remaining"/> is: from the instance as it is now,
    /// since a change may not have gone through (a file in the way, the loader not saved).
    /// </summary>
    public async Task<IReadOnlyList<ModIssue>> RecheckAsync(InstanceManifest manifest, CancellationToken ct = default)
    {
        var now = await Manager.ScanAsync(new ContentLocation(ContentKind.Mod), ct).ConfigureAwait(false);
        var loader = ComponentResolver.PrimaryLoader(manifest);
        return Relevant(ModChecker.Check(now, loader?.Uid, loader?.Version, manifest.MinecraftVersion));
    }

    /// <summary>Deletes the downloads: the changes won't be made.</summary>
    public void Discard() => ContentService.Discard(Manager, Downloads);

    internal ContentManager Manager { get; }
    internal IReadOnlyList<ContentFile> Current { get; }
    internal string? LoaderUid { get; }
    internal string? LoaderVersion { get; }
    internal string? MinecraftVersion { get; }

    /// <summary>Every change's download.</summary>
    internal InstallPlan Downloads { get; } = new();

    /// <summary>The downloads that came in, read as the files they'll be once in place.</summary>
    internal Dictionary<PlannedFile, ContentFile> Read { get; } = new(ReferenceEqualityComparer.Instance);

    /// <summary>The loader in effect with the changes so far (one may add it).</summary>
    internal string? EffectiveLoaderUid => _fixes.LastOrDefault(f => f.Kind == ModFixKind.AddLoader)?.Loader?.Uid ?? LoaderUid;

    /// <summary>Goes up with every change added or taken out.</summary>
    internal int Edits { get; private set; }

    internal void Add(ModFix fix)
    {
        _fixes.Add(fix);
        Edits++;
        if (fix.Download is { } d)
            Downloads.Files.Add(d);
    }

    /// <summary>Takes a change out again, deleting its download.</summary>
    internal void Withdraw(ModFix fix)
    {
        if (!_fixes.Remove(fix))
            return;
        Edits++;
        if (fix.Download is { } d)
            Drop(d);
    }

    internal void Drop(PlannedFile d)
    {
        Downloads.Files.Remove(d);
        Downloads.Staged.Remove(d);
        Read.Remove(d);
        ContentService.Discard(Manager, d);
    }

    internal bool IsTurnedOff(ContentFile file) => _fixes.Any(f => f.TurnedOff.Contains(file));

    internal bool IsTurnedOn(ContentFile file) => _fixes.Any(f => f.Kind == ModFixKind.Enable && ReferenceEquals(f.File, file));

    internal bool IsReplaced(ContentFile file) =>
        _fixes.Any(f => f.Download?.ReplacesKey is { } key && string.Equals(key, file.Key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The problems that count (as <see cref="Remaining"/> reports them) with <paramref name="fixes"/> made.</summary>
    internal List<ModIssue> Evaluate(IEnumerable<ModFix> fixes)
    {
        var list = fixes.ToList();
        // A replaced file goes, and its turned-off twin with it (as when it's put in place); until its download is read,
        // it's what's there.
        var replaced = list.Select(f => f.Download).OfType<PlannedFile>().Where(Read.ContainsKey)
            .Select(d => d.ReplacesKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var off = new HashSet<ContentFile>(list.SelectMany(f => f.TurnedOff), ReferenceEqualityComparer.Instance);
        var on = new HashSet<ContentFile>(list.Where(f => f.Kind == ModFixKind.Enable).Select(f => f.File).OfType<ContentFile>(), ReferenceEqualityComparer.Instance);
        var (uid, version) = (LoaderUid, LoaderVersion);
        if (list.LastOrDefault(f => f.Loader is not null)?.Loader is { } change)
            (uid, version) = (change.Uid, change.To);
        var after = new List<ContentFile>(Current.Count + list.Count);
        foreach (var m in Current)
        {
            if (!replaced.Contains(m.Key))
                after.Add(off.Contains(m) ? Toggled(m, enabled: false) : on.Contains(m) ? Toggled(m, enabled: true) : m);
        }
        after.AddRange(list.Select(f => f.Download).OfType<PlannedFile>().Where(Read.ContainsKey).Select(d => Read[d]));
        return Relevant(ModChecker.Check(after, uid, version, MinecraftVersion));
    }

    /// <summary>Those asked about, and new ones; not the rest of what was wrong already (dismissed, other mods').</summary>
    private List<ModIssue> Relevant(IEnumerable<ModIssue> issues) =>
        issues.Where(i => Counts(i) && (_targets.Contains(Identity(i)) || !_before.Contains(Identity(i)))).ToList();

    /// <summary>Errors, and the missing loader (a warning, as mods do nothing without one).</summary>
    private static bool Counts(ModIssue issue) => issue.Severity == ModIssueSeverity.Error || issue.Kind == ModIssueKind.NoLoader;

    /// <summary>The same problem whatever the files are called and whatever versions they're at.</summary>
    internal static string Identity(ModIssue issue) => issue.Kind == ModIssueKind.NoLoader
        ? nameof(ModIssueKind.NoLoader) // Its file is just the first mod found.
        : $"{issue.Kind}|{(issue.ModId ?? issue.File).ToLowerInvariant()}|{(issue.OtherId ?? issue.Other)?.ToLowerInvariant()}";

    internal static HashSet<string> Identities(IEnumerable<ModIssue> issues) => issues.Select(Identity).ToHashSet(StringComparer.Ordinal);

    internal static ContentFile Toggled(ContentFile m, bool enabled) => m.Enabled == enabled ? m : new()
    {
        Path = enabled ? ContentLocation.StripDisabled(m.Path) : m.Path + ContentLocation.DisabledSuffix,
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
    private const int MaxFixRounds = 8;

    /// <summary>
    /// Looks for changes that clear the instance's mod problems (<see cref="ModChecker"/>'s errors, and a missing loader),
    /// each from the safest way out down to turning a mod off:
    /// <list type="bullet">
    /// <item>a missing dependency: a turned-off mod that has it, then the platform's dependencies of the mod, then its mod id on Modrinth;</item>
    /// <item>a mod for another Minecraft version or loader: a version of it that fits;</item>
    /// <item>a dependency at the wrong version: a version of it in the range, or a newer build of the mod that needs it;</item>
    /// <item>a mod that needs a newer loader: the newest loader version every mod accepts;</item>
    /// <item>two copies of a mod: the newest stays;</item>
    /// <item>mods with no loader: the loader most of them are for.</item>
    /// </list>
    /// Downloads are made (not placed) and read, and kept only if they help. Nothing in the instance changes until
    /// <see cref="ApplyFixesAsync"/>; cancelling deletes the downloads.
    /// </summary>
    /// <param name="include">Which problems to fix (by default every one that counts, dismissed or not).</param>
    /// <param name="loaders">The instance's loaders by uid, when its loader may change (or be added).</param>
    public async Task<ModFixPlan> PlanFixesAsync(ContentManager manager, InstanceManifest manifest, Func<ModIssue, bool>? include = null,
        Func<string, ILoaderProvider?>? loaders = null, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var current = await manager.ScanAsync(new ContentLocation(ContentKind.Mod), ct).ConfigureAwait(false);
        var loader = ComponentResolver.PrimaryLoader(manifest);
        var plan = new ModFixPlan(manager, current, loader?.Uid, loader?.Version, manifest.MinecraftVersion, include);
        var session = new FixSession(this, plan, loaders, ct);
        try
        {
            for (var round = 0; round < MaxFixRounds && plan.Targets.Count > 0; round++)
            {
                ct.ThrowIfCancellationRequested();
                // What the last round's downloads turned out to be decides what's tried next.
                var pruned = session.PruneUnhelpful();
                var advanced = await session.AdvanceAsync().ConfigureAwait(false);
                await session.StageAsync(progress).ConfigureAwait(false);
                if (!pruned && !advanced)
                    break;
            }
            session.PruneUnhelpful();
            session.DropNeedless();
            ct.ThrowIfCancellationRequested(); // Not a review after Cancel, even when the last steps didn't wait on anything.
        }
        catch (Exception)
        {
            // Cancelled, or failed: no download stays behind in the mods folder.
            Discard(manager, plan.Downloads);
            throw;
        }
        return plan;
    }

    /// <summary>
    /// Makes the selected changes of <paramref name="plan"/>, except the loader's (the instance's to save, see
    /// <see cref="ModFixPlan.Loader"/>), and deletes the downloads of the changes left out.
    /// </summary>
    /// <returns>The mods that couldn't be turned off (a file of that name already turned off is in the way…), and why.</returns>
    public static async Task<IReadOnlyList<(ContentFile File, Exception Error)>> ApplyFixesAsync(ContentManager manager, ModFixPlan plan)
    {
        foreach (var d in plan.Fixes.Where(f => !f.Selected).Select(f => f.Download).OfType<PlannedFile>().ToList())
            plan.Drop(d);
        var selected = plan.Fixes.Where(f => f.Selected).ToList();
        await CommitAsync(manager, plan.Downloads, selected.Where(f => f.Kind == ModFixKind.Enable).Select(f => f.File).OfType<ContentFile>()).ConfigureAwait(false);
        // One that won't go doesn't stop the rest (the downloads are in place already, and may need the others off).
        var failed = new List<(ContentFile, Exception)>();
        foreach (var file in selected.SelectMany(f => f.TurnedOff).Distinct())
        {
            if (!file.Enabled || !File.Exists(file.Path))
                continue;
            try
            {
                await manager.SetEnabledAsync(file, false).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((file, ex));
            }
        }
        return failed;
    }

    /// <summary>A way out of a problem, in the order they're tried (see <see cref="FixSession.Ladder"/>).</summary>
    private enum FixStep
    {
        EnableOff,
        PlatformDependency,
        InstallByModId,
        SwitchProvider,
        ProviderByModId,
        SwitchOther,
        Replace,
        ReplaceByModId,
        LoaderVersion,
        AddLoader,
        KeepNewest,
        DisableEither,
        DisableSubject,
    }

    /// <summary>One planning of fixes: what's been tried, and the downloads waiting to come in.</summary>
    private sealed class FixSession(ContentService service, ModFixPlan plan, Func<string, ILoaderProvider?>? loaders, CancellationToken ct)
    {
        private static readonly ContentLocation s_mods = new(ContentKind.Mod);

        /// <summary>When mods are for several loaders, the one added; first the most common.</summary>
        private static readonly string[] s_loaderPreference =
            [ComponentUids.FabricLoader, ComponentUids.NeoForge, ComponentUids.Forge, ComponentUids.QuiltLoader, ComponentUids.LiteLoader];

        /// <summary><c>identity#step</c>: each way out is tried once per problem.</summary>
        private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

        /// <summary>Versions downloaded once: one that didn't help isn't fetched again.</summary>
        private readonly HashSet<string> _triedVersions = new(StringComparer.Ordinal);

        private readonly List<ModFix> _pending = [];

        private ContentTarget Target => new(ContentKind.Mod, plan.MinecraftVersion, ModLoaderNames(plan.EffectiveLoaderUid, plan.MinecraftVersion));

        internal static FixStep[] Ladder(ModIssueKind kind) => kind switch
        {
            ModIssueKind.MissingDependency => [FixStep.EnableOff, FixStep.PlatformDependency, FixStep.InstallByModId, FixStep.DisableSubject],
            ModIssueKind.DependencyVersion => [FixStep.SwitchProvider, FixStep.ProviderByModId, FixStep.Replace, FixStep.DisableSubject],
            ModIssueKind.Incompatible => [FixStep.SwitchOther, FixStep.Replace, FixStep.DisableEither],
            ModIssueKind.WrongMinecraftVersion or ModIssueKind.WrongLoader => [FixStep.Replace, FixStep.ReplaceByModId, FixStep.DisableSubject],
            ModIssueKind.WrongLoaderVersion => [FixStep.LoaderVersion, FixStep.Replace, FixStep.DisableSubject],
            ModIssueKind.Duplicate => [FixStep.KeepNewest],
            ModIssueKind.OptiFineNeedsOptiFabric => [FixStep.InstallByModId, FixStep.DisableSubject],
            ModIssueKind.KnownConflict => [FixStep.DisableEither],
            ModIssueKind.NoLoader => [FixStep.AddLoader],
            _ => [],
        };

        /// <summary>What changes most of what else is wrong goes first: copies, then mods that can't load at all.</summary>
        private static int Priority(ModIssueKind kind) => kind switch
        {
            ModIssueKind.NoLoader => 0,
            ModIssueKind.Duplicate => 1,
            ModIssueKind.WrongLoader or ModIssueKind.WrongMinecraftVersion => 2,
            ModIssueKind.WrongLoaderVersion => 3,
            ModIssueKind.MissingDependency => 4,
            ModIssueKind.DependencyVersion => 5,
            _ => 6,
        };

        /// <summary>Changes whose problems are all still there didn't help: they go, and their problems try the next way out.</summary>
        public bool PruneUnhelpful()
        {
            var left = ModFixPlan.Identities(plan.Remaining());
            var unhelpful = plan.Fixes.Where(f => f.Issues.Count > 0 && f.Issues.All(i => left.Contains(ModFixPlan.Identity(i)))).ToList();
            foreach (var fix in unhelpful)
                plan.Withdraw(fix);
            return unhelpful.Count > 0;
        }

        /// <summary>
        /// Changes the others make needless go: installing OptiFabric when OptiFine is turned off anyway, turning off a
        /// mod that's swapped for a version that works. Turning off goes first, then what was found last.
        /// </summary>
        public void DropNeedless()
        {
            var order = plan.Fixes
                .Select((fix, i) => (Fix: fix, Index: i))
                .OrderBy(x => x.Fix.Kind is ModFixKind.Disable or ModFixKind.KeepOne ? 0 : 1)
                .ThenByDescending(x => x.Index)
                .Select(x => x.Fix)
                .ToList();
            var left = ModFixPlan.Identities(plan.Remaining());
            foreach (var fix in order)
            {
                var without = ModFixPlan.Identities(plan.Evaluate(plan.Fixes.Where(f => f.Selected && f != fix)));
                if (!without.IsSubsetOf(left))
                    continue;
                plan.Withdraw(fix);
                left = without;
            }
        }

        /// <summary>Each problem left takes its next way out. False when there was none.</summary>
        public async Task<bool> AdvanceAsync()
        {
            var problems = plan.Remaining().OrderBy(p => Priority(p.Kind)).ToList();
            // A change made this round can take a problem away (its mod turned off or swapped): it waits for the next
            // round, which sees whether it's still there, rather than spending its ways out on a mod that's gone.
            var inPlay = problems.SelectMany(p => new[] { p.File, p.OtherFile }).OfType<string>()
                .Where(name => Find(name) != default).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Gone(ModIssue i) => new[] { i.File, i.OtherFile }.Any(name => name is not null && inPlay.Contains(name) && Find(name) == default);
            var handled = new HashSet<string>(StringComparer.Ordinal);
            var any = false;
            foreach (var p in problems)
            {
                ct.ThrowIfCancellationRequested();
                if (handled.Contains(ModFixPlan.Identity(p)) || Gone(p))
                    continue;
                var group = GroupOf(p, problems).Where(g => !Gone(g)).ToList();
                handled.UnionWith(group.Select(ModFixPlan.Identity));
                foreach (var step in Ladder(p.Kind))
                {
                    if (group.Any(g => _tried.Contains(Key(g, step))))
                        continue;
                    _tried.UnionWith(group.Select(g => Key(g, step)));
                    if (await TryAsync(step, p, group).ConfigureAwait(false))
                    {
                        any = true;
                        break;
                    }
                }
            }
            return any;
        }

        private static string Key(ModIssue issue, FixStep step) => $"{ModFixPlan.Identity(issue)}#{step}";

        /// <summary>The problems one way out serves at once: every mod missing the same one, every copy, every mod needing a newer loader.</summary>
        private static List<ModIssue> GroupOf(ModIssue p, List<ModIssue> all) => p.Kind switch
        {
            ModIssueKind.MissingDependency => all.Where(i => i.Kind == p.Kind && string.Equals(i.OtherId, p.OtherId, StringComparison.OrdinalIgnoreCase)).ToList(),
            ModIssueKind.Duplicate => all.Where(i => i.Kind == p.Kind && string.Equals(i.ModId, p.ModId, StringComparison.OrdinalIgnoreCase)).ToList(),
            ModIssueKind.WrongLoaderVersion => all.Where(i => i.Kind == p.Kind).ToList(),
            _ => [p],
        };

        private async Task<bool> TryAsync(FixStep step, ModIssue p, List<ModIssue> group)
        {
            var edits = plan.Edits;
            try
            {
                return step switch
                {
                    FixStep.EnableOff => EnableOff(p.OtherId, group),
                    FixStep.PlatformDependency => await PlatformDependencyAsync(p.OtherId, group).ConfigureAwait(false),
                    FixStep.InstallByModId => await InstallByModIdAsync(p.Kind == ModIssueKind.OptiFineNeedsOptiFabric ? "optifabric" : p.OtherId, group).ConfigureAwait(false),
                    FixStep.SwitchProvider => await SwitchOtherAsync(p, inRange: true).ConfigureAwait(false),
                    FixStep.ProviderByModId => await ProviderByModIdAsync(p).ConfigureAwait(false),
                    FixStep.SwitchOther => await SwitchOtherAsync(p, inRange: false).ConfigureAwait(false),
                    FixStep.Replace => await ReplaceAsync(group).ConfigureAwait(false),
                    FixStep.ReplaceByModId => await ReplaceByModIdAsync(p).ConfigureAwait(false),
                    FixStep.LoaderVersion => await LoaderVersionAsync(group).ConfigureAwait(false),
                    FixStep.AddLoader => await AddLoaderAsync(p).ConfigureAwait(false),
                    FixStep.KeepNewest => KeepNewest(group),
                    FixStep.DisableEither => DisableEither(p),
                    FixStep.DisableSubject => DisableSubjects(group),
                    _ => false,
                };
            }
            catch (Exception ex) when (IsOffline(ex))
            {
                // What it found before the platform stopped answering stays; with nothing found, the next way out.
                return plan.Edits != edits;
            }
        }

        /// <summary>Offline, or a platform or loader site didn't answer (in time, or sensibly): not a reason to stop looking.</summary>
        private bool IsOffline(Exception ex) =>
            ex is HttpRequestException or IOException or InvalidDataException or System.Text.Json.JsonException or System.Xml.XmlException
            || (ex is TaskCanceledException && !ct.IsCancellationRequested);

        /// <summary>A project's versions for the target; null when the platform can't be reached (the next project is tried).</summary>
        private async Task<IReadOnlyList<RemoteVersion>?> VersionsAsync(IContentPlatform platform, string projectId)
        {
            try
            {
                return await platform.GetVersionsAsync(projectId, Target, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOffline(ex))
            {
                return null;
            }
        }

        // ── Finding the mods a problem is about ─────────────────────────────

        /// <param name="Installed">An installed mod (on, or turned on by <paramref name="Ours"/>).</param>
        /// <param name="Ours">The change that brought it: its download, or turning it on.</param>
        private readonly record struct Party(ContentFile? Installed, ModFix? Ours);

        private Party Find(string? fileName)
        {
            if (fileName is null)
                return default;
            foreach (var fix in plan.Fixes)
            {
                if (fix.Download is { } d && plan.Read.TryGetValue(d, out var read) && string.Equals(read.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    return new(null, fix);
            }
            foreach (var m in plan.Current)
            {
                if (m.IsDirectory || !string.Equals(m.FileName, fileName, StringComparison.OrdinalIgnoreCase) || plan.IsReplaced(m))
                    continue;
                if (m.Enabled && !plan.IsTurnedOff(m))
                    return new(m, null);
                if (!m.Enabled && plan.Fixes.FirstOrDefault(f => f.Kind == ModFixKind.Enable && ReferenceEquals(f.File, m)) is { } on)
                    return new(m, on);
            }
            return default;
        }

        /// <summary>An installed mod no change touches yet, that can be swapped for another version of itself.</summary>
        private ContentFile? Swappable(Party party) =>
            party is { Ours: null, Installed: { Record.Pinned: false } file } && !plan.IsTurnedOff(file) && !plan.IsReplaced(file) ? file : null;

        // ── Ways out ────────────────────────────────────────────────────────

        private bool EnableOff(string? id, List<ModIssue> group)
        {
            if (id is null)
                return false;
            var uid = plan.EffectiveLoaderUid;
            var off = plan.Current
                .Where(m => !m.Enabled && !m.IsDirectory && !plan.IsReplaced(m) && !plan.IsTurnedOn(m)
                    && m.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase)
                    // Not the twin of a mod that's on ("x.jar.disabled" next to "x.jar").
                    && !plan.Current.Any(o => o.Enabled && string.Equals(o.Key, m.Key, StringComparison.OrdinalIgnoreCase)))
                // One this loader loads comes first.
                .OrderByDescending(m => uid is not null && m.Metadata.Mods.Any(mod => ModChecker.Accepts(uid, plan.MinecraftVersion, mod)))
                .FirstOrDefault();
            if (off is null)
                return false;
            plan.Add(new ModFix { Kind = ModFixKind.Enable, File = off, Issues = group });
            return true;
        }

        /// <summary>The platform's required dependencies of the mods missing <paramref name="id"/> that aren't installed.</summary>
        private async Task<bool> PlatformDependencyAsync(string? id, List<ModIssue> group)
        {
            if (id is null)
                return false;
            foreach (var p in group)
            {
                var subject = Find(p.File);
                var version = subject.Ours?.Download?.Version;
                if (version is null && subject.Installed?.Record.Source is { } source && service._platforms.TryGetValue(source.Platform, out var own))
                    version = await TryGetVersionAsync(own, source.ProjectId, source.VersionId, ct).ConfigureAwait(false);
                if (version is null || !service._platforms.TryGetValue(version.Platform, out var platform))
                    continue;
                var candidates = new List<(string ProjectId, RemoteProject? Project)>();
                foreach (var dep in version.Dependencies.Where(d => d.Kind == RemoteDependencyKind.Required && d.ProjectId is not null))
                {
                    var projectId = dep.ProjectId!;
                    if (IsInstalled(platform.Platform, projectId) || IsPlanned(platform.Platform, projectId))
                        continue;
                    RemoteProject? project = null;
                    try
                    {
                        project = await platform.GetProjectAsync(projectId, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (IsOffline(ex))
                    {
                    }
                    candidates.Add((projectId, project));
                }
                // The one the missing mod id names; else all of them (they're required anyway), and reading them says.
                var named = candidates.Where(c => c.Project is { } pr && SlugCandidates(pr.Slug).Contains(id, StringComparer.OrdinalIgnoreCase)).ToList();
                var added = false;
                foreach (var (projectId, project) in named.Count > 0 ? named : candidates)
                {
                    if (await VersionsAsync(platform, projectId).ConfigureAwait(false) is { } versions && Best(versions) is { PrimaryFile: { } file } best)
                        added |= AddDownload(ModFixKind.Install, new PlannedFile(best, file, s_mods, IsDependency: true) { Project = project }, group);
                }
                if (added)
                    return true;
            }
            return false;
        }

        private async Task<bool> InstallByModIdAsync(string? id, List<ModIssue> group)
        {
            if (id is null || await service.FindByModIdAsync(id, Target, plan.Downloads, [], ct).ConfigureAwait(false) is not { } found)
                return false;
            // The project is installed already, in a version that doesn't provide it (another Minecraft version…):
            // this one takes its place, a copy that stays on first. One a change turns off stays turned off until the
            // swap works out (then turning it off is needless, see DropNeedless).
            var installed = plan.Current
                .Where(m => m.Enabled && m.Record.Source is { } s && s.Platform == found.Version.Platform && s.ProjectId == found.Version.ProjectId)
                .OrderBy(plan.IsTurnedOff)
                .FirstOrDefault();
            if (installed is null)
                return AddDownload(ModFixKind.Install, found, group);
            if (installed.Record.Pinned || plan.IsReplaced(installed) || SameFile(installed, found))
                return false;
            return AddDownload(ModFixKind.Replace, found with { ReplacesKey = installed.Key, IsDependency = installed.Record.IsDependency }, group, installed);
        }

        /// <summary>A dependency at the wrong version that's bundled in another mod, or wasn't installed from a platform: by its mod id.</summary>
        private async Task<bool> ProviderByModIdAsync(ModIssue p)
        {
            if (p.OtherId is not { } id)
                return false;
            var provider = Find(p.OtherFile);
            var own = provider.Installed is { } file && file.Metadata.Mods.Any(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
            if (provider.Ours is not null || (own && provider.Installed!.Record.Source?.Platform == ContentPlatform.Modrinth))
                return false; // Its own versions were looked at.
            if (await service.FindByModIdAsync(id, Target, plan.Downloads, [], ct).ConfigureAwait(false) is not { } found)
                return false;
            if (!own)
                return AddDownload(ModFixKind.Install, found, [p]); // The newest copy of an id wins: a standalone one does.
            if (Swappable(provider) is not { } swap || SameFile(swap, found))
                return false;
            return AddDownload(ModFixKind.Replace, found with { ReplacesKey = swap.Key, IsDependency = swap.Record.IsDependency }, [p], swap);
        }

        /// <summary>
        /// The other mod of a two-mod problem, at another version: one in the range the mod asks for
        /// (<paramref name="inRange"/>), or a newer one out of the range it declares itself incompatible with.
        /// </summary>
        private async Task<bool> SwitchOtherAsync(ModIssue p, bool inRange)
        {
            if (Swappable(Find(p.OtherFile)) is not { Record.Source: { } source } other
                || !other.Metadata.Mods.Any(m => string.Equals(m.Id, p.OtherId, StringComparison.OrdinalIgnoreCase)) // Bundled: not its own version.
                || !service._platforms.TryGetValue(source.Platform, out var platform))
                return false;
            var versions = Candidates(await platform.GetVersionsAsync(source.ProjectId, Target, ct).ConfigureAwait(false), source);
            var loader = p.Loader ?? ModLoaderKind.Fabric;
            List<RemoteVersion> pool;
            if (p.Range is { } range)
            {
                var sure = versions.Where(v => VersionPredicate.Matches(range, v.VersionNumber, loader) == inRange).ToList();
                // Version numbers platforms show don't always parse: then a newer one is the guess (and reading it says).
                pool = sure.Count > 0 ? sure : versions.Where(v => VersionPredicate.Matches(range, v.VersionNumber, loader) is null && Newer(v, source)).ToList();
            }
            else
            {
                pool = versions.Where(v => Newer(v, source)).ToList();
            }
            if (!inRange)
                pool = pool.Where(v => Newer(v, source)).ToList();
            if (Stablest(pool, source) is not { PrimaryFile: { } file } pick)
                return false;
            return AddDownload(ModFixKind.Replace, ReplacementFor(other, pick, file), [p], other);
        }

        /// <summary>
        /// The mod with the problem at another version: one for this Minecraft version and loader, or (when it's about
        /// another mod) a newer build, which may ask for or put up with what's installed.
        /// </summary>
        private async Task<bool> ReplaceAsync(List<ModIssue> group)
        {
            var added = false;
            foreach (var p in group)
            {
                if (Swappable(Find(p.File)) is not { Record.Source: { } source } file || !service._platforms.TryGetValue(source.Platform, out var platform)
                    || await VersionsAsync(platform, source.ProjectId).ConfigureAwait(false) is not { } all)
                    continue;
                var versions = Candidates(all, source);
                var pick = p.Kind is ModIssueKind.DependencyVersion or ModIssueKind.Incompatible
                    ? Stablest(versions.Where(v => Newer(v, source)).ToList(), source)
                    : PickBest(versions, Target);
                if (pick?.PrimaryFile is { } remote)
                    added |= AddDownload(ModFixKind.Replace, ReplacementFor(file, pick, remote), [p], file);
            }
            return added;
        }

        /// <summary>A mod not from Modrinth (added by hand, from CurseForge) for this Minecraft version and loader, by its mod id.</summary>
        private async Task<bool> ReplaceByModIdAsync(ModIssue p)
        {
            if (p.ModId is not { } id || Swappable(Find(p.File)) is not { } file || file.Record.Source?.Platform == ContentPlatform.Modrinth)
                return false;
            if (await service.FindByModIdAsync(id, Target, plan.Downloads, [], ct).ConfigureAwait(false) is not { } found || SameFile(file, found))
                return false;
            return AddDownload(ModFixKind.Replace, found with { ReplacesKey = file.Key, IsDependency = file.Record.IsDependency }, [p], file);
        }

        /// <summary>
        /// A newer loader version: the one that clears the most of <paramref name="group"/> and breaks nothing else
        /// (an old mod may cap it), stable builds and then the newest first. What it doesn't clear takes its next way
        /// out. When a change moves or adds the loader already (for mods that came in since), that change moves on.
        /// </summary>
        private async Task<bool> LoaderVersionAsync(List<ModIssue> group)
        {
            var existing = plan.Fixes.LastOrDefault(f => f.Loader is not null);
            var uid = existing?.Loader?.Uid ?? plan.LoaderUid;
            var from = existing?.Loader?.To ?? plan.LoaderVersion;
            if (uid is null || from is null || plan.MinecraftVersion is not { } mc || loaders?.Invoke(uid) is not { } provider)
                return false;
            var versions = await provider.GetVersionsAsync(mc, ct).ConfigureAwait(false);
            var others = plan.Fixes.Where(f => f.Selected && f != existing).ToList();
            var before = ModFixPlan.Identities(plan.Evaluate(plan.Fixes.Where(f => f.Selected)));
            ModFix Move(string to, IReadOnlyList<ModIssue> issues) => new()
            {
                Kind = existing?.Kind ?? ModFixKind.LoaderVersion,
                Loader = new LoaderChange(uid, existing?.Loader?.From ?? from, to)
                {
                    NeededBy = existing is { Kind: ModFixKind.AddLoader, Loader: { } added }
                        ? added.NeededBy
                        : issues.Where(i => i.Kind == ModIssueKind.WrongLoaderVersion).Select(i => i.Subject).Distinct().ToList(),
                },
                Issues = issues,
            };
            (string Version, List<ModIssue> Cleared)? best = null;
            foreach (var v in Ranked(versions.Where(v => LoaderVersionComparer.Instance.Compare(v.Version, from) > 0)))
            {
                var after = ModFixPlan.Identities(plan.Evaluate(others.Append(Move(v.Version, []))));
                if (!after.IsSubsetOf(before))
                    continue; // It breaks something else.
                var cleared = group.Where(g => !after.Contains(ModFixPlan.Identity(g))).ToList();
                if (cleared.Count > (best?.Cleared.Count ?? 0))
                    best = (v.Version, cleared);
                if (cleared.Count == group.Count)
                    break;
            }
            if (best is not { } pick)
                return false;
            var issues = existing?.Issues.Concat(pick.Cleared).ToList() ?? pick.Cleared;
            if (existing is not null)
                plan.Withdraw(existing);
            plan.Add(Move(pick.Version, issues));
            return true;
        }

        /// <summary>
        /// The loader most of the mods are for: its recommended build if they all take it, else the version that leaves
        /// the fewest of them out (stable builds and then the newest first).
        /// </summary>
        private async Task<bool> AddLoaderAsync(ModIssue p)
        {
            if (plan.LoaderUid is not null || plan.MinecraftVersion is not { } mc || loaders is null || plan.Fixes.Any(f => f.Loader is not null))
                return false;
            var mods = plan.Current.Where(m => m.Enabled && !m.IsDirectory && m.Metadata.Mods.Count > 0).ToList();
            var votes = mods
                .SelectMany(m => m.Metadata.Loaders.Select(UidFor).OfType<string>().Distinct())
                .GroupBy(u => u)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => Array.IndexOf(s_loaderPreference, g.Key))
                .Select(g => g.Key);
            var chosen = plan.Fixes.Where(f => f.Selected).ToList();
            foreach (var uid in votes)
            {
                if (loaders(uid) is not { } provider)
                    continue;
                var versions = await provider.GetVersionsAsync(mc, ct).ConfigureAwait(false);
                var recommended = versions.FirstOrDefault(v => v.Recommended);
                var forIt = mods.Where(m => m.Metadata.Loaders.Any(l => UidFor(l) == uid)).Select(m => m.DisplayName).Take(3).ToList();
                var order = Ranked(versions).ToList();
                if (recommended is not null)
                {
                    order.Remove(recommended);
                    order.Insert(0, recommended);
                }
                ModFix? best = null;
                var fewest = int.MaxValue;
                foreach (var v in order)
                {
                    var fix = new ModFix { Kind = ModFixKind.AddLoader, Loader = new LoaderChange(uid, "", v.Version) { NeededBy = forIt }, Issues = [p] };
                    var left = plan.Evaluate(chosen.Append(fix)).Count;
                    if (left < fewest)
                        (best, fewest) = (fix, left);
                    if (left == 0)
                        break;
                }
                if (best is null)
                    continue;
                plan.Add(best);
                return true;
            }
            return false;
        }

        /// <summary>Loader versions to try, stable builds and then the newest first.</summary>
        private static IEnumerable<LoaderVersion> Ranked(IEnumerable<LoaderVersion> versions) => versions
            .OrderBy(v => IsPreview(v.Version))
            .ThenByDescending(v => v.Version, LoaderVersionComparer.Instance)
            .Take(MaxLoaderCandidates);

        private static string? UidFor(ModLoaderKind kind) => kind switch
        {
            ModLoaderKind.Fabric => ComponentUids.FabricLoader,
            ModLoaderKind.Quilt => ComponentUids.QuiltLoader,
            ModLoaderKind.Forge or ModLoaderKind.LegacyForge => ComponentUids.Forge,
            ModLoaderKind.NeoForge => ComponentUids.NeoForge,
            ModLoaderKind.LiteLoader => ComponentUids.LiteLoader,
            _ => null,
        };

        /// <summary>Copies of a mod: the newest version stays (then one from a platform, then the latest file), the user can pick another.</summary>
        private bool KeepNewest(List<ModIssue> group)
        {
            var copies = new List<ContentFile>();
            foreach (var name in group.SelectMany(g => new[] { g.File, g.OtherFile }))
            {
                var party = Find(name);
                if (party.Ours is { } ours)
                {
                    plan.Withdraw(ours); // A copy a change brought: that change goes, the installed one stays.
                    return true;
                }
                if (party.Installed is { } m && !copies.Contains(m))
                    copies.Add(m);
            }
            if (copies.Count < 2)
                return false;
            var id = group[0].ModId;
            copies.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.FileName, b.FileName));
            var keep = copies
                .OrderByDescending(m => m.Metadata.Mods.FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))?.Version ?? m.Version, s_versions)
                .ThenByDescending(m => m.Record.Source is not null)
                .ThenByDescending(m => m.ModifiedUtc)
                .First();
            plan.Add(new ModFix { Kind = ModFixKind.KeepOne, Choices = copies, Choice = copies.IndexOf(keep), Issues = group });
            return true;
        }

        private static readonly Comparer<string?> s_versions = Comparer<string?>.Create((a, b) =>
            (LooseVersion.TryParse(a, out var x), LooseVersion.TryParse(b, out var y)) switch
            {
                (true, true) => x.CompareTo(y),
                (true, false) => 1,
                (false, true) => -1,
                _ => 0,
            });

        /// <summary>
        /// One of two mods that don't work together is turned off: OptiFine before its replacements; otherwise the one
        /// fewer mods need, then the one added last. The user can pick the other.
        /// </summary>
        private bool DisableEither(ModIssue p)
        {
            var a = Find(p.File);
            var b = Find(p.OtherFile);
            if ((a.Ours ?? b.Ours) is { } ours)
            {
                plan.Withdraw(ours); // A change brought it: that change goes.
                return true;
            }
            if (a.Installed is not { } subject || b.Installed is not { } other || ReferenceEquals(subject, other))
                return false;
            var choice = 0;
            if (p.Kind != ModIssueKind.KnownConflict)
            {
                var (needA, needB) = (Dependents(subject), Dependents(other));
                choice = needA != needB ? (needA < needB ? 0 : 1) : other.ModifiedUtc > subject.ModifiedUtc ? 1 : 0;
            }
            plan.Add(new ModFix { Kind = ModFixKind.Disable, Choices = [subject, other], Choice = choice, Issues = [p] });
            return true;
        }

        /// <summary>How many other mods that are on need something <paramref name="file"/> provides.</summary>
        private int Dependents(ContentFile file)
        {
            var ids = file.Metadata.ProvidedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return plan.Current.Count(m => m != file && m.Enabled && m.Metadata.Mods.Any(mod =>
                mod.Dependencies.Any(d => d.Kind == DependencyKind.Required && ids.Contains(d.Id))));
        }

        /// <summary>The last way out: what can't work is turned off (or, when a change brought it, that change goes).</summary>
        private bool DisableSubjects(List<ModIssue> group)
        {
            var any = false;
            foreach (var p in group)
            {
                var party = Find(p.File);
                if (party.Ours is { } ours)
                {
                    plan.Withdraw(ours);
                    any = true;
                }
                else if (party.Installed is { } file && !plan.IsTurnedOff(file))
                {
                    plan.Add(new ModFix { Kind = ModFixKind.Disable, Choices = [file], Issues = [p] });
                    any = true;
                }
            }
            return any;
        }

        // ── Downloads ───────────────────────────────────────────────────────

        private bool AddDownload(ModFixKind kind, PlannedFile download, IReadOnlyList<ModIssue> issues, ContentFile? replaced = null)
        {
            if (download.IsBlocked || !_triedVersions.Add(download.Version.Id))
                return false;
            var fix = new ModFix { Kind = kind, Download = download, File = replaced, Issues = issues };
            plan.Add(fix);
            _pending.Add(fix);
            return true;
        }

        /// <summary>Downloads what this round added and reads it; what didn't come in, or isn't what it should be, goes.</summary>
        public async Task StageAsync(IProgress<DownloadProgress>? progress)
        {
            if (_pending.Count == 0)
                return;
            try
            {
                await service.StageAsync(plan.Manager, plan.Downloads, progress, ct).ConfigureAwait(false);
            }
            catch (DownloadFailedException ex)
            {
                // What came in stays. What didn't is left out below, and the problems it was for take their next way out.
                var failed = ex.Failures.Select(f => Path.GetFullPath(f.Item.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var fix in _pending.Where(plan.Fixes.Contains))
                {
                    if (!failed.Contains(Path.GetFullPath(PartPath(plan.Manager, fix.Download!))))
                        plan.Downloads.Staged.Add(fix.Download!);
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
            {
                // All left out below.
            }
            foreach (var fix in _pending)
            {
                if (!plan.Fixes.Contains(fix))
                    continue; // Taken out meanwhile.
                var d = fix.Download!;
                if (!plan.Downloads.Staged.Contains(d))
                {
                    plan.Withdraw(fix);
                    continue;
                }
                var read = Read(d, fix.File);
                // Found by a mod id: kept only if it really is that mod.
                if (d.MustProvide is { } id && !read.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                    plan.Withdraw(fix);
                else
                    plan.Read[d] = read;
            }
            _pending.Clear();
        }

        /// <summary>A download as the file it will be once in place (a replaced mod that's off stays off).</summary>
        private ContentFile Read(PlannedFile d, ContentFile? replaced)
        {
            var target = d.TargetPath(plan.Manager.GameDirectory);
            return new ContentFile
            {
                Path = replaced is { Enabled: false } ? target + ContentLocation.DisabledSuffix : target,
                Key = ContentManager.KeyFor(d.Location, d.File.FileName),
                Location = d.Location,
                Metadata = ContentMetadataReader.Read(PartPath(plan.Manager, d)),
                Record = new ContentRecord
                {
                    Title = d.Project?.Title is { Length: > 0 } title ? title : replaced?.Record.Title,
                    Source = new RemoteRef
                    {
                        Platform = d.Version.Platform,
                        ProjectId = d.Version.ProjectId,
                        VersionId = d.Version.Id,
                        VersionNumber = d.Version.VersionNumber,
                        Published = d.Version.Published,
                        Channel = d.Version.Channel,
                    },
                },
                Size = d.File.Size,
            };
        }

        // ── Helpers ─────────────────────────────────────────────────────────

        private bool IsInstalled(ContentPlatform platform, string projectId) =>
            plan.Current.Any(m => m.Record.Source is { } s && s.Platform == platform && s.ProjectId == projectId);

        private bool IsPlanned(ContentPlatform platform, string projectId) =>
            plan.Downloads.Files.Any(f => f.Version.Platform == platform && f.Version.ProjectId == projectId);

        /// <summary>The versions for the target other than the installed one, that can be downloaded and weren't tried.</summary>
        private List<RemoteVersion> Candidates(IEnumerable<RemoteVersion> versions, RemoteRef installed) =>
            Compatible(versions, Target).Where(v => v.Id != installed.VersionId && v.PrimaryFile?.Url is not null && !_triedVersions.Contains(v.Id)).ToList();

        private RemoteVersion? Best(IEnumerable<RemoteVersion> versions) =>
            PickBest(versions.Where(v => v.PrimaryFile?.Url is not null && !_triedVersions.Contains(v.Id)), Target);

        /// <summary>The newest of <paramref name="pool"/>, as stable as the installed file when one is.</summary>
        private RemoteVersion? Stablest(List<RemoteVersion> pool, RemoteRef installed)
        {
            var stable = pool.Where(v => v.Channel <= installed.Channel).ToList();
            return Newest(stable.Count > 0 ? stable : pool, Target);
        }

        private static bool Newer(RemoteVersion v, RemoteRef installed) => installed.Published is not { } at || v.Published > at;

        private static bool SameFile(ContentFile installed, PlannedFile found) =>
            found.File.Sha1 is { } sha1 && installed.Record.Sha1 is { } have && string.Equals(sha1, have, StringComparison.OrdinalIgnoreCase);

        private static PlannedFile ReplacementFor(ContentFile file, RemoteVersion version, RemoteFile remote) =>
            new(version, remote, file.Location, IsDependency: file.Record.IsDependency)
            {
                ReplacesKey = file.Key,
                Project = new RemoteProject(version.Platform, version.ProjectId, "", file.DisplayName) { IconUrl = file.Record.IconUrl, WebUrl = file.Record.ProjectUrl },
            };
    }
}
