using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.Modding.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;

namespace Fluorite.App.Views;

public sealed partial class InstancePage : Page
{
    public InstancePage()
    {
        InitializeComponent();
    }

    public InstanceViewModel ViewModel { get; } = App.Get<InstanceViewModel>();

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Get<ContentInstallService>().ComponentsChanged += Installer_ComponentsChanged;
        if (e.Parameter is InstanceItem item)
        {
            var initializing = ViewModel.InitializeAsync(item); // Knows the instance at once; the tabs need it.
            // Back (or Forward) to this instance: on the tab it was left on, not the first one.
            if (e.NavigationMode is NavigationMode.Back or NavigationMode.Forward && item.ReturnTab is { } tab)
                ShowTab(tab);
            await initializing;
        }
        ApplyAutoScroll();
    }

    /// <summary>An update moved the loader to the version the new mods need: the Version tab and card show it.</summary>
    private async void Installer_ComponentsChanged(object? sender, Fluorite.Core.Instances.GameInstance instance)
    {
        if (ViewModel.Instance is not { } shown || !string.Equals(shown.GameDirectory, instance.GameDirectory, StringComparison.OrdinalIgnoreCase))
            return;
        ViewModel.Item?.Refresh();
        await ViewModel.ReloadVersionAsync();
    }

    /// <summary>
    /// A page is made per visit, but the running game (and its log) outlives it. Let go of the shared log so a page
    /// that's gone never receives its updates (updating a stale page's list fails with E_FAIL and ends the app).
    /// </summary>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        if (ViewModel.Item is { } shown)
            shown.ReturnTab = Tabs.SelectedItem?.Tag as string;
        App.Get<ContentInstallService>().ComponentsChanged -= Installer_ComponentsChanged;
        Bindings.StopTracking();
        LogList.ItemsSource = null;
        // Drop the tabs' lists and images now: the page's controls otherwise stay alive until the GC finalizes them.
        foreach (var panel in ContentPanels)
        {
            panel.ViewModel?.Release();
            panel.ViewModel = null;
        }
        WorldsTab.ViewModel?.Detach();
        WorldsTab.ViewModel = null;
        ServersTab.ViewModel?.Detach();
        ServersTab.ViewModel = null;
        ScreenshotsTab.ViewModel?.ReleaseThumbnails();
        ScreenshotsTab.ViewModel = null;
        if (SnapshotsTab.ViewModel is { } snapshots)
        {
            snapshots.Detach();
            snapshots.Restored -= Snapshots_Restored;
        }
        SnapshotsTab.ViewModel = null;
    }

    /// <summary>Ctrl+F: the filter box of the content or worlds tab on screen; false when this tab has none.</summary>
    public bool FocusSearch() =>
        ContentPanels.FirstOrDefault(p => p.Visibility == Visibility.Visible) is { } panel ? panel.FocusFilter()
        : WorldsTab.Visibility == Visibility.Visible && WorldsTab.FocusFilter();

    /// <summary>Worlds, servers, screenshots and snapshots get their view model (and first scan) when first shown.</summary>
    private void ShowGameData(string? tag)
    {
        WorldsTab.Visibility = tag == "Worlds" ? Visibility.Visible : Visibility.Collapsed;
        ServersTab.Visibility = tag == "Servers" ? Visibility.Visible : Visibility.Collapsed;
        ScreenshotsTab.Visibility = tag == "Screenshots" ? Visibility.Visible : Visibility.Collapsed;
        SnapshotsTab.Visibility = tag == "Snapshots" ? Visibility.Visible : Visibility.Collapsed;
        if (ViewModel.Instance is not { } instance)
            return;
        switch (tag)
        {
            case "Worlds" when WorldsTab.ViewModel is null:
            {
                var vm = App.Get<WorldsViewModel>();
                vm.Launch = ViewModel.Launch;
                vm.ShowDataPacks = ShowDataPacksFor;
                WorldsTab.ViewModel = vm;
                _ = vm.InitializeAsync(instance);
                break;
            }
            case "Servers" when ServersTab.ViewModel is null:
            {
                var vm = App.Get<ServersViewModel>();
                vm.Launch = ViewModel.Launch;
                ServersTab.ViewModel = vm;
                _ = vm.InitializeAsync(instance);
                break;
            }
            case "Screenshots" when ScreenshotsTab.ViewModel is null:
            {
                var vm = App.Get<ScreenshotsViewModel>();
                ScreenshotsTab.ViewModel = vm;
                _ = vm.InitializeAsync(instance);
                break;
            }
            case "Screenshots":
                _ = ScreenshotsTab.ViewModel.RefreshAsync(); // New ones appear after every session.
                break;
            case "Snapshots" when SnapshotsTab.ViewModel is null:
            {
                var vm = App.Get<SnapshotsViewModel>();
                vm.Restored += Snapshots_Restored;
                vm.Initialize(instance);
                SnapshotsTab.ViewModel = vm;
                break;
            }
            case "Snapshots":
                SnapshotsTab.ViewModel.Refresh(); // Updates take them too.
                break;
        }
    }

    /// <summary>A snapshot is back: the page shows its loader setup, and every list rescans.</summary>
    private async void Snapshots_Restored(object? sender, EventArgs e)
    {
        ViewModel.Item?.Refresh();
        ViewModel.RefreshPackInfo();
        await ViewModel.ReloadVersionAsync();
        foreach (var panel in ContentPanels)
            _ = panel.ViewModel?.RefreshAsync();
    }

    /// <summary>Switches to the Log tab (after a crash, while this page is already open).</summary>
    public void ShowLog() => ShowTab("Log");

    /// <summary>Switches to the tab with this tag ("Log", "Mod", "Worlds"…), if there is one.</summary>
    public void ShowTab(string tag)
    {
        if (!Tabs.IsLoaded)
        {
            // A page just navigated to: once its tabs are there.
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                Tabs.Loaded -= OnLoaded;
                ShowTab(tag);
            }
            Tabs.Loaded += OnLoaded;
            return;
        }
        if (Tabs.Items.FirstOrDefault(i => (string)i.Tag == tag) is { } item)
            Tabs.SelectedItem = item;
    }

    /// <summary>"Data packs" on a world: switch to that tab with the world picked.</summary>
    private void ShowDataPacksFor(string worldFolder)
    {
        Tabs.SelectedItem = Tabs.Items.First(i => (string)i.Tag == nameof(ContentKind.DataPack));
        if (DataPackPanel.ViewModel is { } vm && vm.Worlds.Contains(worldFolder))
            vm.SelectedWorld = worldFolder;
    }

    private IEnumerable<ContentPanel> ContentPanels => [ModPanel, ResourcePackPanel, ShaderPackPanel, DataPackPanel];

    private ContentPanel? PanelFor(string? tag) => tag switch
    {
        nameof(ContentKind.Mod) => ModPanel,
        nameof(ContentKind.ResourcePack) => ResourcePackPanel,
        nameof(ContentKind.ShaderPack) => ShaderPackPanel,
        nameof(ContentKind.DataPack) => DataPackPanel,
        _ => null,
    };

    /// <summary>Content tabs get their view model (and first scan) when first shown.</summary>
    private void ShowContent(string? tag)
    {
        foreach (var p in ContentPanels)
            p.Visibility = Visibility.Collapsed;
        if (PanelFor(tag) is not { } panel || ViewModel.Instance is not { } instance)
            return;
        panel.Visibility = Visibility.Visible;
        if (panel.ViewModel is null)
        {
            var vm = App.Get<ContentListViewModel>();
            vm.Initialize(instance, Enum.Parse<ContentKind>(tag!));
            panel.ViewModel = vm;
            _ = vm.RefreshAsync();
        }
    }

    private void LogList_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAutoScroll();
        if (_dragHandlersAdded)
            return;
        // The pressed row captures the pointer and handles its events, so listen to handled ones too.
        LogList.AddHandler(PointerPressedEvent, new PointerEventHandler(LogList_PointerPressed), handledEventsToo: true);
        LogList.AddHandler(PointerMovedEvent, new PointerEventHandler(LogList_PointerMoved), handledEventsToo: true);
        LogList.AddHandler(PointerReleasedEvent, new PointerEventHandler(LogList_PointerEnded), handledEventsToo: true);
        LogList.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(LogList_PointerEnded), handledEventsToo: true);
        _dragHandlersAdded = true;
    }

    // ── Log selection: drag across lines like text, copy as text ────────

    private bool _dragHandlersAdded;
    private int _dragAnchor = -1;
    private int _dragEnd = -1;

    private void LogList_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(LogList);
        var modifiers = e.KeyModifiers & (Windows.System.VirtualKeyModifiers.Shift | Windows.System.VirtualKeyModifiers.Control);
        // Shift/Ctrl+click keep the list's own range and toggle selection.
        _dragAnchor = point.Properties.IsLeftButtonPressed && modifiers == 0 ? IndexAt(point.Position) : -1;
        _dragEnd = _dragAnchor;
    }

    private void LogList_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_dragAnchor < 0)
            return;
        var point = e.GetCurrentPoint(LogList);
        if (!point.Properties.IsLeftButtonPressed)
        {
            _dragAnchor = -1;
            return;
        }
        var y = point.Position.Y;
        // Past the top or bottom edge: scroll that way, faster the further out.
        if (FindScrollViewer(LogList) is { } scroller && (y < 0 || y > LogList.ActualHeight))
        {
            var step = y < 0 ? y : y - LogList.ActualHeight;
            scroller.ChangeView(null, scroller.VerticalOffset + Math.Clamp(step, -60, 60), null, disableAnimation: true);
        }
        var index = IndexAt(new Windows.Foundation.Point(Math.Clamp(point.Position.X, 4, Math.Max(4, LogList.ActualWidth - 4)), Math.Clamp(y, 4, Math.Max(4, LogList.ActualHeight - 4))));
        if (index < 0 || index == _dragEnd)
            return;
        _dragEnd = index;
        var first = Math.Min(_dragAnchor, index);
        LogList.SelectedItems.Clear();
        LogList.SelectRange(new Microsoft.UI.Xaml.Data.ItemIndexRange(first, (uint)(Math.Abs(index - _dragAnchor) + 1)));
    }

    private void LogList_PointerEnded(object sender, PointerRoutedEventArgs e) => _dragAnchor = -1;

    /// <summary>Index of the log line under a point in the list's coordinates; -1 when there's none.</summary>
    private int IndexAt(Windows.Foundation.Point point)
    {
        var inWindow = LogList.TransformToVisual(null).TransformPoint(point);
        foreach (var element in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(inWindow, LogList))
        {
            if (element is ListViewItem item)
                return LogList.IndexFromContainer(item);
        }
        return -1;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if ((child as ScrollViewer ?? FindScrollViewer(child)) is { } found)
                return found;
        }
        return null;
    }

    /// <summary>Selecting lines pauses auto-scroll, so new output doesn't move them away mid-selection.</summary>
    private void LogList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedRanges.Count > 0 && AutoScroll.IsChecked == true)
        {
            AutoScroll.IsChecked = false;
            ApplyAutoScroll();
        }
    }

    /// <summary>Right-clicking a line outside the selection selects just it, as in a file list.</summary>
    private void LogList_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((args.OriginalSource as FrameworkElement)?.DataContext is Core.Logging.GameLogEntry entry && !LogList.SelectedItems.Contains(entry))
            LogList.SelectedItem = entry;
        CopySelectedItem.IsEnabled = LogList.SelectedRanges.Count > 0;
    }

    private void CopySelected_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        CopySelected();
    }

    private void CopySelected_Click(object sender, RoutedEventArgs e) => CopySelected();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => LogList.SelectAll();

    /// <summary>Copies the selected lines, in log order.</summary>
    private void CopySelected()
    {
        if (ViewModel.Game?.Log is not { } log || LogList.SelectedRanges.Count == 0)
            return;
        var lines = LogList.SelectedRanges
            .OrderBy(r => r.FirstIndex)
            .SelectMany(r => Enumerable.Range(r.FirstIndex, (int)r.Length))
            .Where(i => i < log.Count)
            .Select(i => log[i]);
        var dp = new DataPackage();
        dp.SetText(InstanceViewModel.FormatLog(lines));
        Clipboard.SetContent(dp);
    }

    private void AutoScroll_Click(object sender, RoutedEventArgs e)
    {
        if (AutoScroll.IsChecked == true)
            LogList.SelectedItems.Clear(); // Following the output again: the old selection would just scroll away.
        ApplyAutoScroll();
    }

    /// <summary>
    /// On: the list's panel keeps the newest line in view as lines arrive (no scrolling per line, which made the
    /// view jump). Off: what's on screen stays put, even while old lines are trimmed from the top.
    /// </summary>
    private void ApplyAutoScroll()
    {
        var on = AutoScroll.IsChecked == true;
        if (LogList.ItemsPanelRoot is ItemsStackPanel panel)
            panel.ItemsUpdatingScrollMode = on ? ItemsUpdatingScrollMode.KeepLastItemInView : ItemsUpdatingScrollMode.KeepItemsInView;
        if (on && ViewModel.Game is { Log.Count: > 0 } game)
            LogList.ScrollIntoView(game.Log[^1]);
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var tag = sender.SelectedItem?.Tag as string;
        LogPanel.Visibility = tag == "Log" ? Visibility.Visible : Visibility.Collapsed;
        VersionPanel.Visibility = tag == "Version" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = tag == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        ShowContent(tag);
        ShowGameData(tag);
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(ViewModel.LogAsText());
        Clipboard.SetContent(dp);
        CopiedFeedback.Show(sender);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Instance is { } i)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(i.GameDirectory) { UseShellExecute = true });
    }

    private async void UpdatePack_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Instance is not { } instance)
            return;
        if (ViewModel.Game?.IsActive == true)
        {
            await Dialogs.ShowMessageAsync(Loc.Get("Modpack_UpdateTitle"), Loc.Get("Version_StopFirst"));
            return;
        }
        if (await App.Get<ModpackInstallService>().UpdateAsync(instance))
        {
            ViewModel.RefreshPackInfo();
            ViewModel.Item?.Refresh();
            foreach (var panel in ContentPanels)
                _ = panel.ViewModel?.RefreshAsync();
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Instance is { } instance)
            await App.Get<ModpackInstallService>().ExportAsync(instance);
    }

    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Item is { } item)
            await IconPicker.ChangeAsync(item); // The Library reloads when it's shown again.
    }

    private async void LaunchAs_Opening(object sender, object e)
    {
        if (sender is MenuFlyout menu && ViewModel.Instance is { } instance)
            await LaunchAsMenu.FillAsync(menu.Items, instance, ViewModel.LaunchAs);
    }
}
