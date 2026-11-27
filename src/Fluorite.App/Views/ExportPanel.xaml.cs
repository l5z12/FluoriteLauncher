using System.Collections.ObjectModel;
using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Modpacks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Fluorite.App.Views;

/// <summary>
/// The Export dialog's body: the instance, the format (and what it can't hold), the pack's details, and a tree of the
/// game folder to choose from, with sizes. It starts from what the instance was last exported as.
/// </summary>
public sealed partial class ExportPanel : UserControl
{
    /// <summary>The formats in the order the dialog lists them (and <c>LastExportFormat</c> counts them).</summary>
    public static readonly PackFormat[] FormatOrder = [PackFormat.Modrinth, PackFormat.CurseForge, PackFormat.MultiMC, PackFormat.Packwiz];

    private static readonly string[] s_shortNames = ["Modrinth", "CurseForge", "Prism", "packwiz"];

    private readonly GameInstance _instance;
    private readonly PackExportState? _saved;
    private readonly bool _curseForge;
    private readonly bool _localOnlyDefault;
    private bool _localOnly;
    private bool _keepDisabled;
    private readonly List<ExportNode> _roots = [];
    private ObservableCollection<ExportNode> _rows = [];
    private readonly List<ExportNode> _all = [];
    private readonly CancellationTokenSource _measuring = new();
    private Dictionary<string, ExportTally>? _tallies;

    /// <param name="saved">What it was last exported as, to start from.</param>
    /// <param name="pack">The modpack it was installed from, whose name and version a first export starts from.</param>
    /// <param name="lastFormat">The format used last time for any instance (<see cref="FormatOrder"/>).</param>
    /// <param name="curseForge">Whether CurseForge is on in this build (without it, nothing can be linked from there).</param>
    /// <param name="localOnly">What Settings says: copy every file in, linking nothing.</param>
    public ExportPanel(GameInstance instance, PackExportState? saved, PackState? pack, int lastFormat, string? lastAuthor, bool running, bool curseForge,
        bool localOnly)
    {
        InitializeComponent();
        _instance = instance;
        _saved = saved;
        _curseForge = curseForge;
        _localOnlyDefault = localOnly;
        _localOnly = saved?.LocalOnly ?? localOnly;
        _keepDisabled = saved?.KeepDisabled ?? false;

        var item = new InstanceItem(instance);
        InstanceName.Text = instance.Name;
        InstanceVersion.Text = item.VersionText;
        InitialText.Text = item.Initial;
        IconImage.Source = item.Icon;
        InitialTile.Visibility = item.HasIcon ? Visibility.Collapsed : Visibility.Visible;

        // Emptied, they fall back to the instance's name and 1.0.0; the placeholders say so.
        NameBox.Text = saved?.Name ?? pack?.Name ?? instance.Name;
        NameBox.PlaceholderText = instance.Name;
        VersionBox.Text = saved?.Version ?? pack?.Version ?? "1.0.0";
        AuthorBox.Text = saved?.Author ?? lastAuthor ?? "";
        SummaryBox.Text = saved?.Summary ?? "";
        if (saved is { ExportedAt: var at } && at != default)
        {
            var exported = saved.ExportedFormat ?? saved.Format;
            var format = Array.IndexOf(FormatOrder, exported);
            LastExport.Text = Loc.Format("Export_LastExport", Formats.Ago(at), format >= 0 ? s_shortNames[format] : exported.ToString(), saved.ExportedVersion ?? saved.Version ?? "");
            ToolTipService.SetToolTip(LastExport, Formats.When(at));
            LastExport.Visibility = Visibility.Visible;
        }

        RunningBar.Message = Loc.Get("Export_Running");
        Show(RunningBar, running);

        BuildTree();
        var index = saved is not null ? Array.IndexOf(FormatOrder, saved.Format) : lastFormat;
        FormatList.SelectedIndex = Math.Clamp(index, 0, FormatOrder.Length - 1);
        UpdateFormat();
        UpdateCount();

        Loaded += (_, _) =>
        {
            FitWindow();
            if (XamlRoot is { } root)
                root.Changed += XamlRoot_Changed;
        };
        Unloaded += (_, _) =>
        {
            _measuring.Cancel(); // The dialog closed: stop measuring.
            if (XamlRoot is { } root)
                root.Changed -= XamlRoot_Changed;
        };
        _ = MeasureAsync();
    }

    // ── Layout ────────────────────────────────────────────────────────────

    /// <summary>The panel's width when the window has room, and the narrowest that still fits the list beside the details.</summary>
    private const double WideWidth = 920, TwoColumnWidth = 780;

    /// <summary>The list's least height beside the details, and its height under them in a narrow window.</summary>
    private const double MinListHeight = 360, NarrowListHeight = 320;

    private bool _wide = true;

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => FitWindow();

    /// <summary>
    /// As wide as the window allows (a dialog doesn't shrink what's inside it, so a fixed width would be cut off): the
    /// list beside the details, or under them when there isn't room for both.
    /// </summary>
    private void FitWindow()
    {
        if (XamlRoot is not { } root)
            return;
        Root.Width = Math.Clamp(root.Size.Width - 140, 320, WideWidth); // The dialog's padding and the scroll bar's gutter.
        _wide = Root.Width >= TwoColumnWidth;
        ListColumn.Width = _wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetRow(ListPart, _wide ? 1 : 2);
        Grid.SetColumn(ListPart, _wide ? 1 : 0);
        ListPart.Margin = new Thickness(_wide ? 28 : 0, 0, 0, 0);
        FitList();
    }

    /// <summary>
    /// Beside the details, the list is as tall as they are (Advanced opening makes it taller). Its height is set, not
    /// left to the layout: a list given all the height it asks for shows every row at once and stops scrolling.
    /// </summary>
    private void FitList() => ListPart.Height = _wide ? Math.Max(DetailsPart.ActualHeight, MinListHeight) : NarrowListHeight;

    private void DetailsPart_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_wide)
            FitList();
    }

    public int FormatIndex => Math.Max(FormatList.SelectedIndex, 0);

    public PackFormat Format => FormatOrder[FormatIndex];

    /// <summary>As typed, or the instance's name when left empty.</summary>
    public string PackName => string.IsNullOrWhiteSpace(NameBox.Text) ? _instance.Name : NameBox.Text.Trim();

    public string PackVersion => string.IsNullOrWhiteSpace(VersionBox.Text) ? "1.0.0" : VersionBox.Text.Trim();

    /// <summary>Null when empty or when the format has no place for it.</summary>
    public string? Author => AuthorBox.IsEnabled && !string.IsNullOrWhiteSpace(AuthorBox.Text) ? AuthorBox.Text.Trim() : null;

    /// <summary>The format has a place for an author (CurseForge, packwiz).</summary>
    public bool UsesAuthor => AuthorBox.IsEnabled;

    public string? Summary => SummaryBox.IsEnabled && !string.IsNullOrWhiteSpace(SummaryBox.Text) ? SummaryBox.Text.Trim() : null;

    /// <summary>Copy every file in, linking nothing (Advanced; what Settings says to start with).</summary>
    public bool LocalOnly => _localOnly;

    /// <summary>Keep turned-off files (Advanced); Prism packs keep them anyway.</summary>
    public bool KeepDisabled => _keepDisabled;

    /// <summary>What goes in the pack: every path ticked as a whole.</summary>
    public IReadOnlyList<string> Include
    {
        get
        {
            var included = new List<string>();
            Walk(_roots, included, excluded: null);
            return included;
        }
    }

    /// <summary>The choices as they stand, to start from next time (and when it was last exported, kept until it is again).</summary>
    public PackExportState ToState()
    {
        var state = new PackExportState
        {
            Format = Format,
            Name = PackName,
            Version = PackVersion,
            // Kept even where this format has no place for them: the next export may be one that does.
            Author = string.IsNullOrWhiteSpace(AuthorBox.Text) ? null : AuthorBox.Text.Trim(),
            Summary = string.IsNullOrWhiteSpace(SummaryBox.Text) ? null : SummaryBox.Text.Trim(),
            // What was last exported stays until an export finishes.
            ExportedAt = _saved?.ExportedAt ?? default,
            ExportedFormat = _saved?.ExportedFormat,
            ExportedVersion = _saved?.ExportedVersion,
            LocalOnly = _localOnly == _localOnlyDefault ? null : _localOnly, // Kept only when it differs: Settings stays in charge otherwise.
            KeepDisabled = _keepDisabled,
        };
        Walk(_roots, state.Included, state.Excluded);
        return state;
    }

    private static void Walk(IEnumerable<ExportNode> nodes, List<string> included, List<string>? excluded)
    {
        foreach (var node in nodes)
        {
            switch (node.State)
            {
                case true:
                    included.Add(node.Path);
                    break;
                case false:
                    excluded?.Add(node.Path);
                    break;
                default:
                    Walk(node.Children ?? [], included, excluded);
                    break;
            }
        }
    }

    /// <summary>
    /// Exporting again: the version, selected, ready for the next one. The first time: the name.
    /// </summary>
    public void FocusFirst()
    {
        var box = _saved is { ExportedAt: var at } && at != default ? VersionBox : NameBox;
        box.Focus(FocusState.Programmatic);
        box.SelectAll();
    }

    // ── Format ────────────────────────────────────────────────────────────

    private void Formats_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateFormat();
        UpdateCount();
    }

    private void UpdateFormat()
    {
        var format = Format;
        var name = s_shortNames[FormatIndex];

        // The loader this format can't hold (pointing to Prism when it can), and OptiFine as a Forge mod, which none can.
        var m = _instance.Manifest;
        var notes = new List<string>();
        if (ModpackExporter.UnsupportedLoader(m, format) is { } unsupported)
        {
            notes.Add(Loc.Format("Export_LoaderUnsupported", name, InstanceItem.LoaderName(unsupported), m.MinecraftVersion ?? ""));
            if (format != PackFormat.MultiMC && ModpackExporter.UnsupportedLoader(m, PackFormat.MultiMC) is null)
                notes[^1] += " " + Loc.Get("Export_LoaderPrismCan");
        }
        if (ComponentResolver.OptiFineAsMod(m) is not null)
            notes.Add(Loc.Get("Export_OptiFineLeftOut"));
        Show(LoaderBar, notes.Count > 0);
        LoaderBar.Message = string.Join("\n", notes);

        // Where each format puts what: Modrinth has no author, CurseForge no summary, Prism keeps the summary as notes.
        SetField(AuthorField, AuthorBox, format is PackFormat.CurseForge or PackFormat.Packwiz, name);
        SetField(SummaryField, SummaryBox, format is not PackFormat.CurseForge, name);
    }

    // ── Advanced ──────────────────────────────────────────────────────────

    /// <summary>Turned-off files stay out of this export: the format leaves them out and they aren't to be kept.</summary>
    private bool DropsTurnedOff => ModpackExporter.DropsDisabled(Format) && !_keepDisabled;

    /// <summary>Prism packs always hold every file, and so do CurseForge packs when CurseForge is off in this build.</summary>
    private bool CopiesAll => Format == PackFormat.MultiMC || (Format == PackFormat.CurseForge && !_curseForge);

    /// <summary>Opened at the foot of a dialog that may scroll: what it holds comes into view.</summary>
    private void Advanced_Expanding(Expander sender, ExpanderExpandingEventArgs args) =>
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => (sender.Content as UIElement)?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = true }));

    private void LocalOnly_Click(object sender, RoutedEventArgs e)
    {
        _localOnly = LocalOnlyBox.IsChecked == true;
        UpdateCount();
    }

    private void KeepDisabled_Click(object sender, RoutedEventArgs e)
    {
        _keepDisabled = KeepDisabledBox.IsChecked == true;
        UpdateCount();
    }

    /// <summary>
    /// The two choices as this format takes them (ticked and turned off where it does that anyway, the caption saying
    /// so), and the header's summary of what differs from a plain export.
    /// </summary>
    private void UpdateAdvanced(int? disabled)
    {
        var format = Format;
        var copiesAll = CopiesAll;
        LocalOnlyBox.IsEnabled = !copiesAll;
        LocalOnlyBox.IsChecked = copiesAll || _localOnly;
        LocalOnlyCaption.Text = format switch
        {
            PackFormat.MultiMC => Loc.Get("Export_LocalOnlyPrism"),
            PackFormat.CurseForge when !_curseForge => Loc.Get("Export_LocalOnlyNoCurseForge"),
            PackFormat.Modrinth => Loc.Format("Export_LocalOnlyCaption", "Modrinth"),
            PackFormat.CurseForge => Loc.Format("Export_LocalOnlyCaption", "CurseForge"),
            _ => Loc.Format("Export_LocalOnlyCaption", Loc.Get("Export_ModrinthOrCurseForge")),
        };

        var keeps = !ModpackExporter.DropsDisabled(format);
        KeepDisabledBox.IsEnabled = !keeps;
        KeepDisabledBox.IsChecked = keeps || _keepDisabled;
        KeepDisabledCaption.Text = keeps ? Loc.Get("Export_KeepDisabledPrism")
            : disabled switch
            {
                null => "",
                0 => Loc.Get("Export_KeepDisabledNone"),
                var n => Loc.Format("Export_KeepDisabledCount", Formats.Count(n.Value)),
            };
        KeepDisabledCaption.Visibility = KeepDisabledCaption.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

        var parts = new List<string>();
        if (!copiesAll && _localOnly)
            parts.Add(Loc.Get("Export_SummaryLocalOnly"));
        if (!keeps && disabled > 0)
            parts.Add(_keepDisabled ? Loc.Get("Export_SummaryKeptDisabled") : Loc.Format("Export_SummaryDroppedDisabled", Formats.Count(disabled.Value)));
        AdvancedSummary.Text = string.Join(" · ", parts);
    }

    /// <summary>A closed InfoBar still takes the panel's spacing twice over: collapsed, it takes none.</summary>
    private static void Show(InfoBar bar, bool open)
    {
        bar.IsOpen = open;
        bar.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A field the format has no place for: turned off, and its tooltip says why (a turned-off box shows none itself).</summary>
    private static void SetField(Grid field, TextBox box, bool used, string format)
    {
        box.IsEnabled = used;
        ToolTipService.SetToolTip(field, used ? null : Loc.Format("Export_FieldUnused", format));
    }

    // ── The tree ──────────────────────────────────────────────────────────

    private void BuildTree()
    {
        foreach (var candidate in ModpackExporter.DefaultSelection(_instance.GameDirectory))
        {
            var node = new ExportNode(candidate.Name, candidate.Name, candidate.IsDirectory, 0, null, candidate.IncludedByDefault);
            node.State = InitialState(node);
            Add(node);
            _roots.Add(node);
        }
        ShowRows();
        EmptyText.Visibility = _roots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// The rows shown, made anew in one go. Rows inserted and removed one by one left an empty row behind in the
    /// list after folders were opened and closed.
    /// </summary>
    private void ShowRows()
    {
        _rows = new ObservableCollection<ExportNode>(Visible(_roots));
        Tree.ItemsSource = _rows;
    }

    /// <summary>The nodes and, under each open folder, what's in it.</summary>
    private static IEnumerable<ExportNode> Visible(IEnumerable<ExportNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            if (node.IsExpanded)
            {
                foreach (var inner in Visible(node.Children ?? []))
                    yield return inner;
            }
        }
    }

    /// <summary>
    /// As it was left last time; partly ticked when something inside was chosen on its own (its files are read then, to
    /// say which); otherwise its default.
    /// </summary>
    private bool? InitialState(ExportNode node)
    {
        if (_saved?.WasIncluded(node.Path) is { } was)
            return was;
        if (node.IsDirectory && _saved?.HasChoicesInside(node.Path) == true)
        {
            LoadChildren(node); // Untouched (null) until now, so each thing in it is restored on its own.
            return node.Children is { Count: > 0 } children ? Combined(children) : node.DefaultIncluded;
        }
        return node.DefaultIncluded;
    }

    private void Add(ExportNode node)
    {
        _all.Add(node);
        if (_tallies is not null)
            node.Tally = _tallies.GetValueOrDefault(node.Path);
    }

    /// <summary>Reads a folder's own files and folders (once), folders first, each ticked as the folder is.</summary>
    private void LoadChildren(ExportNode node)
    {
        if (node.Children is not null)
            return;
        node.Children = [];
        IEnumerable<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(Path.Combine(_instance.GameDirectory, node.Path)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        var children = entries
            .Select(p => (Name: Path.GetFileName(p), IsDir: Directory.Exists(p)))
            .Where(e => e.IsDir || !e.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) // Never exported.
            .OrderByDescending(e => e.IsDir).ThenBy(e => ExportNode.Readable(e.Name), StringComparer.CurrentCultureIgnoreCase); // As they read.
        foreach (var (name, isDir) in children)
        {
            var child = new ExportNode(name, node.Path + "/" + name, isDir, node.Depth + 1, node, node.DefaultIncluded);
            node.Children.Add(child);
            Add(child);
            child.State = node.State is { } whole ? whole : InitialState(child);
        }
    }

    /// <summary>True when all are ticked, false when none are, null when some are.</summary>
    private static bool? Combined(IReadOnlyCollection<ExportNode> nodes) =>
        nodes.All(n => n.State == true) ? true
        : nodes.All(n => n.State == false) ? false
        : null;

    private static void SetState(ExportNode node, bool value)
    {
        node.State = value;
        foreach (var child in node.Children ?? [])
            SetState(child, value);
    }

    /// <summary>Each folder above <paramref name="node"/> becomes ticked, not, or partly, by what's in it.</summary>
    private static void UpdateAncestors(ExportNode node)
    {
        for (var p = node.Parent; p is not null; p = p.Parent)
            p.State = Combined(p.Children!); // Never empty: node is in it.
    }

    /// <summary>A click ticks everything in it, or (when it's all ticked) nothing; a partly ticked folder ticks all of it.</summary>
    private void Check_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ExportNode>(sender) is not { } node)
            return;
        SetState(node, node.State != true);
        UpdateAncestors(node);
        UpdateCount();
    }

    private void Chevron_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ExportNode>(sender) is { } node)
            SetExpanded(node, !node.IsExpanded, FocusState.Programmatic);
    }

    /// <summary>Right opens a folder, Left closes it (or, on something inside, goes to its folder), as in Explorer.</summary>
    private void Check_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (Bind.ItemOf<ExportNode>(sender) is not { } node)
            return;
        switch (e.Key)
        {
            case VirtualKey.Right when node.IsDirectory && !node.IsExpanded:
                SetExpanded(node, true, FocusState.Keyboard);
                break;
            case VirtualKey.Left when node.IsExpanded:
                SetExpanded(node, false, FocusState.Keyboard);
                break;
            case VirtualKey.Left when node.Parent is { } parent:
                FocusRow(parent, FocusState.Keyboard);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    /// <summary>Opens or closes a folder; focus in the list stays on it (the rows are made anew).</summary>
    private void SetExpanded(ExportNode node, bool expand, FocusState focus)
    {
        if (!node.IsDirectory || node.IsExpanded == expand)
            return;
        var hadFocus = ListHasFocus();
        if (expand)
            LoadChildren(node);
        node.IsExpanded = expand;
        ShowRows();
        var index = _rows.IndexOf(node);
        if (expand) // Its contents come into view, as far as they fit with the folder still in view.
            ScrollTo(index, index + Visible(node.Children ?? []).Count(), animate: true);
        if (hadFocus)
            FocusRow(node, focus);
    }

    private bool ListHasFocus()
    {
        for (var d = XamlRoot is { } root ? FocusManager.GetFocusedElement(root) as DependencyObject : null; d is not null; d = VisualTreeHelper.GetParent(d))
        {
            if (ReferenceEquals(d, Tree))
                return true;
        }
        return false;
    }

    /// <summary>Rows are all one height, so where they are is known without asking the list for them.</summary>
    private const double RowHeight = 32, ListPadding = 4;

    /// <summary>Scrolls the least that shows rows <paramref name="first"/> to <paramref name="last"/>, or as many as fit from the first.</summary>
    private void ScrollTo(int first, int last, bool animate)
    {
        TreeScroll.UpdateLayout(); // The new rows' height first, or the scroll stops at the old end.
        double top = ListPadding + first * RowHeight, bottom = ListPadding + (last + 1) * RowHeight;
        var offset = TreeScroll.VerticalOffset;
        var view = TreeScroll.ViewportHeight;
        double? target = bottom > offset + view ? Math.Min(top, bottom - view) : top < offset ? top : null;
        if (target is { } t)
            TreeScroll.ChangeView(null, Math.Max(0, t), null, disableAnimation: !animate);
    }

    private void FocusRow(ExportNode node, FocusState focus)
    {
        var index = _rows.IndexOf(node);
        if (index < 0)
            return;
        ScrollTo(index, index, animate: false);
        Tree.UpdateLayout();
        if (Tree.TryGetElement(index) is Grid row && row.Children.OfType<CheckBox>().FirstOrDefault() is { } box)
            box.Focus(focus);
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SetRoots(_ => true);

    private void SelectNone_Click(object sender, RoutedEventArgs e) => SetRoots(_ => false);

    private void Defaults_Click(object sender, RoutedEventArgs e) => SetRoots(n => n.DefaultIncluded);

    private void SetRoots(Func<ExportNode, bool> value)
    {
        foreach (var root in _roots)
            SetState(root, value(root));
        UpdateCount();
    }

    // ── Sizes ─────────────────────────────────────────────────────────────

    /// <summary>Measures everything once, in the background; sizes show as they're known.</summary>
    private async Task MeasureAsync()
    {
        var gameDir = _instance.GameDirectory;
        var roots = _roots.Select(r => r.Path).ToList();
        var ct = _measuring.Token;
        try
        {
            _tallies = await Task.Run(() => Measure(gameDir, roots, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        foreach (var node in _all)
            node.Tally = _tallies.GetValueOrDefault(node.Path);
        UpdateCount();
    }

    /// <summary>Relative path → tally, for every file and folder under <paramref name="roots"/>.</summary>
    private static Dictionary<string, ExportTally> Measure(string gameDir, IReadOnlyList<string> roots, CancellationToken ct)
    {
        var map = new Dictionary<string, ExportTally>(StringComparer.OrdinalIgnoreCase);
        // Hidden and system files are exported too, so they count.
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 };

        static ExportTally OfFile(FileInfo f) => f.Name.EndsWith(ContentLocation.DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? new(f.Length, 1, 1, f.Length)
            : new(f.Length, 1, 0, 0);

        ExportTally Folder(DirectoryInfo dir, string relative)
        {
            ct.ThrowIfCancellationRequested();
            var total = default(ExportTally);
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                var path = relative + "/" + entry.Name;
                ExportTally tally;
                if (entry is DirectoryInfo sub)
                    tally = sub.Attributes.HasFlag(FileAttributes.ReparsePoint) ? default : Folder(sub, path); // A link could loop.
                else if (entry.Name.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                    continue;
                else
                    tally = OfFile((FileInfo)entry);
                map[path] = tally;
                total += tally;
            }
            map[relative] = total;
            return total;
        }

        foreach (var root in roots)
        {
            var full = Path.Combine(gameDir, root);
            try
            {
                if (Directory.Exists(full))
                    Folder(new DirectoryInfo(full), root);
                else if (File.Exists(full))
                    map[root] = OfFile(new FileInfo(full));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
        return map;
    }

    /// <summary>"12 of 20 chosen · 384 MB", and Advanced (how many turned-off files there are among them).</summary>
    private void UpdateCount()
    {
        var chosen = _roots.Count(r => r.State != false);
        var tally = _tallies is null ? (ExportTally?)null : Sum(_roots);
        CountText.Text = tally is { } t
            ? Loc.Format("Export_ChosenSize", chosen, _roots.Count, Formats.Size(t.Bytes - (DropsTurnedOff ? t.DisabledBytes : 0)))
            : Loc.Format("Export_SelectedCount", chosen, _roots.Count);
        // Linked mods don't add to the pack; with nothing linked, the size is about what it takes before compression.
        ToolTipService.SetToolTip(CountText, tally is null ? null : Loc.Get(CopiesAll || _localOnly ? "Export_SizeHintLocal" : "Export_SizeHint"));
        UpdateAdvanced(tally?.Disabled);
    }

    private ExportTally Sum(IEnumerable<ExportNode> nodes)
    {
        var total = default(ExportTally);
        foreach (var node in nodes)
        {
            total += node.State switch
            {
                true => _tallies!.GetValueOrDefault(node.Path),
                false => default,
                _ => Sum(node.Children ?? []),
            };
        }
        return total;
    }
}
