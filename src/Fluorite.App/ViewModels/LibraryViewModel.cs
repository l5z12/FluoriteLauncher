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
    ShortcutService shortcuts, JumpListService jumpList, UpdateBadgeService badges, Fluorite.Modding.Content.ContentService content) : ObservableObject
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

    /// <summary>Every group, A–Z: those in use and the empty ones made with "New group…" (for "Move to group" and the new instance dialog).</summary>
    public IReadOnlyList<string> GroupNames => _all.Select(i => i.Group).OfType<string>()
        .Concat(settings.Current.CreatedGroups)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Order(StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public bool IsGroupHidden(string? group) => settings.Current.IsGroupHidden(group);

    private static bool SameName(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Takes a group out of one of the settings lists (collapsed, created, hidden); true if it was there.</summary>
    private static bool Drop(List<string> list, string name) => list.RemoveAll(k => SameName(k, name)) > 0;

    /// <summary>The group lists in the settings (collapsed, made, hidden), copied, to put back if they can't be saved.</summary>
    private sealed record GroupLists(List<string> Collapsed, List<string> Created, List<string> Hidden);

    private GroupLists TakeGroupLists()
    {
        var s = settings.Current;
        return new([.. s.CollapsedGroups], [.. s.CreatedGroups], [.. s.HiddenGroups]);
    }

    /// <summary>
    /// Saves a change to the group lists. If the settings can't be written, the lists go back to <paramref name="before"/>
    /// and the Library says why, so nothing shows that a restart would lose.
    /// </summary>
    private bool SaveGroupLists(GroupLists before)
    {
        try
        {
            settings.Save();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var s = settings.Current;
            (s.CollapsedGroups, s.CreatedGroups, s.HiddenGroups) = (before.Collapsed, before.Created, before.Hidden);
            ErrorMessage = ex.Message;
            ApplyFilter();
            return false;
        }
    }

    /// <summary>
    /// Saves instances after a change made to them in memory. One that can't be saved gets its old state back (and the
    /// Library says why), so what shows is what's on disk. Returns those saved.
    /// </summary>
    private async Task<List<InstanceItem>> SaveOrRevertAsync(IReadOnlyList<(InstanceItem Item, Action Revert)> changes)
    {
        var saved = new List<InstanceItem>();
        foreach (var (item, revert) in changes)
        {
            if (await SaveAsync(item))
            {
                saved.Add(item);
                continue;
            }
            revert();
            item.RefreshHidden();
        }
        if (saved.Count < changes.Count)
            ApplyFilter();
        return saved;
    }

    /// <summary>"New group…": an empty section to drag instances into, kept until removed. An existing name is just opened.</summary>
    public InstanceGroup? CreateGroup(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            return null;
        var s = settings.Current;
        var before = TakeGroupLists();
        if (!GroupNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            s.CreatedGroups.Add(name);
        Drop(s.CollapsedGroups, name);
        if (!SaveGroupLists(before))
            return null;
        if (!string.IsNullOrWhiteSpace(SearchText)) // A search would hide the empty section just made.
            SearchText = "";
        if (s.IsGroupHidden(name))
            ShowHidden = true;
        ApplyFilter();
        return Groups.FirstOrDefault(g => SameName(g.Name, name));
    }

    /// <summary>Hides a whole group (its instances count as hidden wherever they're listed), or shows it again.</summary>
    public void SetGroupHidden(string name, bool hidden, bool offerUndo = true)
    {
        var s = settings.Current;
        if (s.IsGroupHidden(name) == hidden)
            return;
        var before = TakeGroupLists();
        if (hidden)
            s.HiddenGroups.Add(name.Trim());
        else
            Drop(s.HiddenGroups, name);
        if (!SaveGroupLists(before))
            return;
        ApplyFilter();
        UpdateJumpList();
        if (hidden && offerUndo)
            OfferUndo(Loc.Format("Library_HidGroup", name), () =>
            {
                SetGroupHidden(name, false);
                return Task.CompletedTask;
            });
    }

    /// <summary>
    /// Removes a group: its instances stay, ungrouped (another launcher's instance records "none" in Fluorite's notes).
    /// Undo puts them back in it, with the group's settings (hidden, collapsed, kept while empty).
    /// </summary>
    public async Task RemoveGroupAsync(string name)
    {
        var s = settings.Current;
        var members = _all.Where(i => SameName(i.Group, name)).ToList();
        var created = s.CreatedGroups.Any(k => SameName(k, name));
        if (members.Count == 0 && !created)
            return; // Gone already (a second Del while the first was saving): the first one's Undo stays.
        var before = TakeGroupLists();
        var hidden = s.IsGroupHidden(name);
        var collapsed = Drop(s.CollapsedGroups, name);
        Drop(s.CreatedGroups, name);
        Drop(s.HiddenGroups, name);
        if (!SaveGroupLists(before))
            return;

        // In memory first, so the section goes at once and a second Remove finds nothing; then to disk.
        var old = members.Select(i => (Item: i, Group: i.Instance.Manifest.Group)).ToList();
        foreach (var item in members)
            item.Instance.Manifest.Group = item.IsExternal ? "" : null;
        ApplyFilter();
        var saved = await SaveOrRevertAsync(old.Select(o => (o.Item, (Action)(() => o.Item.Instance.Manifest.Group = o.Group))).ToList());
        if (hidden)
            UpdateJumpList();

        OfferUndo(Loc.Format("Library_RemovedGroup", name), async () =>
        {
            var undoBefore = TakeGroupLists();
            if (created && !s.CreatedGroups.Any(k => SameName(k, name)))
                s.CreatedGroups.Add(name);
            if (hidden && !s.IsGroupHidden(name))
                s.HiddenGroups.Add(name);
            if (collapsed && !s.CollapsedGroups.Any(k => SameName(k, name)))
                s.CollapsedGroups.Add(name);
            SaveGroupLists(undoBefore);
            var back = old.Where(o => saved.Contains(o.Item))
                .Select(o => (o.Item, o.Group, Now: o.Item.Instance.Manifest.Group)).ToList();
            foreach (var b in back)
                b.Item.Instance.Manifest.Group = b.Group;
            ApplyFilter();
            await SaveOrRevertAsync(back.Select(b => (b.Item, (Action)(() => b.Item.Instance.Manifest.Group = b.Now))).ToList());
            if (hidden)
                UpdateJumpList();
        });
    }

    /// <summary>Selects every instance of a section (opening it), for a move, hide or delete of them all.</summary>
    public void SelectGroup(InstanceGroup group)
    {
        if (!group.IsExpanded)
            ToggleGroup(group);
        foreach (var item in group.Items)
            item.IsSelected = true;
        _anchor = group.Items.LastOrDefault() ?? _anchor;
        UpdateSelection();
    }

    private void UpdateJumpList() => _ = jumpList.UpdateAsync(_all.Select(i => i.Instance).ToList());

    /// <summary>One Undo at a time: a new one makes the last one final.</summary>
    private void OfferUndo(string message, Func<Task> undo)
    {
        var offer = new UndoOffer(message, undo);
        _ = PendingUndo?.CommitAsync();
        offer.Finished += (_, _) =>
        {
            if (PendingUndo == offer)
                PendingUndo = null;
        };
        PendingUndo = offer;
    }

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

    public bool HasHidden => HiddenCount > 0 || ShowHidden || HasHiddenEmptyGroups;

    /// <summary>A hidden group with nothing in it: no instance to count, but the toggle must still reach it.</summary>
    private bool HasHiddenEmptyGroups { get; set; }

    /// <summary>"Show hidden (3)", in the Library's menu.</summary>
    public string HiddenToggleText => Loc.Format("Library_ShowHidden", HiddenCount);

    /// <summary>The count on the eye-slash toggle by the search box (none when only empty groups are hidden).</summary>
    public string HiddenCountText => HiddenCount > 0 ? HiddenCount.ToString(System.Globalization.CultureInfo.CurrentCulture) : "";

    /// <summary>Nothing shows, but hidden instances would: the empty state offers to show them.</summary>
    [ObservableProperty] public partial bool CanRevealHidden { get; set; }

    [RelayCommand]
    private void RevealHidden() => ShowHidden = true;

    /// <summary>"Hid “X”. Undo".</summary>
    [ObservableProperty] public partial UndoOffer? PendingUndo { get; set; }

    /// <summary>
    /// Hides instances, or shows them. Showing one that only its hidden group hides shows that group (its menu says so:
    /// "Show group “X”"); one hidden itself just comes out of hiding, and its group stays as it is. Hiding offers Undo;
    /// another launcher's instance keeps the flag in Fluorite's notes beside it.
    /// </summary>
    public async Task SetHiddenAsync(IReadOnlyList<InstanceItem> items, bool hidden, bool offerUndo = true)
    {
        var changed = items.Where(i => i.Instance.Manifest.Hidden != hidden).ToList();
        List<string> groups = hidden ? []
            : items.Where(i => i.IsGroupHidden && !i.Instance.Manifest.Hidden).Select(i => i.Group!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (changed.Count == 0 && groups.Count == 0)
            return;
        if (groups.Count > 0)
        {
            var before = TakeGroupLists();
            foreach (var g in groups)
                Drop(settings.Current.HiddenGroups, g);
            SaveGroupLists(before);
        }
        foreach (var item in changed)
        {
            item.Instance.Manifest.Hidden = hidden;
            item.RefreshHidden();
        }
        ApplyFilter();
        var saved = await SaveOrRevertAsync(changed.Select(i => (i, (Action)(() => i.Instance.Manifest.Hidden = !hidden))).ToList());
        UpdateJumpList();
        if (hidden && offerUndo && saved.Count > 0)
        {
            var message = saved.Count == 1 ? Loc.Format("Library_HidOne", saved[0].Name) : Loc.Format("Library_HidMany", saved.Count);
            OfferUndo(message, () => SetHiddenAsync(saved.Where(i => i.Instance.Manifest.Hidden).ToList(), false, offerUndo: false));
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
        _ = Task.Run(() => RemovalStash.CommitLeftovers(layout.InstancesDir)); // Instances deleted for good, but not all of them yet.
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
        foreach (var item in _all)
            item.IsGroupHidden = settings.Current.IsGroupHidden(item.Group);
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

    /// <summary>
    /// Sections in the sort order within each; while searching, collapsed groups open to show their matches. Groups made
    /// with "New group…" that nothing is in yet show too (not while searching: they can't match).
    /// </summary>
    private void RebuildGroups()
    {
        Groups.Clear();
        var s = settings.Current;
        var collapsed = s.CollapsedGroups;
        var searching = SearchText.Trim().Length > 0;
        var list = ViewIndex == 1;
        var empty = s.CreatedGroups.Where(c => !_all.Any(i => SameName(i.Group, c))).ToList();
        HasHiddenEmptyGroups = empty.Any(s.IsGroupHidden);
        OnPropertyChanged(nameof(HasHidden));
        var sections = Instances.Where(i => i.Group is not null)
            .GroupBy(i => i.Group!, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Name: g.First().Group!, Items: g.ToList()))
            .Concat(searching ? [] : empty.Where(e => ShowHidden || !s.IsGroupHidden(e)).Select(e => (Name: e.Trim(), Items: new List<InstanceItem>())))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase);
        foreach (var (name, items) in sections)
            Groups.Add(new InstanceGroup(name, items, hasHeader: true, searching || !collapsed.Contains(name, StringComparer.OrdinalIgnoreCase), list, s.IsGroupHidden(name)));
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
        var old = items.Select(i => (Item: i, Group: i.Instance.Manifest.Group, WasHidden: i.IsHidden)).ToList();
        foreach (var item in items)
            item.Instance.Manifest.Group = string.IsNullOrWhiteSpace(group) ? (item.IsExternal ? "" : null) : group.Trim();
        // A group dropped into while collapsed opens, so what was just moved there is seen.
        var before = TakeGroupLists();
        if (!string.IsNullOrWhiteSpace(group) && Drop(settings.Current.CollapsedGroups, group))
            SaveGroupLists(before);
        ApplyFilter();
        await SaveOrRevertAsync(old.Select(o => (o.Item, (Action)(() => o.Item.Instance.Manifest.Group = o.Group))).ToList());
        if (old.Any(o => o.Item.IsHidden != o.WasHidden)) // Into or out of a hidden group.
            UpdateJumpList();
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

    /// <summary>
    /// Renames a group for every instance in it; it stays collapsed, hidden or kept-while-empty as it was. A name in use
    /// already merges the two.
    /// </summary>
    public async Task RenameGroupAsync(string from, string to)
    {
        to = to.Trim();
        if (to.Length == 0)
            return;
        var old = _all.Where(i => SameName(i.Group, from)).Select(i => (Item: i, Group: i.Instance.Manifest.Group)).ToList();
        foreach (var (item, _) in old)
            item.Instance.Manifest.Group = to;
        var s = settings.Current;
        var before = TakeGroupLists();
        foreach (var list in new[] { s.CollapsedGroups, s.CreatedGroups, s.HiddenGroups })
        {
            if (Drop(list, from) && !list.Contains(to, StringComparer.OrdinalIgnoreCase))
                list.Add(to);
        }
        SaveGroupLists(before);
        ApplyFilter();
        await SaveOrRevertAsync(old.Select(o => (o.Item, (Action)(() => o.Item.Instance.Manifest.Group = o.Group))).ToList());
        UpdateJumpList();
    }

    /// <summary>A name for "New group…" that isn't taken: null if fine, else why not.</summary>
    public string? CheckNewGroupName(string name) =>
        GroupNames.FirstOrDefault(g => SameName(g, name)) is { } taken ? Loc.Format("Library_GroupExists", taken) : null;

    /// <summary>False (and the Library says why) when it couldn't be written.</summary>
    private async Task<bool> SaveAsync(InstanceItem item)
    {
        try
        {
            await registry.SaveAsync(item.Instance);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) // The last: its folder was removed from Fluorite.
        {
            ErrorMessage = ex.Message;
            return false;
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
            var copy = item.Instance.IsExternal
                ? await Fluorite.Modding.Modpacks.InstanceImporter.CopyAsync(layout, item.Instance)
                : await layout.DuplicateAsync(item.Instance, Loc.Format("Duplicate_Suffix", item.Name));
            // Its content index came along, with what the original dismissed under the original's id.
            try
            {
                await content.For(copy).MoveDismissedIssuesAsync(item.Instance.Id, copy.Id);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The copy's made; it only asks again about what the original dismissed.
            }
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

    /// <summary>Moves the instance folder to the Recycle Bin (<paramref name="permanent"/>: deletes it for good).</summary>
    public async Task DeleteAsync(InstanceItem item, bool permanent = false)
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
        string? error = null;
        try
        {
            await Task.Run(() => Remove(item.Instance.Directory, permanent));
        }
        catch (Exception ex) when (IsRemoveError(ex))
        {
            error = ex.Message;
        }
        await LoadAsync(); // It clears the message, so that's set after.
        if (error is not null)
            ErrorMessage = error;
    }

    /// <summary>
    /// The selection to the Recycle Bin (<paramref name="permanent"/>: deleted for good), except what can't go: a
    /// game that's running, another launcher's instance. Says how many were left, and how many failed (one that
    /// fails, a file in use, doesn't stop the rest).
    /// </summary>
    public async Task DeleteAsync(IReadOnlyList<InstanceItem> items, bool permanent = false)
    {
        var deletable = items.Where(i => !i.Instance.IsExternal && runner.Find(i.Instance) is null).ToList();
        var failed = 0;
        string? error = null;
        foreach (var item in deletable)
        {
            try
            {
                await Task.Run(() => Remove(item.Instance.Directory, permanent));
            }
            catch (Exception ex) when (IsRemoveError(ex))
            {
                failed++;
                error ??= ex.Message;
            }
        }
        await LoadAsync(); // It clears the message, so that's set after.
        var messages = new List<string>();
        if (items.Count > deletable.Count)
            messages.Add(Loc.Format("Library_DeleteKept", items.Count - deletable.Count));
        if (error is not null)
            messages.Add(Loc.Format("Library_DeleteFailed", failed, error));
        if (messages.Count > 0)
            ErrorMessage = string.Join(" ", messages);
    }

    /// <summary>
    /// To the Recycle Bin, or deleted for good. For good, the folder first leaves the Library in one move, all or
    /// nothing: a file in use stops it there, with the instance whole. Whatever then can't be deleted yet waits,
    /// hidden, for the next try (<see cref="LoadAsync"/>).
    /// </summary>
    private static void Remove(string directory, bool permanent)
    {
        if (permanent)
            RemovalStash.Create(Path.GetDirectoryName(directory)!, [directory], permanent: true).Commit();
        else
            RecycleBin.Send(directory);
    }

    private static bool IsRemoveError(Exception ex) => ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception;

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
