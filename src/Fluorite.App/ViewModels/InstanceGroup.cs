using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.App.Services;

namespace Fluorite.App.ViewModels;

/// <summary>
/// One section of the Library: a named group (Prism's groups, or Fluorite's own), or the instances in none. With no
/// named groups at all there's one section and no header, so the Library looks as it did without groups.
/// </summary>
public sealed partial class InstanceGroup : ObservableObject
{
    public InstanceGroup(string? name, IEnumerable<InstanceItem> items, bool hasHeader, bool isExpanded, bool isListView, bool isHidden = false)
    {
        Name = name;
        Items = new ObservableCollection<InstanceItem>(items); // AOT: a concrete list type for ItemsSource.
        HasHeader = hasHeader;
        IsExpanded = isExpanded;
        IsListView = isListView;
        IsHidden = isHidden;
    }

    /// <summary>Null for the ungrouped instances.</summary>
    public string? Name { get; }

    public bool IsNamed => Name is not null;

    public string Title => Name ?? Loc.Get("Library_Ungrouped");

    public ObservableCollection<InstanceItem> Items { get; }

    public bool HasHeader { get; }

    /// <summary>A group made with "New group…" that nothing is in yet: it shows where to drop instances.</summary>
    public bool IsEmpty => Items.Count == 0;

    public bool HasItems => Items.Count > 0;

    /// <summary>Hidden as a whole (shown only while "Show hidden" is on, dimmed, with an eye-slash).</summary>
    public bool IsHidden { get; }

    /// <summary>"Hide group" or "Show group", for its header's menu.</summary>
    public string HideText => Loc.Get(IsHidden ? "Library_ShowGroup" : "Library_HideGroup");

    public string HideGlyph => IsHidden ? "" : "";

    public string CountText => Items.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>The settings key for the collapsed state.</summary>
    public string Key => Name ?? "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowList), nameof(ShowEmpty), nameof(Chevron), nameof(HeaderName))]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGrid), nameof(ShowList))]
    public partial bool IsListView { get; set; }

    public bool ShowGrid => IsExpanded && !IsListView && HasItems;

    public bool ShowList => IsExpanded && IsListView && HasItems;

    public bool ShowEmpty => IsExpanded && IsEmpty;

    public string Chevron => IsExpanded ? "" : "";

    /// <summary>What Narrator reads for the header: "Modded, 5 instances, expanded" (", hidden" when it is).</summary>
    public string HeaderName => Loc.Format(
        (IsExpanded ? "Library_GroupExpanded" : "Library_GroupCollapsed") + (Items.Count == 1 ? "One" : ""), Title, Items.Count)
        + (IsHidden ? ", " + Loc.Get("Library_ItemHidden") : "");
}
