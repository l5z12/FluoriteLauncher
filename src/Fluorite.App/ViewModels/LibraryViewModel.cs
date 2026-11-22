using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core.Instances;
using Fluorite.Core.Settings;
using Fluorite.Core.Versions;
using Fluorite.Platform.Windows.Shell;

namespace Fluorite.App.ViewModels;

/// <summary>A Library source filter entry: null = everything, "" = Fluorite's own, otherwise an external root id.</summary>
public sealed record SourceChoice(string? RootId, string Label)
{
    public override string ToString() => Label;
}

public sealed partial class LibraryViewModel(
    FluoriteLayout layout, InstanceRegistry registry, VersionRepository versions, GameRunner runner, SettingsService settings,
    ShortcutService shortcuts, JumpListService jumpList, UpdateBadgeService badges) : ObservableObject
{
    private List<InstanceItem> _all = [];
    private bool _badgesHooked;

    private void OnBadgeChanged(object? sender, string instanceId)
    {
        foreach (var item in _all.Where(i => i.Instance.Id == instanceId))
            item.UpdateCount = badges.CountFor(instanceId);
    }

    public ObservableCollection<InstanceItem> Instances { get; } = [];

    /// <summary>"All", "Fluorite" and one entry per other launcher's folder (shown when there are any).</summary>
    public ObservableCollection<SourceChoice> Sources { get; } = [];
    [ObservableProperty] public partial int SourceIndex { get; set; }
    [ObservableProperty] public partial bool HasSources { get; set; }

    partial void OnSourceIndexChanged(int value) => ApplyFilter();

    [ObservableProperty] public partial bool IsLoading { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string? ErrorMessage { get; set; }
    [ObservableProperty] public partial string? StatusMessage { get; set; }
    [ObservableProperty] public partial int SortIndex { get; set; } = (int)settings.Current.LibrarySort;

    /// <summary>The filtered instances in sections: named groups A–Z, then the ungrouped ones.</summary>
    public ObservableCollection<InstanceGroup> Groups { get; } = [];

    /// <summary>0: cards; 1: rows.</summary>
    [ObservableProperty] public partial int ViewIndex { get; set; } = settings.Current.LibraryListView ? 1 : 0;

    partial void OnViewIndexChanged(int value)
    {
        settings.Current.LibraryListView = value == 1;
        settings.Save();
        foreach (var g in Groups)
            g.IsListView = value == 1;
    }

    /// <summary>Every group name in use, A–Z (for "Move to group" and the new instance dialog).</summary>
    public IReadOnlyList<string> GroupNames => _all.Select(i => i.Group).OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    /// <summary>Hidden instances show too (dimmed). For this session only: hidden is meant to stay out of the way.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden))]
    public partial bool ShowHidden { get; set; }

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    /// <summary>Hidden instances in the source shown; the "Hidden" toggle appears only when there are some.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHidden), nameof(HiddenToggleText), nameof(HiddenCountText))]
    public partial int HiddenCount { get; set; }

    public bool HasHidden => HiddenCount > 0 || ShowHidden;

    /// <summary>"Show hidden (3)", in the Library's menu.</summary>
    public string HiddenToggleText => Loc.Format("Library_ShowHidden", HiddenCount);

    /// <summary>The count on the eye-slash toggle by the search box.</summary>
    public string HiddenCountText => HiddenCount.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Nothing shows, but hidden instances would: the empty state offers to show them.</summary>
    [ObservableProperty] public partial bool CanRevealHidden { get; set; }

    [RelayCommand]
    private void RevealHidden() => ShowHidden = true;

    /// <summary>"Hid “X”. Undo".</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>
    /// Hides or shows instances. Hiding them out of sight offers Undo; another launcher's instance keeps the flag in
    /// Fluorite's notes beside it.
    /// </summary>
    public async Task SetHiddenAsync(IReadOnlyList<InstanceItem> items, bool hidden, bool offerUndo = true)
    {
        var changed = items.Where(i => i.IsHidden != hidden).ToList();
        if (changed.Count == 0)
            return;
        foreach (var item in changed)
        {
            item.Instance.Manifest.Hidden = hidden;
            item.RefreshHidden();
            await SaveAsync(item);
        }
        ApplyFilter();
        _ = jumpList.UpdateAsync(_all.Select(i => i.Instance).ToList());
        if (hidden && offerUndo && !ShowHidden) // While they show, they stay in sight (dimmed): nothing to undo from.
        {
            var message = changed.Count == 1 ? Loc.Format("Library_HidOne", changed[0].Name) : Loc.Format("Library_HidMany", changed.Count);
            var offer = new UndoOffer(message, () => SetHiddenAsync(changed, false));
            _ = PendingUndo?.CommitAsync();
            offer.Finished += (_, _) =>
            {
                if (PendingUndo == offer)
                    PendingUndo = null;
            };
            PendingUndo = offer;
        }
    }

    partial void OnSortIndexChanged(int value)
    {
        settings.Current.LibrarySort = (LibrarySort)Math.Clamp(value, 0, 3);
        settings.Save();
        ApplyFilter();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var list = await registry.GetInstancesAsync();
            _all = list.Select(i => new InstanceItem(i) { IsRunning = runner.Find(i) is not null, UpdateCount = badges.CountFor(i.Id) }).ToList();
            _anchor = null;
            RebuildSources();
            ApplyFilter();
            if (registry.RootErrors.Count > 0)
                ErrorMessage = string.Join("\n", registry.RootErrors.Select(e => Loc.Format("Library_RootError", e.Key, e.Value)));
            if (!_badgesHooked)
            {
                badges.Changed += OnBadgeChanged;
                _badgesHooked = true;
            }
            badges.CheckInBackground(list);
            await jumpList.UpdateAsync(list);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void RebuildSources()
    {
        var selected = SourceIndex > 0 && SourceIndex < Sources.Count ? Sources[SourceIndex].RootId : null;
        var roots = settings.Current.ExternalRoots.Where(r => r.Enabled).ToList();
        Sources.Clear();
        Sources.Add(new SourceChoice(null, Loc.Get("Library_SourceAll")));
        Sources.Add(new SourceChoice("", "Fluorite"));
        foreach (var r in roots)
            Sources.Add(new SourceChoice(r.Id, ExternalRootItem.Label(r)));
        HasSources = roots.Count > 0;
        var index = selected is null ? 0 : Sources.ToList().FindIndex(s => s.RootId == selected);
        SourceIndex = Math.Max(index, 0);
        OnPropertyChanged(nameof(SourceIndex));
    }

    private void ApplyFilter()
    {
        var source = SourceIndex > 0 && SourceIndex < Sources.Count ? Sources[SourceIndex].RootId : null;
        // Every word somewhere in the name, version and loader, group or launcher: "fabric 1.21", "modded", "prism".
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var inSource = _all.Where(i => source is null || (i.Instance.RootId ?? "") == source).ToList();
        var matches = inSource.Where(i => terms.All(t => i.Name.Contains(t, StringComparison.OrdinalIgnoreCase)
                || i.VersionText.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (i.Group?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)
                || i.SourceLabel.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        HiddenCount = inSource.Count(i => i.IsHidden);
        var hiddenMatches = ShowHidden ? 0 : matches.Count(i => i.IsHidden);
        IEnumerable<InstanceItem> items = ShowHidden ? matches : matches.Where(i => !i.IsHidden);

        items = (LibrarySort)SortIndex switch
        {
            LibrarySort.Name => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            LibrarySort.Created => items.OrderByDescending(i => i.Instance.Manifest.Created),
            LibrarySort.MostPlayed => items.OrderByDescending(i => i.Instance.Manifest.Stats.TotalPlaySeconds),
            _ => items.OrderByDescending(i => i.Instance.Manifest.Stats.LastPlayed ?? i.Instance.Manifest.Created),
        };

        Instances.Clear();
        foreach (var i in items)
            Instances.Add(i);
        // What the search or source now hides isn't selected any more (it would be moved or deleted unseen).
        foreach (var hidden in _all.Where(i => i.IsSelected && !Instances.Contains(i)))
            hidden.IsSelected = false;
        UpdateSelection();
        RebuildGroups();
        IsEmpty = _all.Count == 0;
        IsNoMatch = !IsEmpty && Instances.Count == 0;
        CanRevealHidden = IsNoMatch && hiddenMatches > 0;
        NoMatchText = (SearchText.Trim(), hiddenMatches) switch
        {
            ({ Length: > 0 } q, 0) => Loc.Format("Library_NoMatch", q),
            ({ Length: > 0 } q, 1) => Loc.Format("Library_NoMatchHiddenOne", q),
            ({ Length: > 0 } q, var n) => Loc.Format("Library_NoMatchHidden", q, n),
            (_, 0) => Loc.Get("Library_NoneHere"),
            (_, 1) => Loc.Get("Library_AllHiddenOne"),
            (_, var n) => Loc.Format("Library_AllHidden", n),
        };
    }

    /// <summary>Sections in the sort order within each; while searching, collapsed groups open to show their matches.</summary>
    private void RebuildGroups()
    {
        Groups.Clear();
        var collapsed = settings.Current.CollapsedGroups;
        var searching = SearchText.Trim().Length > 0;
        var list = ViewIndex == 1;
        foreach (var g in Instances.Where(i => i.Group is not null)
                     .GroupBy(i => i.Group!, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            Groups.Add(new InstanceGroup(g.First().Group, g, hasHeader: true, searching || !collapsed.Contains(g.Key, StringComparer.OrdinalIgnoreCase), list));
        }
        var ungrouped = Instances.Where(i => i.Group is null).ToList();
        if (ungrouped.Count > 0)
            Groups.Add(new InstanceGroup(null, ungrouped, hasHeader: Groups.Count > 0, searching || Groups.Count == 0 || !collapsed.Contains(""), list));
    }

    /// <summary>Opens or closes a section and remembers it.</summary>
    public void ToggleGroup(InstanceGroup group)
    {
        group.IsExpanded = !group.IsExpanded;
        var collapsed = settings.Current.CollapsedGroups;
        collapsed.RemoveAll(k => string.Equals(k, group.Key, StringComparison.OrdinalIgnoreCase));
        if (!group.IsExpanded)
            collapsed.Add(group.Key);
        settings.Save();
    }

    /// <summary>Opens or closes every section that has a header, and remembers it.</summary>
    public void SetAllExpanded(bool expanded)
    {
        var collapsed = settings.Current.CollapsedGroups;
        foreach (var group in Groups.Where(g => g.HasHeader))
        {
            group.IsExpanded = expanded;
            collapsed.RemoveAll(k => string.Equals(k, group.Key, StringComparison.OrdinalIgnoreCase));
            if (!expanded)
                collapsed.Add(group.Key);
        }
        settings.Save();
    }

    /// <summary>
    /// Puts an instance in a group (null: in none). Another launcher's instance keeps this in Fluorite's notes beside
    /// it; its own group file isn't touched, and "" records "none" over the group it has there.
    /// </summary>
    public Task SetGroupAsync(InstanceItem item, string? group) => SetGroupAsync([item], group);

    public async Task SetGroupAsync(IReadOnlyList<InstanceItem> items, string? group)
    {
        foreach (var item in items)
        {
            item.Instance.Manifest.Group = string.IsNullOrWhiteSpace(group) ? (item.IsExternal ? "" : null) : group.Trim();
            await SaveAsync(item);
        }
        // A group dropped into while collapsed opens, so what was just moved there is seen.
        if (!string.IsNullOrWhiteSpace(group)
            && settings.Current.CollapsedGroups.RemoveAll(k => string.Equals(k, group.Trim(), StringComparison.OrdinalIgnoreCase)) > 0)
            settings.Save();
        ApplyFilter();
    }

    // ── Selection: Ctrl+click, Shift+click, Ctrl+A; Esc clears ───────────

    private InstanceItem? _anchor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionText))]
    public partial int SelectionCount { get; set; }

    public string SelectionText => Loc.Format(SelectionCount == 1 ? "Library_SelectedOne" : "Library_Selected", SelectionCount);

    public IReadOnlyList<InstanceItem> Selection => Instances.Where(i => i.IsSelected).ToList();

    public void ToggleSelected(InstanceItem item)
    {
        item.IsSelected = !item.IsSelected;
        _anchor = item;
        UpdateSelection();
    }

    /// <summary>From the last one clicked to this one, in the order shown (open groups only), as Explorer does.</summary>
    public void SelectRange(InstanceItem item)
    {
        var shown = Groups.Where(g => g.IsExpanded).SelectMany(g => g.Items).ToList();
        var from = _anchor is null ? -1 : shown.IndexOf(_anchor);
        var to = shown.IndexOf(item);
        if (from < 0 || to < 0)
        {
            ToggleSelected(item);
            return;
        }
        var (lo, hi) = (Math.Min(from, to), Math.Max(from, to));
        for (var i = 0; i < shown.Count; i++)
            shown[i].IsSelected = i >= lo && i <= hi;
        UpdateSelection();
    }

    public void SelectAll()
    {
        foreach (var item in Groups.Where(g => g.IsExpanded).SelectMany(g => g.Items))
            item.IsSelected = true;
        UpdateSelection();
    }

    public void ClearSelection()
    {
        foreach (var item in _all)
            item.IsSelected = false;
        _anchor = null;
        UpdateSelection();
    }

    /// <summary>Every instance picked is hidden: the selection bar offers to show them instead of hiding.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectionHideText))]
    public partial bool SelectionAllHidden { get; set; }

    public string SelectionHideText => Loc.Get(SelectionAllHidden ? "Library_Unhide" : "Library_Hide");

    private void UpdateSelection()
    {
        var selected = Instances.Where(i => i.IsSelected).ToList();
        SelectionCount = selected.Count;
        SelectionAllHidden = selected.Count > 0 && selected.All(i => i.IsHidden);
    }

    /// <summary>Renames a group for every instance in it (null ungroups them all; the instances stay).</summary>
    public async Task RenameGroupAsync(string from, string? to)
    {
        foreach (var item in _all.Where(i => string.Equals(i.Group, from, StringComparison.OrdinalIgnoreCase)))
        {
            item.Instance.Manifest.Group = string.IsNullOrWhiteSpace(to) ? (item.IsExternal ? "" : null) : to.Trim();
            await SaveAsync(item);
        }
        var collapsed = settings.Current.CollapsedGroups;
        if (collapsed.RemoveAll(k => string.Equals(k, from, StringComparison.OrdinalIgnoreCase)) > 0 && !string.IsNullOrWhiteSpace(to))
            collapsed.Add(to.Trim());
        settings.Save();
        ApplyFilter();
    }

    private async Task SaveAsync(InstanceItem item)
    {
        try
        {
            await registry.SaveAsync(item.Instance);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>There are instances, but the search or the source filter hides them all.</summary>
    [ObservableProperty] public partial bool IsNoMatch { get; set; }
    [ObservableProperty] public partial string? NoMatchText { get; set; }

    [RelayCommand]
    private void ClearSearch() => SearchText = "";

    /// <summary>What a new instance is called when its name is left empty: "Fabric 1.21.1", "1.21.1".</summary>
    public static string DefaultName(string minecraftVersion, string? loaderUid) =>
        loaderUid is null ? minecraftVersion : $"{InstanceItem.LoaderName(loaderUid)} {minecraftVersion}";

    /// <summary>Versions for the "new instance" picker, newest first.</summary>
    public async Task<IReadOnlyList<VersionManifestEntry>> GetVersionsAsync(bool includeSnapshots, bool includeOld)
    {
        var manifest = await versions.GetManifestAsync();
        return manifest.Versions
            .Where(v => v.Type == VersionTypes.Release
                || (includeSnapshots && v.Type == VersionTypes.Snapshot)
                || (includeOld && v.Type is VersionTypes.OldBeta or VersionTypes.OldAlpha))
            .OrderByDescending(v => v.ParsedReleaseTime)
            .ToList();
    }

    public async Task<GameInstance> CreateAsync(string name, string minecraftVersion, LoaderPickerViewModel? loader = null, string? group = null)
    {
        var manifest = new InstanceManifest
        {
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = minecraftVersion }],
            Group = string.IsNullOrWhiteSpace(group) ? null : group.Trim(),
        };
        loader?.ApplyTo(manifest, minecraftVersion);
        manifest.Name = string.IsNullOrWhiteSpace(name) ? DefaultName(minecraftVersion, ComponentResolver.PrimaryLoader(manifest)?.Uid) : name.Trim();

        var instance = await layout.CreateAsync(manifest);
        await LoadAsync();
        return instance;
    }

    public async Task RenameAsync(InstanceItem item, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName))
            return;
        item.Instance.Manifest.Name = newName.Trim();
        await registry.SaveAsync(item.Instance);
        await LoadAsync();
    }

    /// <summary>Fluorite instances are duplicated; another launcher's instance gets a Fluorite-owned copy.</summary>
    public async Task DuplicateAsync(InstanceItem item)
    {
        IsLoading = true;
        try
        {
            if (item.Instance.IsExternal)
                await Fluorite.Modding.Modpacks.InstanceImporter.CopyAsync(layout, item.Instance);
            else
                await layout.DuplicateAsync(item.Instance, Loc.Format("Duplicate_Suffix", item.Name));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
        await LoadAsync();
    }

    /// <summary>Moves the instance folder to the Recycle Bin.</summary>
    public async Task DeleteAsync(InstanceItem item)
    {
        if (runner.Find(item.Instance) is not null)
        {
            ErrorMessage = Loc.Get("Delete_Running");
            return;
        }
        if (item.Instance.IsExternal)
        {
            ErrorMessage = Loc.Get("Delete_External"); // Never delete another launcher's files from Fluorite.
            return;
        }
        await Task.Run(() => RecycleBin.Send(item.Instance.Directory));
        await LoadAsync();
    }

    /// <summary>
    /// The selection to the Recycle Bin, except what can't go: a game that's running, another launcher's instance.
    /// Says how many were left.
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<InstanceItem> items)
    {
        var deletable = items.Where(i => !i.Instance.IsExternal && runner.Find(i.Instance) is null).ToList();
        try
        {
            foreach (var item in deletable)
                await Task.Run(() => RecycleBin.Send(item.Instance.Directory));
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        await LoadAsync();
        if (items.Count > deletable.Count)
            ErrorMessage = Loc.Format("Library_DeleteKept", items.Count - deletable.Count);
    }

    public void CreateShortcut(InstanceItem item)
    {
        shortcuts.CreateDesktopShortcut(item.Instance);
        StatusMessage = Loc.Format("Shortcut_Created", item.Name);
    }

    [RelayCommand]
    private void Play(InstanceItem item) => PlayAs(item, null);

    /// <summary>Plays as <paramref name="accountId"/> this once ("Launch as"), or as the usual account when null.</summary>
    public void PlayAs(InstanceItem item, string? accountId)
    {
        runner.Launch(item.Instance, accountId: accountId);
        item.IsRunning = true;
    }

    [RelayCommand]
    private static void OpenFolder(InstanceItem item) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(item.Instance.GameDirectory) { UseShellExecute = true });

    [RelayCommand]
    private static void OpenInstancesFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Directory.CreateDirectory(Path.Combine(AppPaths.DataRoot, "instances")).FullName) { UseShellExecute = true });
}
