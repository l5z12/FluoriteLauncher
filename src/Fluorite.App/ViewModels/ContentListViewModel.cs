using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Text;
using Fluorite.Modding.Content;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Platforms;
using Fluorite.Platform.Windows.Shell;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fluorite.App.ViewModels;

/// <summary>One file in an instance's content tab.</summary>
public sealed partial class ContentItemViewModel : ObservableObject
{
    private readonly ContentListViewModel _owner;
    private bool _syncing;
    private bool _iconRequested;
    private int _iconGeneration;

    public ContentItemViewModel(ContentFile file, ContentListViewModel owner)
    {
        _owner = owner;
        File = file;
        _syncing = true;
        Enabled = file.Enabled;
        _syncing = false;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Version), nameof(FileName), nameof(Description), nameof(SourceText), nameof(IsPinned), nameof(Subtitle), nameof(IsManifestEdited))]
    public partial ContentFile File { get; set; }

    /// <summary>A mod jar: it has mod info and a manifest to edit.</summary>
    public bool IsMod => File.Location.Kind == ContentKind.Mod && !File.IsDirectory;

    /// <summary>Its manifest was edited in Fluorite (the original is kept).</summary>
    public bool IsManifestEdited => File.Record.OriginalSha1 is not null;

    public string Name => File.DisplayName;
    public string? Version => File.Version;
    public string FileName => File.FileName;
    public bool IsPinned => File.Record.Pinned;
    public bool HasSource => File.Record.Source is not null;

    public string Description =>
        ContentMetadataReader.PlainText(File.Metadata.Description ?? "").ReplaceLineEndings(" ").Trim() is { Length: > 0 } d ? d : FileNameText;

    /// <summary>The file name as the title reads it (repaired, without <c>§</c> codes), unless that leaves only its extension.</summary>
    private string FileNameText =>
        ContentMetadataReader.PlainText(Mojibake.Repair(File.FileName)).Trim() is var name && name.Length > System.IO.Path.GetExtension(name).Length
            ? name
            : File.FileName;

    public string Subtitle => string.Join("  ·  ", new[] { Version, File.Metadata.Authors.Count > 0 ? string.Join(", ", File.Metadata.Authors.Take(3)) : null }.Where(s => !string.IsNullOrEmpty(s)));

    public string SourceText => File.Record.Source?.Platform switch
    {
        ContentPlatform.Modrinth => "Modrinth",
        ContentPlatform.CurseForge => "CurseForge",
        _ => File.IsDirectory ? Loc.Get("Content_Folder") : Loc.Get("Content_Local"),
    };

    [ObservableProperty] public partial bool Enabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateText), nameof(HasUpdate))]
    public partial RemoteVersion? Update { get; set; }

    public bool HasUpdate => Update is not null;
    public string UpdateText => Update is null ? "" : Loc.Format("Content_UpdateTo", Update.VersionNumber);

    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    /// <summary>Its issues (pre-launch checks), one per line; dismissed ones aren't here.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIssue))]
    public partial string? Issue { get; set; }

    public bool HasIssue => Issue is not null;
    [ObservableProperty] public partial bool IssueIsError { get; set; }

    /// <summary>Some issue showing is one Fix looks for a way out of (an error, or no loader).</summary>
    [ObservableProperty] public partial bool CanFixIssue { get; set; }

    /// <summary>Its dismissed issues while they're shown (Show dismissed), else null.</summary>
    [ObservableProperty] public partial string? DismissedIssue { get; set; }

    /// <summary>It has dismissed issues, shown or not: they can be brought back.</summary>
    [ObservableProperty] public partial bool HasDismissedIssues { get; set; }

    /// <summary><see cref="ModIssue.Key"/>s of what <see cref="Issue"/> shows.</summary>
    public IReadOnlyList<string> IssueKeys { get; set; } = [];

    /// <summary><see cref="ModIssue.Key"/>s of its dismissed issues.</summary>
    public IReadOnlyList<string> DismissedKeys { get; set; } = [];

    [ObservableProperty] public partial bool IsBusy { get; set; }

    partial void OnEnabledChanged(bool value)
    {
        if (!_syncing)
            _ = _owner.SetEnabledAsync(this, value);
    }

    public void SetEnabledSilently(bool value)
    {
        _syncing = true;
        Enabled = value;
        _syncing = false;
    }

    /// <summary>Remote icon if the project is known, else the jar/pack's own icon. Call on the UI thread.</summary>
    public async Task LoadIconAsync()
    {
        if (_iconRequested)
            return;
        _iconRequested = true;
        if (File.Record.IconUrl is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            Icon = new BitmapImage(uri) { DecodePixelWidth = 40, DecodePixelType = DecodePixelType.Logical };
            return;
        }
        if (File.Metadata.IconEntry is not { } entry)
            return;
        var generation = _iconGeneration;
        var path = File.Path;
        var bytes = await Task.Run(() => ContentMetadataReader.ReadEntry(path, entry));
        if (bytes is null)
            return;
        // Decoded to the 40 px tile; unlike a BitmapImage fed a stream, the PNG isn't kept alongside.
        var image = await ImageLoader.FromBytesAsync(bytes, 40);
        if (generation != _iconGeneration)
        {
            ImageLoader.Release(image); // Released while it decoded.
            return;
        }
        if (image is not null)
            Icon = image;
    }

    public void ResetIcon() => _iconRequested = false;

    /// <summary>Drops the icon now (the list is going away) rather than whenever the GC gets to it.</summary>
    public void ReleaseIcon()
    {
        _iconGeneration++;
        _iconRequested = false;
        var icon = Icon;
        Icon = null;
        ImageLoader.Release(icon);
    }

    public void RaisePinnedChanged() => OnPropertyChanged(nameof(IsPinned));

    /// <summary>List items are announced by this (screen readers, UI Automation).</summary>
    public override string ToString() => Name;
}

/// <summary>
/// A line of the Mods tab's dismissed issues list: an issue found now, or what's kept for a mod that's away
/// (turned off, not installed) until it's back.
/// </summary>
public sealed class DismissedIssueViewModel(string text, IReadOnlyList<string> keys, ModIssueSeverity? severity)
{
    public string Text { get; } = text;

    /// <summary>What showing it again forgets (<see cref="ModIssue.Key"/>s).</summary>
    public IReadOnlyList<string> Keys { get; } = keys;

    /// <summary>Kept for a mod that's away: nothing shows until it's back.</summary>
    public bool IsAway { get; } = severity is null;

    public bool IsError { get; } = severity == ModIssueSeverity.Error;

    public string Glyph => IsAway ? "" : IsError ? "" : "";

    public string RestoreTip => Loc.Get(IsAway ? "Content_RestoreAwayTip" : "Content_RestoreIssueTip");

    /// <summary>Same line: the list is only rebuilt when one changes (not to lose a click or the scroll).</summary>
    public bool SameAs(DismissedIssueViewModel other) => Text == other.Text && Keys.SequenceEqual(other.Keys);
}

/// <summary>
/// An instance's mods / resource packs / shader packs / data packs: scan, identify, toggle, delete, update,
/// add files, and (for mods) pre-launch issues per file.
/// </summary>
public sealed partial class ContentListViewModel(ContentService content, ContentInstallService installer, UpdateBadgeService badges) : ObservableObject
{
    /// <summary>
    /// What the Mods tab found (or updated) is what the instance's Library card shows. Only after a real check: a
    /// list not checked yet has 0 updates only because nobody looked.
    /// </summary>
    private void ReportUpdates()
    {
        if (Kind == ContentKind.Mod && Instance is { } instance)
            badges.Report(instance, UpdateCount);
    }

    private List<ContentItemViewModel> _all = [];
    private CancellationTokenSource? _refresh;

    public GameInstance? Instance { get; private set; }
    public ContentKind Kind { get; private set; }

    public ObservableCollection<ContentItemViewModel> Items { get; } = [];
    public ObservableCollection<string> Worlds { get; } = [];

    [ObservableProperty] public partial string Filter { get; set; } = "";
    [ObservableProperty] public partial string? SelectedWorld { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsChecking { get; set; }
    [ObservableProperty] public partial string? Status { get; set; }
    [ObservableProperty] public partial string? Error { get; set; }
    [ObservableProperty] public partial string? IssueSummary { get; set; }
    [ObservableProperty] public partial bool IssueSummaryIsError { get; set; }

    /// <summary>Some issue showing can be fixed (an error, or no loader): the issue bar offers Fix.</summary>
    [ObservableProperty] public partial bool CanFix { get; set; }

    /// <summary>Issues dismissed in this instance (<see cref="ModIssue.Key"/>), as last read.</summary>
    private IReadOnlySet<string> _dismissed = new HashSet<string>();

    /// <summary>What the last check found, and how many of those weren't dismissed.</summary>
    private IReadOnlyList<ModIssue> _issues = [];
    private int _shownIssues;

    /// <summary>Dismissed issues are shown (dimmed) under their mods, to bring back.</summary>
    [ObservableProperty] public partial bool ShowDismissed { get; set; }

    /// <summary>Some issue found now is dismissed: there's something to show under the mods.</summary>
    [ObservableProperty] public partial bool HasDismissedHere { get; set; }

    /// <summary>The footer's list (opened from "2 dismissed issues"): those found now, errors first, then those kept for mods that are away.</summary>
    public ObservableCollection<DismissedIssueViewModel> DismissedIssues { get; } = [];

    /// <summary>How many dismissals the list holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DismissedText))]
    public partial int DismissedCount { get; set; }

    /// <summary>The footer's link to the list: "2 dismissed issues".</summary>
    public string DismissedText => DismissedCount == 1 ? Loc.Get("Content_DismissedOne") : Loc.Format("Content_DismissedMany", DismissedCount);

    partial void OnShowDismissedChanged(bool value) => UpdateIssues();
    [ObservableProperty] public partial string Summary { get; set; } = "";
    [ObservableProperty] public partial int UpdateCount { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }

    public bool IsDataPacks => Kind == ContentKind.DataPack;
    public bool IsMods => Kind == ContentKind.Mod;
    public bool HasWorlds => Worlds.Count > 0;

    /// <summary>Mods tab of an instance without a loader: mods won't load.</summary>
    public bool NoLoader => IsMods && Instance is { } i && ContentService.ModLoaderNames(i.Manifest).Count == 0;

    public string EmptyText => Loc.Get(Kind switch
    {
        ContentKind.ResourcePack => "Content_EmptyResourcePacks",
        ContentKind.ShaderPack => "Content_EmptyShaders",
        ContentKind.DataPack => HasWorlds ? "Content_EmptyDataPacks" : "DataPack_NoWorlds",
        _ => "Content_EmptyMods",
    });

    public ContentLocation? Location => Kind == ContentKind.DataPack
        ? SelectedWorld is { } w ? new ContentLocation(Kind, w) : null
        : new ContentLocation(Kind);

    private ContentManager Manager => content.For(Instance!);

    public void Initialize(GameInstance instance, ContentKind kind)
    {
        Instance = instance;
        Kind = kind;
        installer.ContentChanged += OnContentChanged;
        if (kind == ContentKind.Mod)
            installer.DismissalsChanged += OnDismissalsChanged;
        if (kind == ContentKind.DataPack)
        {
            foreach (var w in Manager.GetWorlds())
                Worlds.Add(w);
            SelectedWorld = Worlds.FirstOrDefault();
        }
        OnPropertyChanged(nameof(IsDataPacks));
        OnPropertyChanged(nameof(IsMods));
        OnPropertyChanged(nameof(HasWorlds));
        OnPropertyChanged(nameof(NoLoader));
        OnPropertyChanged(nameof(EmptyText));
    }

    public void Detach()
    {
        installer.ContentChanged -= OnContentChanged;
        installer.DismissalsChanged -= OnDismissalsChanged;
    }

    /// <summary>Dismissed at launch ("Don't ask again") or forgotten there: only the issues change, no need to scan again.</summary>
    private async void OnDismissalsChanged(object? sender, string instanceId)
    {
        if (Instance is not { } instance || !string.Equals(instanceId, instance.Id, StringComparison.OrdinalIgnoreCase))
            return;
        try
        {
            _dismissed = await Manager.GetDismissedIssuesAsync(instance.Id);
            UpdateIssues();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            // Shown at the next refresh (or the app's closing).
        }
    }

    /// <summary>
    /// The instance page is closing: stop listening, cancel a scan in progress and drop the list and its icons, so
    /// nothing keeps them (or the page's list control) alive.
    /// </summary>
    public void Release()
    {
        Detach();
        _refresh?.Cancel();
        foreach (var item in _all)
            item.ReleaseIcon();
        _all = [];
        Items.Clear();
    }

    private void OnContentChanged(object? sender, string gameDirectory)
    {
        if (Instance is not null && string.Equals(gameDirectory, Instance.GameDirectory, StringComparison.OrdinalIgnoreCase))
            _ = ReloadAsync();
    }

    partial void OnSelectedWorldChanged(string? value)
    {
        if (Instance is not null)
            _ = ReloadAsync();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    /// <summary>When the last list was refilled to play its entrance again (<see cref="RefreshAsync"/>).</summary>
    private long _lastReplay = long.MinValue / 2;

    /// <param name="replay">
    /// Refill the list, so its rows play their entrance again: a refresh asked for is seen even when nothing changed.
    /// At most once a second, so holding F5 doesn't rebuild the list nonstop; otherwise, and for every other update,
    /// only what changed changes.
    /// </param>
    private void ApplyFilter(bool replay = false)
    {
        var f = Filter.Trim();
        var visible = string.IsNullOrEmpty(f)
            ? _all
            : _all.Where(i => i.Name.Contains(f, StringComparison.CurrentCultureIgnoreCase) || i.FileName.Contains(f, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (replay && Environment.TickCount64 - _lastReplay >= 1000)
        {
            _lastReplay = Environment.TickCount64;
            Items.Clear();
            foreach (var i in visible)
                Items.Add(i);
        }
        else
        {
            CollectionSync.Sync(Items, visible);
        }
        IsEmpty = _all.Count == 0 && !IsLoading;
        var enabled = _all.Count(i => i.Enabled);
        Summary = _all.Count == 0 ? "" : Loc.Format(_all.Count == 1 ? "Content_SummaryOne" : "Content_Summary", _all.Count, enabled);
    }

    /// <summary>Refresh, F5, the menu: the list plays its entrance again, so it's seen to have happened.</summary>
    [RelayCommand]
    public Task RefreshAsync() => LoadAsync(replay: true);

    /// <summary>After an install, a removal, another world…: only what changed changes.</summary>
    public Task ReloadAsync() => LoadAsync(replay: false);

    private async Task LoadAsync(bool replay)
    {
        if (Instance is null || Location is not { } location)
        {
            _all = [];
            ApplyFilter();
            return;
        }
        _refresh?.Cancel();
        var cts = _refresh = new CancellationTokenSource();
        IsLoading = true;
        Error = null;
        try
        {
            var manager = Manager;
            var files = await Task.Run(() => manager.ScanAsync(location, cts.Token));
            if (IsMods)
                _dismissed = await manager.GetDismissedIssuesAsync(Instance.Id, cts.Token);
            if (cts.IsCancellationRequested)
                return;
            Rebuild(files, replay);
            await ForgetResolvedAsync(manager, files);

            // Identify unknown files in the background, then refresh titles/icons/links.
            var identified = await Task.Run(() => content.IdentifyAsync(manager, files, cts.Token));
            if (identified > 0 && !cts.IsCancellationRequested)
                Rebuild(await Task.Run(() => manager.ScanAsync(location, cts.Token)));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            // Run as a command (Refresh, F5), whatever escapes here would end the app: show it instead.
            App.Get<Microsoft.Extensions.Logging.ILogger<ContentListViewModel>>().LogError(ex, "Refreshing {Kind} failed", Kind);
            Error = ex.Message;
        }
        finally
        {
            if (_refresh == cts)
                IsLoading = false;
            IsEmpty = _all.Count == 0 && !IsLoading;
            MemoryTrim.Schedule(); // Opening every jar leaves a lot of garbage behind.
        }
    }

    private void Rebuild(IReadOnlyList<ContentFile> files, bool replay = false)
    {
        // Not ToDictionary: "x.jar" and "x.jar.disabled" side by side share a key, and a duplicate would throw.
        var previous = new Dictionary<string, ContentItemViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _all)
            previous.TryAdd(item.File.Key, item);
        _all = files.Select(f =>
        {
            // Each one reused once: twins mustn't become the same row twice.
            if (previous.Remove(f.Key, out var existing))
            {
                var iconChanged = existing.File.Record.IconUrl != f.Record.IconUrl;
                existing.File = f;
                existing.SetEnabledSilently(f.Enabled);
                if (iconChanged)
                    existing.ResetIcon();
                return existing;
            }
            return new ContentItemViewModel(f, this);
        }).ToList();
        ApplyFilter(replay);
        UpdateIssues();
        UpdateCount = _all.Count(i => i.HasUpdate);
    }

    /// <summary>Mods tab: run the pre-launch checks and show them per file, leaving out the dismissed ones.</summary>
    private void UpdateIssues()
    {
        if (!IsMods || Instance is null)
            return;
        var issues = _issues = ModChecker.Check(_all.Select(i => i.File).ToList(), Instance.Manifest);
        var fixOn = ModFixService.Enabled; // Read each time: the page is made anew after a visit to Settings.
        static string Lines(List<ModIssue> list) => string.Join("\n", list.Select(ContentInstallService.Describe));
        foreach (var item in _all)
        {
            // Only the enabled copy: a disabled twin ("x.jar.disabled" by "x.jar") has the same file name but isn't checked.
            var mine = item.File.Enabled ? issues.Where(i => i.File == item.FileName).ToList() : [];
            var shown = mine.Where(i => !_dismissed.Contains(i.Key)).ToList();
            var dismissed = mine.Where(i => _dismissed.Contains(i.Key)).ToList();
            item.IssueKeys = shown.Select(i => i.Key).ToList();
            item.DismissedKeys = dismissed.Select(i => i.Key).ToList();
            item.Issue = shown.Count == 0 ? null : Lines(shown);
            item.IssueIsError = shown.Any(i => i.Severity == ModIssueSeverity.Error);
            item.CanFixIssue = fixOn && shown.Any(Fixable);
            item.HasDismissedIssues = dismissed.Count > 0;
            item.DismissedIssue = ShowDismissed && dismissed.Count > 0 ? Lines(dismissed) : null;
        }
        var active = issues.Where(i => !_dismissed.Contains(i.Key)).ToList();
        _shownIssues = active.Count;
        var errors = active.Count(i => i.Severity == ModIssueSeverity.Error);
        var warnings = active.Count - errors;
        IssueSummary = active.Count == 0 ? null : Loc.Format("Content_IssueSummary", errors, warnings);
        IssueSummaryIsError = errors > 0;
        CanFix = fixOn && active.Any(Fixable);
        UpdateDismissedList(issues);
        HasDismissedHere = issues.Count > active.Count;
        if (!HasDismissedHere)
            ShowDismissed = false; // Nothing left to show: the next dismissal hides its issue again.
    }

    private void UpdateDismissedList(IReadOnlyList<ModIssue> issues)
    {
        // Copies of a mod can share one (duplicates): one line, as showing it again brings back both.
        var list = issues.Where(i => _dismissed.Contains(i.Key)).DistinctBy(i => i.Key)
            .OrderByDescending(i => i.Severity)
            .Select(i => new DismissedIssueViewModel(ContentInstallService.Describe(i), [i.Key], i.Severity))
            .ToList();
        // The rest wait for a mod that's away (by that mod), or without a loader aren't checked (by their own mod).
        // Not those just fixed: they're being forgotten.
        var files = _all.Select(i => i.File).ToList();
        var current = issues.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var here = new EnabledMods(files);
        var noLoader = issues.Any(i => i.Kind == ModIssueKind.NoLoader);
        list.AddRange(_dismissed.Where(k => !current.Contains(k))
            .Select(k => (Key: k, Party: here.AwayParty(k) ?? (noLoader ? ModChecker.KeyModId(k) : null)))
            .Where(k => k.Party is not null)
            .GroupBy(k => k.Party!, StringComparer.OrdinalIgnoreCase)
            .Select(g => Away(g.Key, g.Select(k => k.Key).Order(StringComparer.Ordinal).ToList(), files))
            .OrderBy(e => e.Text, StringComparer.CurrentCultureIgnoreCase));
        if (!list.SequenceEqual(DismissedIssues, s_sameLine))
        {
            DismissedIssues.Clear();
            foreach (var entry in list)
                DismissedIssues.Add(entry);
        }
        DismissedCount = list.Sum(e => e.Keys.Count);
    }

    private static readonly IEqualityComparer<DismissedIssueViewModel> s_sameLine =
        EqualityComparer<DismissedIssueViewModel>.Create((a, b) => a is not null && b is not null && a.SameAs(b));

    /// <summary>
    /// The list's line for what's kept waiting for <paramref name="party"/> (<see cref="EnabledMods.AwayParty"/>: a
    /// mod id, a duplicate's copy, or "" for any mod), with how many dismissals that is.
    /// </summary>
    private static DismissedIssueViewModel Away(string party, List<string> keys, List<ContentFile> files)
    {
        if (party.Length == 0)
            return new(Loc.Get("Content_DismissedNoLoader"), keys, null);
        var count = keys.Count == 1 ? Loc.Get("Content_DismissedOne") : Loc.Format("Content_DismissedMany", keys.Count);
        var byFile = files.Where(f => !f.IsDirectory && string.Equals(f.FileName, party, StringComparison.OrdinalIgnoreCase)).ToList();
        var copies = byFile.Count > 0 ? byFile
            : files.Where(f => !f.IsDirectory && ModChecker.IdsOf(f).Contains(party, StringComparer.OrdinalIgnoreCase)).ToList();
        // Turned off, gone, or here but not checked (no loader: nothing else is).
        var text = copies.Count == 0 ? "Content_DismissedGone" : copies.Any(f => f.Enabled) ? "Content_DismissedUnchecked" : "Content_DismissedOff";
        // A duplicate's copy goes by its file: the other copy, by the same name, is on.
        var name = byFile.Count > 0 ? party : copies.FirstOrDefault()?.DisplayName ?? party;
        return new(Loc.Format(text, name, count), keys, null);
    }

    /// <summary>
    /// Dismisses the issues showing under these mods, for this instance: they're no longer shown, nor asked about
    /// before launch, until they change.
    /// </summary>
    public Task DismissIssuesAsync(IEnumerable<ContentItemViewModel> items) =>
        SetDismissedAsync(items.SelectMany(i => i.IssueKeys).Distinct().ToList(), dismissed: true);

    /// <summary>Shows these mods' dismissed issues again.</summary>
    public Task RestoreIssuesAsync(IEnumerable<ContentItemViewModel> items) =>
        SetDismissedAsync(items.SelectMany(i => i.DismissedKeys).Distinct().ToList(), dismissed: false);

    /// <summary>The issue bar's Dismiss all.</summary>
    [RelayCommand]
    private Task DismissAllIssuesAsync() => DismissIssuesAsync(_all);

    /// <summary>What Fix looks for a way out of: errors, and mods without a loader (other warnings are only advice).</summary>
    private static bool Fixable(ModIssue issue) => issue.Severity == ModIssueSeverity.Error || issue.Kind == ModIssueKind.NoLoader;

    /// <summary>The issue bar's Fix: every issue showing.</summary>
    [RelayCommand]
    private Task FixAllIssuesAsync() => FixIssuesAsync(_all);

    /// <summary>
    /// Looks for changes that fix the issues showing under these mods (dismissed ones stay as they are), shows them,
    /// and makes those the user keeps; the list then reloads, and says what was fixed.
    /// </summary>
    public async Task FixIssuesAsync(IEnumerable<ContentItemViewModel> items)
    {
        if (Instance is not { } instance)
            return;
        var keys = items.SelectMany(i => i.IssueKeys).Distinct().ToList();
        if (keys.Count == 0)
            return;
        Status = null;
        if (await App.Get<ModFixService>().FixAsync(instance, keys) is { Changed: true } outcome)
        {
            Status = outcome.Summary;
            OnPropertyChanged(nameof(NoLoader)); // One may have been added.
        }
    }

    /// <summary>The dismissed list's Show again, on one line.</summary>
    public Task RestoreDismissedAsync(DismissedIssueViewModel entry) => SetDismissedAsync(entry.Keys, dismissed: false);

    /// <summary>The dismissed list's Show all again: also what's kept for mods that are away.</summary>
    public Task RestoreAllDismissedAsync() => SetDismissedAsync(DismissedIssues.SelectMany(e => e.Keys).Distinct().ToList(), dismissed: false);

    /// <summary>The undo offered by the last dismissal (or showing again), while it's the pending one.</summary>
    private UndoOffer? _dismissUndo;

    private async Task SetDismissedAsync(IReadOnlyList<string> keys, bool dismissed, bool offerUndo = true)
    {
        if (keys.Count == 0 || Instance is not { } instance)
            return;
        var manager = Manager;
        var shownBefore = _shownIssues;
        try
        {
            await manager.SetIssuesDismissedAsync(instance.Id, keys, dismissed);
            _dismissed = await manager.GetDismissedIssuesAsync(instance.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return;
        }
        UpdateIssues();
        // Not in place of a removal's Undo: that one can't be had back any other way (these can, from the row or the list).
        if (offerUndo && (PendingUndo is null || PendingUndo == _dismissUndo))
        {
            // Dismissed: the lines that went, which a key shared by several rows (copies of one mod) can be more than.
            // Shown again: the dismissals forgotten, some of which (mods away) show nothing yet.
            var count = dismissed ? Math.Max(1, shownBefore - _shownIssues) : keys.Count;
            var message = dismissed
                ? count == 1 ? Loc.Get("Undo_DismissedOne") : Loc.Format("Undo_DismissedMany", count)
                : count == 1 ? Loc.Get("Undo_RestoredOne") : Loc.Format("Undo_RestoredMany", count);
            OfferUndo(_dismissUndo = new UndoOffer(message, () => SetDismissedAsync(keys, !dismissed, offerUndo: false)));
        }
    }

    /// <summary>Dismissals whose problem was fixed are forgotten, so if it comes back it's shown (and asked about) again.</summary>
    private async Task ForgetResolvedAsync(ContentManager manager, IReadOnlyList<ContentFile> files)
    {
        if (!IsMods || Instance is not { } instance)
            return;
        if (ModChecker.ResolvedDismissals(_dismissed, _issues, files) is not { Count: > 0 } resolved)
            return;
        try
        {
            await manager.SetIssuesDismissedAsync(instance.Id, resolved, dismissed: false);
            _dismissed = await manager.GetDismissedIssuesAsync(instance.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Forgotten next time; meanwhile they're not shown anyway (nothing to show).
        }
    }

    public async Task SetEnabledAsync(ContentItemViewModel item, bool enabled)
    {
        try
        {
            item.File = await Manager.SetEnabledAsync(item.File, enabled);
        }
        catch (IOException ex)
        {
            item.SetEnabledSilently(item.File.Enabled);
            Error = ex.Message;
        }
        ApplySummaryOnly();
        UpdateIssues();
        // Turning on what fixes a dismissed problem forgets its dismissal, as a refresh or launch would: if turning it
        // off again brings the problem back, it's shown (and asked about) again.
        await ForgetResolvedAsync(Manager, _all.Select(i => i.File).ToList());
    }

    private void ApplySummaryOnly() =>
        Summary = _all.Count == 0 ? "" : Loc.Format(_all.Count == 1 ? "Content_SummaryOne" : "Content_Summary", _all.Count, _all.Count(i => i.Enabled));

    public async Task SetAllEnabledAsync(IReadOnlyList<ContentItemViewModel> items, bool enabled)
    {
        foreach (var item in items.Where(i => i.Enabled != enabled))
        {
            item.SetEnabledSilently(enabled);
            await SetEnabledAsync(item, enabled);
        }
    }

    /// <summary>"Removed … Undo" after the last removal.</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>
    /// Removed at once, no question: they wait out of the game's sight for a few seconds (Undo), then go to the
    /// Recycle Bin (<paramref name="permanent"/>: deleted for good). Undo also brings back what Fluorite knew about
    /// them (source, pin).
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<ContentItemViewModel> items, bool permanent = false)
    {
        if (items.Count == 0 || Instance is null)
            return;
        var manager = Manager;
        var files = items.Select(i => i.File).ToList();
        var message = UndoOffer.RemovedText(items.Select(i => i.Name).ToList(), permanent);
        RemovalStash stash;
        try
        {
            stash = await Task.Run(() => RemovalStash.Create(manager.GameDirectory, files.Select(f => f.Path), permanent));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = ex.Message;
            return;
        }
        await ReloadAsync();
        OfferUndo(new UndoOffer(message, async () =>
        {
            await Task.Run(stash.Restore);
            await manager.RestoreRecordsAsync(files);
            await ReloadAsync();
        }, stash.Commit));
    }

    private void OfferUndo(UndoOffer offer)
    {
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
    }

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (Instance is null)
            return;
        IsChecking = true;
        Status = null;
        Error = null;
        try
        {
            var target = ContentService.TargetFor(Instance.Manifest, Kind);
            var files = _all.Select(i => i.File).ToList();
            var updates = await Task.Run(() => content.CheckUpdatesAsync(files, target));
            foreach (var item in _all)
                item.Update = updates.FirstOrDefault(u => u.File.Key == item.File.Key)?.Latest;
            UpdateCount = updates.Count;
            ReportUpdates();
            var unknown = _all.Count(i => !i.HasSource && !i.File.IsDirectory);
            Status = updates.Count == 0 ? Loc.Get("Content_UpToDate") : Loc.Format("Content_UpdatesFound", updates.Count);
            if (unknown > 0)
                Status += " " + Loc.Format("Content_UnknownFiles", unknown);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidDataException)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            // A command: whatever escapes would end the app.
            App.Get<Microsoft.Extensions.Logging.ILogger<ContentListViewModel>>().LogError(ex, "Checking {Kind} for updates failed", Kind);
            Error = ex.Message;
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAllAsync()
    {
        var updates = _all.Where(i => i.Update is not null).Select(i => new ContentUpdate(i.File, i.Update!)).ToList();
        if (Instance is null || updates.Count == 0)
            return;
        foreach (var i in _all.Where(i => i.HasUpdate))
            i.IsBusy = true;
        try
        {
            if (await installer.UpdateAsync(Instance, Kind, updates) is { } applied)
            {
                // Updates skipped because they wouldn't work here keep their badge.
                var done = applied.Select(u => u.File.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var i in _all.Where(i => done.Contains(i.File.Key)))
                    i.Update = null;
                UpdateCount = _all.Count(i => i.HasUpdate);
                ReportUpdates();
                Status = applied.Count == updates.Count
                    ? Loc.Format("Content_Updated", applied.Count)
                    : Loc.Format("Content_UpdatedSkipped", applied.Count, updates.Count - applied.Count);
            }
        }
        finally
        {
            foreach (var i in _all)
                i.IsBusy = false;
        }
    }

    public async Task UpdateOneAsync(ContentItemViewModel item)
    {
        if (Instance is null || item.Update is not { } latest)
            return;
        item.IsBusy = true;
        try
        {
            if (await installer.UpdateAsync(Instance, Kind, [new ContentUpdate(item.File, latest)]) is { Count: > 0 })
            {
                item.Update = null;
                UpdateCount = _all.Count(i => i.HasUpdate);
                ReportUpdates();
            }
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    public async Task TogglePinAsync(ContentItemViewModel item)
    {
        var pinned = !item.IsPinned;
        await Manager.UpdateAsync(index =>
        {
            if (index.Files.TryGetValue(item.File.Key, out var r))
                r.Pinned = pinned;
            return true;
        });
        item.File.Record.Pinned = pinned;
        item.RaisePinnedChanged();
        if (pinned)
        {
            item.Update = null;
            UpdateCount = _all.Count(i => i.HasUpdate);
            ReportUpdates();
        }
    }

    public async Task<int> ImportAsync(IEnumerable<string> paths) =>
        Instance is null || Location is not { } location ? 0 : await installer.ImportAsync(Instance, location, paths);

    public async Task<string?> GetChangelogHtmlAsync(ContentItemViewModel item)
    {
        if (item.Update is not { } latest)
            return null;
        var platform = content.Get(latest.Platform);
        var text = await platform.GetChangelogAsync(latest);
        if (string.IsNullOrWhiteSpace(text))
            return $"<p>{System.Net.WebUtility.HtmlEncode(Loc.Get("Content_NoChangelog"))}</p>";
        return platform.TextFormat == TextFormat.Markdown ? HtmlContent.FromMarkdown(text) : text;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (Instance is null || Location is not { } location)
            return;
        var path = location.FullPath(Instance.GameDirectory);
        Directory.CreateDirectory(path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ── Mod info and manifest ─────────────────────────────────────────────

    /// <summary>
    /// What this instance runs, as mod manifests name it: Minecraft, then its loader (e.g. <c>fabricloader</c>), with
    /// the versions. For "Allow Minecraft 1.21.4" and the dependency ticks in Mod info.
    /// </summary>
    public IReadOnlyList<(string DependencyId, string Label, string Version)> InstanceTargets
    {
        get
        {
            if (Instance is null)
                return [];
            var result = new List<(string, string, string)>();
            if (Instance.Manifest.MinecraftVersion is { } mc)
                result.Add(("minecraft", "Minecraft", mc));
            if (ComponentResolver.PrimaryLoader(Instance.Manifest) is { } loader && LoaderDependencyId(loader.Uid) is { } id)
                result.Add((id, InstanceItem.LoaderName(loader.Uid), loader.Version));
            return result;
        }
    }

    private static string? LoaderDependencyId(string uid) => uid switch
    {
        ComponentUids.FabricLoader or ComponentUids.LegacyFabric => "fabricloader",
        ComponentUids.QuiltLoader => "quilt_loader",
        ComponentUids.Forge => "forge",
        ComponentUids.NeoForge => "neoforge",
        _ => null,
    };

    /// <summary>The loader whose manifest to open first in a jar made for several.</summary>
    public ModLoaderKind? InstanceLoaderKind => Instance is null ? null : ComponentResolver.PrimaryLoader(Instance.Manifest)?.Uid switch
    {
        ComponentUids.QuiltLoader => ModLoaderKind.Quilt,
        ComponentUids.Forge => ModLoaderKind.Forge,
        ComponentUids.NeoForge => ModLoaderKind.NeoForge,
        ComponentUids.FabricLoader or ComponentUids.LegacyFabric => ModLoaderKind.Fabric,
        _ => null,
    };

    /// <summary>Some file in this list (enabled) provides the mod id: for the ticks on Mod info's dependencies.</summary>
    public bool HasModId(string id) => _all.Any(i => i.Enabled && i.File.Metadata.ProvidedIds.Contains(id, StringComparer.OrdinalIgnoreCase));

    public bool HasOriginal(ContentItemViewModel item) => Instance is not null && Manager.OriginalOf(item.File) is not null;

    /// <summary>Writes an edited manifest into the mod's jar. Null when done, else why not.</summary>
    public async Task<string?> EditManifestAsync(ContentItemViewModel item, string entryName, string text)
    {
        if (Instance is null)
            return null;
        try
        {
            var unsigned = await Manager.EditManifestAsync(item.File, entryName, text);
            await ReloadAsync();
            Status = Loc.Format(unsigned ? "Manifest_SavedUnsigned" : "Manifest_Saved", item.Name);
            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException)
        {
            return Loc.Get("Manifest_NoMod");
        }
        catch (Exception ex) when (ex is IOException and not FileNotFoundException)
        {
            return Loc.Format("Manifest_InUse", item.FileName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException)
        {
            return ex.Message;
        }
    }

    /// <summary>Puts back the jar as it was before its manifest was edited. Null when done, else why not.</summary>
    public async Task<string?> RestoreManifestAsync(ContentItemViewModel item)
    {
        if (Instance is null)
            return null;
        try
        {
            await Manager.RestoreOriginalAsync(item.File);
            await ReloadAsync();
            Status = Loc.Format("Manifest_Restored", item.Name);
            return null;
        }
        catch (Exception ex) when (ex is IOException and not FileNotFoundException)
        {
            return Loc.Format("Manifest_InUse", item.FileName);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or FileNotFoundException)
        {
            return ex.Message;
        }
    }

    public static void ShowInFolder(ContentItemViewModel item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{item.File.Path}\"") { UseShellExecute = false });

    [RelayCommand]
    private void Browse()
    {
        if (Instance is not null)
            App.Window?.NavigateTo("Browse", new BrowseContext(Instance.Id, Kind));
    }

    public ProjectNavigation? ProjectFor(ContentItemViewModel item) =>
        item.File.Record.Source is { } s
            ? new ProjectNavigation(new RemoteProject(s.Platform, s.ProjectId, "", item.Name) { IconUrl = item.File.Record.IconUrl, WebUrl = item.File.Record.ProjectUrl }, Instance, Kind)
            : null;
}
