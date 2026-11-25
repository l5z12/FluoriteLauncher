using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Platforms;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Fluorite.App.Services;

/// <summary>An install/update in progress, observable by cards, content tabs and the Activity page.</summary>
public sealed partial class ContentJob(string title, GameInstance instance, string? projectKey) : ProgressItem
{
    public string Title { get; } = title;
    public GameInstance Instance { get; } = instance;
    public string InstanceName => Instance.Name;

    /// <summary><c>Platform:ProjectId</c> of the project being installed (null for bulk updates).</summary>
    public string? ProjectKey { get; } = projectKey;

    [ObservableProperty] public partial bool IsActive { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRetry))]
    public partial bool Failed { get; set; }

    private readonly CancellationTokenSource _cancel = new();

    public CancellationToken CancellationToken => _cancel.Token;

    /// <summary>While files download: Cancel stops it before anything in the instance changes.</summary>
    [ObservableProperty] public partial bool CanCancel { get; set; }

    public void Cancel()
    {
        CanCancel = false;
        Status = Loc.Get("Content_Cancelling");
        _cancel.Cancel();
    }

    /// <summary>Runs the same install or update again (set when it can be).</summary>
    public Func<Task>? Retry { get; set; }

    public bool CanRetry => Failed && Retry is not null;
}

/// <summary>
/// UI flows around <see cref="ContentService"/>: pick a version, confirm dependencies, download with progress, walk the
/// user through files whose authors block third-party downloads, and announce changes so content tabs refresh.
/// </summary>
public sealed partial class ContentInstallService(ContentService content)
{
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<ContentJob> Jobs { get; } = [];

    /// <summary>Raised on the UI thread with the instance's game directory after its content changed.</summary>
    public event EventHandler<string>? ContentChanged;

    /// <summary>
    /// Raised on the UI thread with the instance's id after its dismissed mod issues changed elsewhere: dismissed at
    /// launch, or forgotten there as fixed.
    /// </summary>
    public event EventHandler<string>? DismissalsChanged;

    public void NotifyDismissalsChanged(GameInstance instance) => DismissalsChanged?.Invoke(this, instance.Id);

    public static string Key(ContentPlatform platform, string projectId) => $"{platform}:{projectId}";

    public ContentJob? ActiveJob(string projectKey, GameInstance instance) =>
        Jobs.FirstOrDefault(j => j.IsActive && j.ProjectKey == projectKey && j.Instance.GameDirectory == instance.GameDirectory);

    /// <summary>Projects installed in <paramref name="instance"/> for <paramref name="kind"/>, as <see cref="Key"/>s.</summary>
    public async Task<HashSet<string>> InstalledProjectsAsync(GameInstance instance, ContentKind kind)
    {
        var records = await content.For(instance).GetRecordsAsync();
        var prefix = kind == ContentKind.DataPack ? "saves/" : new ContentLocation(kind).RelativeFolder + "/";
        return records
            .Where(r => r.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (kind != ContentKind.DataPack || r.Key.Contains("/datapacks/", StringComparison.OrdinalIgnoreCase)))
            .Select(r => r.Value.Source)
            .OfType<RemoteRef>()
            .Select(s => Key(s.Platform, s.ProjectId))
            .ToHashSet();
    }

    // ── Install ─────────────────────────────────────────────────────────

    /// <param name="version">Exact version, or null for the best one for the instance.</param>
    public async Task<bool> InstallAsync(GameInstance instance, RemoteProject project, RemoteVersion? version, ContentKind kind)
    {
        var platform = content.Get(project.Platform);
        var manager = content.For(instance);
        var location = await LocationAsync(manager, kind);
        if (location is null)
            return false;
        var target = ContentService.TargetFor(instance.Manifest, kind);
        var job = Start(project.Title, instance, Key(project.Platform, project.Id));
        job.Retry = () => InstallAsync(instance, project, version, kind);
        InstallPlan? plan = null;
        try
        {
            if (version is null)
            {
                job.Status = Loc.Get("Content_FindingVersion");
                version = ContentService.PickBest(await platform.GetVersionsAsync(project.Id, target), target);
                if (version is null)
                {
                    var message = Loc.Format("Content_NoCompatibleVersion", project.Title, Describe(target));
                    Finish(job, message, failed: true);
                    await Dialogs.ShowMessageAsync(Loc.Get("Content_InstallFailedTitle"), message);
                    return false;
                }
            }

            job.Status = Loc.Get("Content_ResolvingDependencies");
            var installedIds = kind == ContentKind.Mod ? await InstalledModIdsAsync(manager) : null;
            var planned = await Task.Run(() => content.PlanAsync(manager, target, version, location, project, installedIds));
            plan = planned;
            if (planned.Files.Count == 0 && planned.AlreadyInstalled.Count > 0)
            {
                Finish(job, Loc.Get("Content_AlreadyInstalled"));
                return true;
            }
            var title = Loc.Format("Content_ConfirmTitle", project.Title);
            if (NeedsConfirmation(planned) && !await ConfirmAsync(title, planned))
            {
                Finish(job, Loc.Get("Content_Cancelled"));
                return false;
            }
            return await RunAsync(job, instance, kind, target, manager, planned, title, update: false) is not null;
        }
        catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (plan is not null)
                ContentService.Discard(manager, plan);
            Finish(job, ex.Message, failed: true);
            await Dialogs.ShowMessageAsync(Loc.Get("Content_InstallFailedTitle"), ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Updates <paramref name="updates"/> together with what they need: new dependencies, newer versions of installed
    /// mods, turned-off mods turned back on, a newer loader. The new mods' own manifests are checked against the
    /// others before anything is placed, and updates that wouldn't work can be skipped.
    /// </summary>
    /// <returns>The updates that went in; null when nothing changed (cancelled or failed).</returns>
    public async Task<IReadOnlyList<ContentUpdate>?> UpdateAsync(GameInstance instance, ContentKind kind, IReadOnlyList<ContentUpdate> updates)
    {
        if (updates.Count == 0)
            return [];
        var manager = content.For(instance);
        var target = ContentService.TargetFor(instance.Manifest, kind);
        var job = Start(updates.Count == 1 ? updates[0].File.DisplayName : Loc.Format("Content_UpdatingN", updates.Count), instance,
            updates.Count == 1 ? Key(updates[0].Latest.Platform, updates[0].Latest.ProjectId) : null);
        // Something to go back to if the new versions don't get along.
        job.Status = Loc.Get("Snapshot_Taking");
        await App.Get<SnapshotService>().TakeBeforeUpdateAsync(instance,
            updates.Count == 1 ? Loc.Format("Snapshot_BeforeUpdateOne", updates[0].File.DisplayName) : Loc.Format("Snapshot_BeforeUpdateMany", updates.Count));
        job.Retry = () => UpdateAsync(instance, kind, updates);
        InstallPlan? plan = null;
        try
        {
            job.Status = Loc.Get("Content_ResolvingDependencies");
            var installedIds = kind == ContentKind.Mod ? await InstalledModIdsAsync(manager) : null;
            var planned = await Task.Run(() => content.PlanUpdatesAsync(manager, target, updates, installedIds));
            plan = planned;
            var skipped = await RunAsync(job, instance, kind, target, manager, planned, Loc.Format("Content_ConfirmUpdateTitle", updates.Count), update: true);
            if (skipped is null)
                return null;
            var left = skipped.Where(f => !f.IsDependency).Select(f => f.ProjectKey).ToHashSet();
            return updates.Where(u => !left.Contains(u.ProjectKey)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (plan is not null)
                ContentService.Discard(manager, plan);
            Finish(job, ex.Message, failed: true);
            await Dialogs.ShowMessageAsync(Loc.Get("Content_InstallFailedTitle"), ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Downloads the plan next to where it goes, checks the new mods against the others (fixing what it can), asks
    /// about anything the user hasn't seen yet or that wouldn't work, then puts everything in place.
    /// </summary>
    /// <returns>The files left out (skipped); null when nothing changed.</returns>
    private async Task<IReadOnlyList<PlannedFile>?> RunAsync(ContentJob job, GameInstance instance, ContentKind kind, ContentTarget target,
        ContentManager manager, InstallPlan plan, string title, bool update)
    {
        job.Status = Loc.Get("Content_Downloading");
        var progress = new Progress<DownloadProgress>(job.Report);
        InstallReview? review = null;
        job.CanCancel = true;
        try
        {
            await Task.Run(() => content.StageAsync(manager, plan, progress, job.CancellationToken));
            if (kind == ContentKind.Mod)
            {
                job.ClearDownload(indeterminate: true);
                job.Status = Loc.Get("Content_Checking");
                var loaders = LoaderFor(instance);
                review = await Task.Run(() => content.ReviewAsync(manager, plan, target, instance.Manifest, loaders, progress, job.CancellationToken));
            }
        }
        catch (OperationCanceledException) when (job.CancellationToken.IsCancellationRequested)
        {
            ContentService.Discard(manager, plan);
            Finish(job, Loc.Get("Content_CancelledNothingChanged"));
            return null;
        }
        finally
        {
            job.CanCancel = false;
        }

        var withLoader = review?.Loader is not null;
        IReadOnlyList<PlannedFile> skipped = [];
        if (NeedsReview(plan, review, update))
        {
            var (choice, loaderChosen) = await ReviewDialogAsync(title, plan, review, update);
            if (choice == ReviewChoice.Cancel)
            {
                ContentService.Discard(manager, plan);
                Finish(job, Loc.Get("Content_Cancelled"));
                return null;
            }
            withLoader = loaderChosen;
            if (choice == ReviewChoice.Skip && review is not null)
                skipped = review.Skip(withLoader);
        }

        var blocked = await Task.Run(() => ContentService.CommitAsync(manager, plan, review?.Enable.Select(e => e.File)));
        var status = skipped.Count(f => !f.IsDependency) is var n and > 0 ? Loc.Format("Content_InstalledSkipped", n) : Loc.Get("Content_Installed");
        if (withLoader && review?.Loader is { } change && await ApplyLoaderAsync(instance, change) is { } loaderNote)
            status += " · " + loaderNote;
        if (blocked.Count > 0)
            await ManualDownloadsAsync(manager, blocked);
        Finish(job, status);
        ContentChanged?.Invoke(this, instance.GameDirectory);
        return skipped;
    }

    /// <summary>The instance's loader, when its version may change (not for another launcher's read-only instance).</summary>
    private static Modding.Loaders.ILoaderProvider? LoaderFor(GameInstance instance) =>
        instance.CanEditComponents && ComponentResolver.PrimaryLoader(instance.Manifest) is { } loader
            ? App.Get<Modding.Loaders.LoaderService>().Providers.FirstOrDefault(p => p.Uid == loader.Uid && p.Uid != ComponentUids.OptiFine)
            : null;

    /// <summary>Raised on the UI thread with the instance whose loader version an install changed.</summary>
    public event EventHandler<GameInstance>? ComponentsChanged;

    /// <summary>
    /// Moves the instance to the loader version the mods need, or adds the loader to an instance without one (an
    /// empty <see cref="LoaderChange.From"/>); it's installed on the next launch. Returns what changed, or null.
    /// </summary>
    internal async Task<string?> ApplyLoaderAsync(GameInstance instance, LoaderChange change)
    {
        var component = instance.Manifest.Components.FirstOrDefault(c => c.Uid == change.Uid);
        var added = component is null && change.From.Length == 0 && ComponentResolver.PrimaryLoader(instance.Manifest) is null;
        if (added)
        {
            component = new ComponentRef { Uid = change.Uid, Version = change.From };
            instance.Manifest.Components.Add(component);
        }
        if (component is null || component.Version != change.From)
            return null;
        var profile = component.ProfileId;
        component.Version = change.To;
        component.ProfileId = null;
        try
        {
            await App.Get<InstanceRegistry>().SaveAsync(instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            if (added)
                instance.Manifest.Components.Remove(component);
            component.Version = change.From;
            component.ProfileId = profile;
            return null;
        }
        ComponentsChanged?.Invoke(this, instance);
        return Loc.Format(added ? "Content_LoaderAdded" : "Content_LoaderChanged", LoaderName(change.Uid), change.To);
    }

    internal static string LoaderName(string uid) =>
        App.Get<Modding.Loaders.LoaderService>().Providers.FirstOrDefault(p => p.Uid == uid)?.DisplayName ?? uid;

    /// <summary>Copies local files (drag and drop / file picker) into the instance.</summary>
    public async Task<int> ImportAsync(GameInstance instance, ContentLocation location, IEnumerable<string> paths)
    {
        var manager = content.For(instance);
        var count = 0;
        foreach (var path in paths.Where(p => File.Exists(p) && location.Accepts(Path.GetFileName(p))))
        {
            await manager.ImportAsync(path, location);
            count++;
        }
        if (count > 0)
            ContentChanged?.Invoke(this, instance.GameDirectory);
        return count;
    }

    public void NotifyChanged(GameInstance instance) => ContentChanged?.Invoke(this, instance.GameDirectory);

    // ── Helpers ─────────────────────────────────────────────────────────

    internal ContentJob Start(string title, GameInstance instance, string? projectKey)
    {
        var job = new ContentJob(title, instance, projectKey) { Status = Loc.Get("Content_Preparing") };
        Jobs.Insert(0, job);
        while (Jobs.Count > 50)
            Jobs.RemoveAt(Jobs.Count - 1);
        return job;
    }

    internal static void Finish(ContentJob job, string status, bool failed = false)
    {
        job.ClearDownload(indeterminate: false);
        job.Status = status;
        job.Failed = failed;
        job.IsActive = false;
        job.Progress = failed ? 0 : 100;
    }

    private static async Task<IReadOnlySet<string>> InstalledModIdsAsync(ContentManager manager)
    {
        var files = await Task.Run(() => manager.ScanAsync(new ContentLocation(ContentKind.Mod)));
        return files.Where(f => f.Enabled).SelectMany(f => f.Metadata.ProvidedIds).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static string Describe(ContentTarget target) =>
        string.Join(' ', new[] { target.MinecraftVersion, target.Kind == ContentKind.Mod ? string.Join('/', target.Loaders) : null }.Where(s => !string.IsNullOrEmpty(s)));

    private static bool NeedsConfirmation(InstallPlan plan) =>
        plan.Dependencies.Any() || plan.Unresolved.Count > 0 || plan.Incompatible.Count > 0 || plan.Blocked.Any();

    private static async Task<bool> ConfirmAsync(string title, InstallPlan plan)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 440 };
        var main = plan.Files.Where(f => !f.IsDependency).ToList();
        if (main.Count > 0)
            panel.Children.Add(Dialogs.Bullets(main.Select(f => $"{f.Title} {f.Version.VersionNumber}")));
        if (plan.Dependencies.Any())
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Content_AlsoInstalls")));
            panel.Children.Add(Dialogs.Bullets(plan.Dependencies.Select(f => $"{f.Title} {f.Version.VersionNumber}")));
        }
        if (plan.Optional.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Content_OptionalNotInstalled")));
            panel.Children.Add(Dialogs.Bullets(plan.Optional.Select(p => p.Title)));
        }
        if (plan.Blocked.Any())
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_ManualNeeded", string.Join(", ", plan.Blocked.Select(b => b.Title))), InfoBarSeverity.Informational));
        if (plan.Incompatible.Count > 0)
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_IncompatibleWith", string.Join(", ", plan.Incompatible)), InfoBarSeverity.Warning));
        if (plan.Unresolved.Count > 0)
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_Unresolved", string.Join(", ", plan.Unresolved)), InfoBarSeverity.Error));

        var result = await Dialogs.ShowAsync(title, Dialogs.Scroll(panel, 460), Loc.Get("Content_Install"));
        return result == ContentDialogResult.Primary;
    }

    private enum ReviewChoice
    {
        Cancel,
        Apply,
        Skip,
    }

    /// <summary>
    /// Whether to ask before placing: for an update, anything beyond the updates themselves; for an install (whose
    /// dependencies were confirmed before downloading), only what the check found.
    /// </summary>
    private static bool NeedsReview(InstallPlan plan, InstallReview? review, bool update) =>
        (review is not null && (review.Added.Count > 0 || review.Enable.Count > 0 || review.Loader is not null || review.Problems(withLoader: false).Count > 0))
        || (update && NeedsConfirmation(plan));

    /// <summary>
    /// Everything the install or update will do, what the new mods' manifests say wouldn't work, and the way out:
    /// a newer loader (ticked), skipping what has problems (with what came with it), or going ahead anyway.
    /// </summary>
    private static async Task<(ReviewChoice Choice, bool WithLoader)> ReviewDialogAsync(string title, InstallPlan plan, InstallReview? review, bool update)
    {
        var panel = new StackPanel { Spacing = 6, MinWidth = 460 };
        var main = plan.Files.Where(f => !f.IsDependency).ToList();
        if (main.Count > 0)
            panel.Children.Add(Dialogs.Bullets(main.Select(f => $"{f.Title} {f.Version.VersionNumber}")));
        string For(IEnumerable<string> roots, string text) =>
            plan.TitlesOf(roots) is { Count: > 0 } by ? Loc.Format("Review_For", text, string.Join(", ", by)) : text;
        var updates = plan.Dependencies.Where(f => f.ReplacesKey is not null).ToList();
        if (updates.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Review_AlsoUpdates")));
            panel.Children.Add(Dialogs.Bullets(updates.Select(f => For(f.NeededBy, $"{f.Title} {f.Version.VersionNumber}"))));
        }
        var installs = plan.Dependencies.Where(f => f.ReplacesKey is null).ToList();
        if (installs.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Content_AlsoInstalls")));
            panel.Children.Add(Dialogs.Bullets(installs.Select(f => For(f.NeededBy, $"{f.Title} {f.Version.VersionNumber}"))));
        }
        if (review is { Enable.Count: > 0 })
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Review_AlsoEnables")));
            panel.Children.Add(Dialogs.Bullets(review.Enable.Select(e => For(e.NeededBy, e.File.DisplayName))));
        }
        if (plan.Optional.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("Content_OptionalNotInstalled")));
            panel.Children.Add(Dialogs.Bullets(plan.Optional.Select(p => p.Title)));
        }
        if (plan.Blocked.Any())
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_ManualNeeded", string.Join(", ", plan.Blocked.Select(b => b.Title))), InfoBarSeverity.Informational));
        if (plan.Incompatible.Count > 0)
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_IncompatibleWith", string.Join(", ", plan.Incompatible)), InfoBarSeverity.Warning));
        if (plan.Unresolved.Count > 0)
            panel.Children.Add(Dialogs.Bar(Loc.Format("Content_Unresolved", string.Join(", ", plan.Unresolved)), InfoBarSeverity.Error));

        CheckBox? loaderBox = null;
        if (review?.Loader is { } change)
        {
            loaderBox = new CheckBox { IsChecked = true, Content = Loc.Format("Review_Loader", LoaderName(change.Uid), change.From, change.To), Margin = new Thickness(0, 8, 0, 0) };
            panel.Children.Add(loaderBox);
            panel.Children.Add(Dialogs.Text(Loc.Format("Review_LoaderNeededBy", string.Join(", ", change.NeededBy)), secondary: true));
        }
        var problemsHost = new StackPanel { Spacing = 6 };
        panel.Children.Add(problemsHost);

        var go = update ? Loc.Get("Review_Update") : Loc.Get("Content_Install");
        var dialog = Dialogs.Create(title, Dialogs.Scroll(panel, 460), go);
        var skipping = false;
        void Refresh()
        {
            var withLoader = loaderBox?.IsChecked == true;
            var problems = review?.Problems(withLoader) ?? [];
            problemsHost.Children.Clear();
            skipping = false;
            dialog.SecondaryButtonText = "";
            dialog.PrimaryButtonText = go;
            dialog.DefaultButton = ContentDialogButton.Primary;
            if (problems.Count == 0)
                return;
            problemsHost.Children.Add(Dialogs.Header(Loc.Get("Review_Problems")));
            problemsHost.Children.Add(Dialogs.Bullets(problems.Select(p => DescribeForReview(p.Issue)).Distinct()));
            var left = update && review is not null ? review.SkipSet(withLoader).Where(f => !f.IsDependency).ToList() : [];
            if (left.Count > 0 && left.Count < main.Count)
            {
                // The safe way out is the default: the rest still updates.
                skipping = true;
                problemsHost.Children.Add(Dialogs.Bar(Loc.Format("Review_SkipLeavesOut", string.Join(", ", left.Select(f => f.Title))), InfoBarSeverity.Informational));
                dialog.PrimaryButtonText = Loc.Format("Review_SkipN", left.Count);
                dialog.SecondaryButtonText = Loc.Get("Review_UpdateAnyway");
            }
            else
            {
                // Nothing to leave out but everything: going ahead has to be chosen, not Enter.
                dialog.PrimaryButtonText = update ? Loc.Get("Review_UpdateAnyway") : Loc.Get("Review_InstallAnyway");
                dialog.DefaultButton = ContentDialogButton.Close;
            }
        }
        if (loaderBox is not null)
        {
            loaderBox.Checked += (_, _) => Refresh();
            loaderBox.Unchecked += (_, _) => Refresh();
        }
        Refresh();
        var result = await Dialogs.ShowAsync(dialog);
        var loader = loaderBox?.IsChecked == true;
        return result switch
        {
            ContentDialogResult.Primary => (skipping ? ReviewChoice.Skip : ReviewChoice.Apply, loader),
            ContentDialogResult.Secondary => (ReviewChoice.Apply, loader),
            _ => (ReviewChoice.Cancel, loader),
        };
    }

    /// <summary>Like <see cref="Describe"/>, without pointing at the Version tab (the dialog offers the loader itself).</summary>
    internal static string DescribeForReview(ModIssue issue) => issue.Kind == ModIssueKind.WrongLoaderVersion
        ? Loc.Format("Review_WrongLoaderVersion", issue.Subject, LoaderDisplayName(issue.Other), issue.Detail ?? "")
        : Describe(issue);

    /// <summary>Data packs live in a world: ask which one.</summary>
    private static async Task<ContentLocation?> LocationAsync(ContentManager manager, ContentKind kind)
    {
        if (kind != ContentKind.DataPack)
            return new ContentLocation(kind);
        var worlds = manager.GetWorlds();
        if (worlds.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("DataPack_NoWorldsTitle"), Loc.Get("DataPack_NoWorlds"));
            return null;
        }
        if (worlds.Count == 1)
            return new ContentLocation(kind, worlds[0]);
        var combo = new ComboBox { ItemsSource = new ObservableCollection<string>(worlds), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch, Header = Loc.Get("DataPack_World") };
        var result = await Dialogs.ShowAsync(Loc.Get("DataPack_PickWorldTitle"), combo, Loc.Get("Content_Install"));
        return result == ContentDialogResult.Primary && combo.SelectedItem is string w ? new ContentLocation(kind, w) : null;
    }

    // ── Files the author only allows downloading from the website ──────

    private sealed partial class ManualRow(PlannedFile file) : ObservableObject
    {
        public PlannedFile File { get; } = file;
        [ObservableProperty] public partial string Status { get; set; } = Loc.Get("Manual_Waiting");
        public bool Done { get; set; }
        public bool Busy { get; set; }
    }

    /// <summary>
    /// Opens each blocked file's download page and watches the Downloads folder; files that arrive (and match the
    /// expected hash) are installed automatically, and the dialog closes once all are in.
    /// </summary>
    internal async Task ManualDownloadsAsync(ContentManager manager, IReadOnlyList<PlannedFile> blocked)
    {
        var downloads = KnownFolders.Downloads;
        var rows = blocked.Select(b => new ManualRow(b)).ToList();

        var panel = new StackPanel { Spacing = 10, MinWidth = 520 };
        panel.Children.Add(Dialogs.Text(Loc.Format("Manual_Body", downloads), secondary: true));
        foreach (var row in rows)
        {
            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = row.File.Title, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = row.File.File.FileName, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], TextTrimming = TextTrimming.CharacterEllipsis });
            var status = new TextBlock { Text = row.Status, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"], Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] };
            row.PropertyChanged += (_, _) => status.Text = row.Status;
            text.Children.Add(status);
            var open = new Button { Content = Loc.Get("Manual_OpenPage"), VerticalAlignment = VerticalAlignment.Center };
            open.Click += (_, _) => HtmlContent.OpenExternal(row.File.File.ManualDownloadUrl);
            Grid.SetColumn(open, 1);
            grid.Children.Add(text);
            grid.Children.Add(open);
            panel.Children.Add(grid);
        }

        var dialog = Dialogs.Create(Loc.Get("Manual_Title"), Dialogs.Scroll(panel, 460), close: Loc.Get("Manual_Skip"));

        async Task TryAsync(string path)
        {
            var name = Path.GetFileName(path);
            var row = rows.FirstOrDefault(r => !r.Done && !r.Busy && Matches(r.File.File.FileName, name));
            if (row is null)
                return;
            row.Busy = true;
            row.Status = Loc.Get("Manual_Checking");
            try
            {
                // Browsers may still hold the file briefly after renaming it into place.
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        if (await ContentService.CompleteManualAsync(manager, row.File, path))
                        {
                            row.Done = true;
                            row.Status = Loc.Get("Manual_Installed");
                        }
                        else
                        {
                            row.Status = Loc.Get("Manual_Mismatch");
                        }
                        break;
                    }
                    catch (IOException)
                    {
                        await Task.Delay(500);
                    }
                }
            }
            finally
            {
                row.Busy = false;
            }
            if (rows.All(r => r.Done))
                dialog.Hide();
        }

        using var watcher = new FileSystemWatcher(downloads)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
        };
        FileSystemEventHandler onFile = (_, e) => _ui.TryEnqueue(() => _ = TryAsync(e.FullPath));
        watcher.Created += onFile;
        watcher.Changed += onFile;
        watcher.Renamed += (_, e) => _ui.TryEnqueue(() => _ = TryAsync(e.FullPath));
        if (Directory.Exists(downloads))
        {
            watcher.EnableRaisingEvents = true;
            foreach (var file in Directory.EnumerateFiles(downloads))
                await TryAsync(file);
        }
        if (rows.All(r => r.Done))
            return;
        await Dialogs.ShowAsync(dialog);
    }

    /// <summary>Exact name, or the browser's de-duplicated "name (1).jar".</summary>
    private static bool Matches(string expected, string actual)
    {
        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            return true;
        var ext = Path.GetExtension(expected);
        var stem = Path.GetFileNameWithoutExtension(expected);
        return actual.Length > stem.Length + ext.Length
            && actual.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
            && actual.StartsWith(stem, StringComparison.OrdinalIgnoreCase)
            && DuplicateSuffix().IsMatch(actual[stem.Length..^ext.Length]);
    }

    [GeneratedRegex(@"^ ?\(\d+\)$")]
    private static partial Regex DuplicateSuffix();

    // ── Pre-launch checks ───────────────────────────────────────────────

    public static string Describe(ModIssue issue)
    {
        var other = issue.Kind == ModIssueKind.WrongLoaderVersion ? LoaderDisplayName(issue.Other) : issue.Other ?? "";
        return Loc.Format($"ModIssue_{issue.Kind}", issue.Subject, other, issue.Detail ?? "", issue.File);
    }

    private static string LoaderDisplayName(string? id) => id?.ToLowerInvariant() switch
    {
        "fabricloader" => "Fabric Loader",
        "quilt_loader" => "Quilt Loader",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        _ => id ?? "",
    };

    public enum LaunchChoice
    {
        Cancel,
        Launch,

        /// <summary>Fix the problems first (<see cref="ModFixService"/>).</summary>
        Fix,
    }

    /// <summary>
    /// Asks whether to fix the errors (when <paramref name="offerFix"/>), launch despite them, or not launch; and
    /// whether to stop asking about these (dismiss them), which goes with launching anyway.
    /// </summary>
    public static async Task<(LaunchChoice Choice, bool DontAskAgain)> ConfirmLaunchAsync(string instanceName, IReadOnlyList<ModIssue> issues, bool offerFix)
    {
        var panel = new StackPanel { Spacing = 8, MinWidth = 480 };
        panel.Children.Add(Dialogs.Text(Loc.Format("ModIssues_Body", instanceName), secondary: true));
        var errors = issues.Where(i => i.Severity == ModIssueSeverity.Error).ToList();
        var warnings = issues.Where(i => i.Severity == ModIssueSeverity.Warning).ToList();
        if (errors.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("ModIssues_Errors")));
            panel.Children.Add(Dialogs.Bullets(errors.Select(Describe)));
        }
        if (warnings.Count > 0)
        {
            panel.Children.Add(Dialogs.Header(Loc.Get("ModIssues_Warnings")));
            panel.Children.Add(Dialogs.Bullets(warnings.Select(Describe)));
        }
        // Outside the scrolling list, so it's seen however long the list is.
        var dontAsk = new CheckBox { Content = Loc.Get("ModIssues_DontAskAgain") };
        ToolTipService.SetToolTip(dontAsk, Loc.Get("ModIssues_DontAskAgainTip"));
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Dialogs.Scroll(panel, 420));
        body.Children.Add(dontAsk);
        var dialog = offerFix
            ? Dialogs.Create(Loc.Get("ModIssues_Title"), body, Loc.Get("ModIssues_Fix"), secondary: Loc.Get("ModIssues_LaunchAnyway"))
            : Dialogs.Create(Loc.Get("ModIssues_Title"), body, Loc.Get("ModIssues_LaunchAnyway"));
        if (offerFix)
        {
            // Not asking again goes with launching anyway: ticked, Enter does that.
            dontAsk.Checked += (_, _) => dialog.DefaultButton = ContentDialogButton.Secondary;
            dontAsk.Unchecked += (_, _) => dialog.DefaultButton = ContentDialogButton.Primary;
        }
        var choice = await Dialogs.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => offerFix ? LaunchChoice.Fix : LaunchChoice.Launch,
            ContentDialogResult.Secondary => LaunchChoice.Launch,
            _ => LaunchChoice.Cancel,
        };
        return (choice, dontAsk.IsChecked == true);
    }
}
