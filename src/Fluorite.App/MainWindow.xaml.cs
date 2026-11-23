using System.Runtime.InteropServices;
using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Fluorite.App.Views;
using Fluorite.Core.Instances;
using Fluorite.Core.Settings;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Fluorite.App;

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, Type> s_pages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Home"] = typeof(HomePage),
        ["Library"] = typeof(LibraryPage),
        ["Browse"] = typeof(BrowsePage),
        ["Activity"] = typeof(ActivityPage),
        ["Accounts"] = typeof(AccountsPage),
        ["Settings"] = typeof(SettingsPage),
    };

    private readonly SettingsService _settings = App.Get<SettingsService>();
    private readonly GameRunner _runner = App.Get<GameRunner>();
    private readonly TrayService _tray = App.Get<TrayService>();
    private readonly NotificationService _notifications = App.Get<NotificationService>();
    private readonly TaskbarProgress _taskbar;
    private bool _hiddenForGame;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Title = Loc.Get("AppTitle/Text");
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        RestorePlacement();
        ApplyTheme(StartupFlags.Current.Theme ?? _settings.Current.Theme);
        ShowFlagsInTitle();

        _taskbar = new TaskbarProgress(Hwnd);
        _runner.ActivityChanged += OnActivityChanged;
        _runner.GameStarted += OnGameStarted;
        _runner.GameEnded += OnGameEnded;
        _tray.ShowRequested += (_, _) => DispatcherQueue.TryEnqueue(BringToFront);
        _tray.ExitRequested += (_, _) => DispatcherQueue.TryEnqueue(ExitApplication);
        _notifications.OpenInstanceRequested += (_, id) => DispatcherQueue.TryEnqueue(() => _ = OpenInstanceAsync(id, launch: false));
        AppWindow.Closing += OnClosing;
        ContentFrame.Navigated += (_, _) => MemoryTrim.Schedule(); // Free the page that was left.

        var openPalette = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.K, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        openPalette.Invoked += OpenPalette_Invoked;
        RootGrid.KeyboardAccelerators.Add(openPalette);
        var find = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.F, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        find.Invoked += Find_Invoked;
        RootGrid.KeyboardAccelerators.Add(find);
        var newInstance = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.N, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        newInstance.Invoked += NewInstance_Invoked;
        RootGrid.KeyboardAccelerators.Add(newInstance);
        var importFile = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = Windows.System.VirtualKey.O, Modifiers = Windows.System.VirtualKeyModifiers.Control };
        importFile.Invoked += ImportFile_Invoked;
        RootGrid.KeyboardAccelerators.Add(importFile);
        // Back and forward: Alt+Left/Right, the keyboard's own Back/Forward keys, and the mouse's side buttons.
        foreach (var (key, modifiers, back) in new[]
                 {
                     (Windows.System.VirtualKey.Left, Windows.System.VirtualKeyModifiers.Menu, true),
                     (Windows.System.VirtualKey.Right, Windows.System.VirtualKeyModifiers.Menu, false),
                     (Windows.System.VirtualKey.GoBack, Windows.System.VirtualKeyModifiers.None, true),
                     (Windows.System.VirtualKey.GoForward, Windows.System.VirtualKeyModifiers.None, false),
                 })
        {
            var accelerator = new Microsoft.UI.Xaml.Input.KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = back ? GoBack() : GoForward();
            RootGrid.KeyboardAccelerators.Add(accelerator);
        }
        RootGrid.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler(RootGrid_PointerPressed), handledEventsToo: true);
        ContentFrame.Navigated += ContentFrame_Navigated;
        RootGrid.KeyboardAcceleratorPlacementMode = Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden;
        AppTitleBar.SizeChanged += (_, _) => UpdateTitleBarRegions();
        PaletteButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        BackButton.SizeChanged += (_, _) => UpdateTitleBarRegions();
        ContentFrame.Navigated += (_, _) => BackButton.IsEnabled = CanGoBack;
        RootGrid.Loaded += (_, _) =>
        {
            RootGrid.XamlRoot.Changed += (_, _) => UpdateTitleBarRegions(); // Moved to a screen with another scale.
            UpdateTitleBarRegions();
        };

        NavigateTo("Home");
        _ = UpdateTrayAsync();
    }

    private nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>The theme in use (Settings › Theme, or <c>--theme</c>): the launch window matches it.</summary>
    public ElementTheme Theme => RootGrid.RequestedTheme;

    public void ApplyTheme(string theme) =>
        RootGrid.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    // ── Command palette ───────────────────────────────────────────────────

    private void OpenPalette_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Palette.IsOpen)
            Palette.Close();
        else
            _ = Palette.OpenAsync(this);
    }

    private void PaletteButton_Click(object sender, RoutedEventArgs e) => _ = Palette.OpenAsync(this);

    /// <summary>A dialog is up (only one can be open), or the palette is: page shortcuts wait.</summary>
    private bool IsBusyWithOverlay() =>
        Palette.IsOpen
        || (RootGrid.XamlRoot is { } root && Microsoft.UI.Xaml.Media.VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog));

    /// <summary>Ctrl+F: the search or filter box of the page on screen.</summary>
    private void Find_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsBusyWithOverlay())
            return;
        switch (ContentFrame.Content)
        {
            case LibraryPage library:
                library.FocusSearch();
                args.Handled = true;
                break;
            case BrowsePage browse:
                browse.FocusSearch();
                args.Handled = true;
                break;
            case InstancePage instance:
                args.Handled = instance.FocusSearch();
                break;
        }
    }

    /// <summary>Ctrl+N: new instance, from anywhere.</summary>
    private void NewInstance_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsBusyWithOverlay())
            return;
        args.Handled = true;
        StartNewInstance();
    }

    /// <summary>The new-instance dialog on the Library page, with <paramref name="versionId"/> picked if given.</summary>
    public void StartNewInstance(string? versionId = null)
    {
        if (ContentFrame.Content is LibraryPage library)
            library.StartNewInstance(versionId);
        else
            NavigateTo("Library", versionId is null ? "NewInstance" : new LibraryPage.NewInstanceRequest(versionId));
    }

    /// <summary>Ctrl+O: import a modpack file, from anywhere.</summary>
    private void ImportFile_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsBusyWithOverlay())
            return;
        args.Handled = true;
        if (ContentFrame.Content is LibraryPage library)
            library.StartImportFile();
        else
            NavigateTo("Library", "ImportFile");
    }

    /// <summary>
    /// The title bar drags the window; the back and search buttons need pass-through regions to be clickable. The
    /// search also keeps clear of the caption buttons, whose width depends on the scale.
    /// </summary>
    private void UpdateTitleBarRegions()
    {
        if (RootGrid.XamlRoot is not { } root)
            return;
        var scale = root.RasterizationScale;
        var margin = new Thickness(24, 0, AppWindow.TitleBar.RightInset / scale + 16, 0);
        if (PaletteButton.Margin != margin)
            PaletteButton.Margin = margin;
        var passthrough = new FrameworkElement[] { BackButton, PaletteButton }
            .Where(e => e.ActualWidth > 0)
            .Select(e =>
            {
                var b = e.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, e.ActualWidth, e.ActualHeight));
                return new Windows.Graphics.RectInt32((int)Math.Round(b.X * scale), (int)Math.Round(b.Y * scale), (int)Math.Round(b.Width * scale), (int)Math.Round(b.Height * scale));
            })
            .ToArray();
        if (passthrough.Length > 0)
            Microsoft.UI.Input.InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
                .SetRegionRects(Microsoft.UI.Input.NonClientRegionKind.Passthrough, passthrough);
    }

    // ── Navigation ─────────────────────────────────────────────────────────

    public void NavigateTo(string tag, object? parameter = null)
    {
        if (!s_pages.TryGetValue(tag, out var page))
            return;
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                Nav.SelectedItem = item;
                break;
            }
        }
        if (ContentFrame.CurrentSourcePageType != page || parameter is not null)
            ContentFrame.Navigate(page, parameter);
    }

    /// <summary>
    /// Opens an instance's page; when it's already the page on screen, stays on it. On its log, or another tab
    /// (<paramref name="tab"/>: the tab's tag, e.g. "Mod"), if asked.
    /// </summary>
    public void NavigateToInstance(InstanceItem item, bool showLog = false, string? tab = null)
    {
        if (ShownInstancePage(item.Instance.Id) is not { } page)
        {
            ContentFrame.Navigate(typeof(InstancePage), item, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });
            page = ContentFrame.Content as InstancePage;
        }
        if (showLog)
            page?.ShowLog();
        else if (tab is not null)
            page?.ShowTab(tab);
    }

    private InstancePage? ShownInstancePage(string instanceId) =>
        ContentFrame.Content is InstancePage { ViewModel.Instance: { } shown } page && shown.Id == instanceId ? page : null;

    public void NavigateToProject(ProjectNavigation nav) =>
        ContentFrame.Navigate(typeof(ProjectPage), nav, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    public void NavigateToSkin(Fluorite.Auth.Accounts.Account account) =>
        ContentFrame.Navigate(typeof(SkinPage), account, new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromRight });

    /// <summary>
    /// The previous page (the title bar's Back button, Alt+Left, the mouse's back button); from a page opened directly
    /// (no history), its parent. False when there's nowhere to go or a dialog is up.
    /// </summary>
    public bool GoBack()
    {
        if (IsBusyWithOverlay())
            return false;
        if (ContentFrame.CanGoBack)
        {
            ContentFrame.GoBack();
            return true;
        }
        if (ParentPage(ContentFrame.Content) is { } parent)
        {
            NavigateTo(parent);
            return true;
        }
        return false;
    }

    private bool CanGoBack => ContentFrame.CanGoBack || ParentPage(ContentFrame.Content) is not null;

    /// <summary>Where a sub-page goes back to when it was opened directly (a shortcut, a link, a notification).</summary>
    private static string? ParentPage(object? page) => page switch
    {
        InstancePage => "Library",
        ProjectPage => "Browse",
        SkinPage => "Accounts",
        PatchNotesPage => "Home",
        _ => null,
    };

    private void BackButton_Click(object sender, RoutedEventArgs e) => GoBack();

    public bool GoForward()
    {
        if (IsBusyWithOverlay() || !ContentFrame.CanGoForward)
            return false;
        ContentFrame.GoForward();
        return true;
    }

    /// <summary>The mouse's side buttons (XButton1: back, XButton2: forward).</summary>
    private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(RootGrid).Properties;
        if (point.IsXButton1Pressed)
            e.Handled = GoBack() || e.Handled;
        else if (point.IsXButton2Pressed)
            e.Handled = GoForward() || e.Handled;
    }

    /// <summary>Going back or forward to a main page selects it in the navigation pane too.</summary>
    private void ContentFrame_Navigated(object sender, Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        if (e.NavigationMode is not (Microsoft.UI.Xaml.Navigation.NavigationMode.Back or Microsoft.UI.Xaml.Navigation.NavigationMode.Forward))
            return;
        var tag = s_pages.FirstOrDefault(p => p.Value == e.SourcePageType).Key;
        if (tag is null)
            return;
        foreach (var item in Nav.MenuItems.Concat(Nav.FooterMenuItems).OfType<NavigationViewItem>())
        {
            if (string.Equals((string)item.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                Nav.SelectedItem = item;
                break;
            }
        }
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && s_pages.TryGetValue(tag, out var page) && ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page, null, args.RecommendedNavigationTransitionInfo);
    }

    // ── Activation (command line, protocol, jump list, toast, shortcut) ───

    public async Task HandleActivationAsync(ActivationRequest request)
    {
        if (request.Page is { } page)
            NavigateTo(page);
        if (request.LaunchInstanceId is { } launchId)
            await OpenInstanceAsync(launchId, launch: true, request.QuickPlay);
        else if (request.OpenInstanceId is { } openId)
            await OpenInstanceAsync(openId, launch: false);
        if (request.Files is { Count: > 0 } || request.InstallLocation is not null)
        {
            await WhenLoadedAsync(); // Opened with a pack file at startup: its dialogs need the window in place.
            BringToFront();
            NavigateTo("Library");
            var packs = App.Get<ModpackInstallService>();
            if (request.InstallLocation is { } location)
                await packs.InstallFromLocationAsync(location);
            foreach (var file in request.Files ?? [])
                await packs.InstallFromFileAsync(file);
        }
    }

    // ── Modpack files dropped anywhere ────────────────────────────────────

    /// <summary>
    /// A modpack file (.mrpack, .zip) can be dropped anywhere in the window. Panels that take files of their own (an
    /// instance's mods, worlds) handle their drops first, so this only sees what they leave.
    /// </summary>
    private async void RootGrid_DragOver(object sender, DragEventArgs e)
    {
        if (IsBusyWithOverlay() || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        // Taken only with a modpack file in it, else a folder or a screenshot would look welcome and then do nothing.
        // What's dragged takes a read to know: once per drag, with the answer deferred until then.
        var view = e.DataView;
        if (!ReferenceEquals(view, _draggedView))
        {
            var deferral = e.GetDeferral();
            try
            {
                var items = await view.GetStorageItemsAsync();
                _draggedHasPack = items.OfType<StorageFile>().Any(f => ModpackInstallService.HasModpackExtension(f.Path));
                _draggedView = view;
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
            {
                _draggedHasPack = false;
            }
            finally
            {
                AcceptModpackDrop(e);
                deferral.Complete();
            }
            return;
        }
        AcceptModpackDrop(e);
    }

    /// <summary>The drag last read by <see cref="RootGrid_DragOver"/>, and whether it holds a modpack file.</summary>
    private DataPackageView? _draggedView;
    private bool _draggedHasPack;

    private void AcceptModpackDrop(DragEventArgs e)
    {
        if (!_draggedHasPack)
            return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = Loc.Get("Import_DropCaption");
    }

    private async void RootGrid_Drop(object sender, DragEventArgs e)
    {
        _draggedView = null;
        if (IsBusyWithOverlay() || !e.DataView.Contains(StandardDataFormats.StorageItems))
            return;
        // The source keeps its data until the files are read; not for the whole install, which waits on dialogs.
        var deferral = e.GetDeferral();
        IReadOnlyList<IStorageItem> items;
        try
        {
            items = await e.DataView.GetStorageItemsAsync();
        }
        finally
        {
            deferral.Complete();
        }
        // One at a time; each install asks for confirmation.
        if (items.OfType<StorageFile>().Select(f => f.Path).FirstOrDefault(ModpackInstallService.HasModpackExtension) is { } path)
            await InstallModpackAsync(path);
    }

    /// <summary>Installs a modpack file as a new instance, from the Library, where it shows up and opens when done.</summary>
    public async Task InstallModpackAsync(string path)
    {
        NavigateTo("Library");
        await App.Get<ModpackInstallService>().InstallFromFileAsync(path);
    }

    public async Task OpenInstanceAsync(string id, bool launch, Core.Launch.QuickPlay? quickPlay = null)
    {
        var instance = await App.Get<InstanceRegistry>().GetAsync(id);
        if (instance is null)
            return;
        if (launch)
            _runner.Launch(instance, quickPlay);
        if (ShownInstancePage(instance.Id) is null)
            NavigateTo("Library");
        NavigateToInstance(new InstanceItem(instance) { IsRunning = _runner.Find(instance) is not null });
        BringToFront();
    }

    // ── Game lifecycle → window behaviour, tray, taskbar, toasts ──────────

    private void OnActivityChanged(object? sender, EventArgs e)
    {
        var active = _runner.ActiveCount;
        ActivityBadge.Value = active;
        ActivityBadge.Visibility = active > 0 ? Visibility.Visible : Visibility.Collapsed;

        var (state, value) = _runner.AggregateProgress();
        _taskbar.SetState(state);
        if (state == TaskbarProgressState.Normal)
            _taskbar.SetValue(value);
    }

    private async void OnGameStarted(object? sender, RunningGame game)
    {
        var behavior = _settings.Current.OnGameStart;
        // Offline skins are served by the launcher itself: hide instead of exiting, or they'd vanish in game.
        if (behavior == GameStartBehavior.Close && App.Get<Fluorite.Auth.Yggdrasil.LocalSkinServer>().IsRunning)
            behavior = GameStartBehavior.HideToTray;
        switch (behavior)
        {
            case GameStartBehavior.Minimize:
                if (AppWindow.Presenter is OverlappedPresenter p)
                    p.Minimize();
                break;
            case GameStartBehavior.HideToTray:
                await _tray.EnsureAsync();
                _hiddenForGame = true;
                AppWindow.Hide();
                break;
            case GameStartBehavior.Close:
                // The game runs in its own job object and keeps running after the launcher exits.
                ExitApplication();
                break;
        }
    }

    private void OnGameEnded(object? sender, RunningGame game)
    {
        var s = _settings.Current;
        var crashed = game.CrashedOrFailed;
        var wasHidden = _hiddenForGame && _runner.ActiveCount == 0;

        if (wasHidden || (crashed && s.ShowLogOnCrash))
        {
            _hiddenForGame = false;
            BringToFront();
        }
        if (crashed && s.ShowLogOnCrash)
            NavigateToInstance(new InstanceItem(game.Instance), showLog: true);

        if (crashed && game.ExitCode is { } code)
        {
            var cause = game.Diagnosis?.Findings.FirstOrDefault(f => f.Confidence == Core.Diagnostics.CrashConfidence.Likely) is { } finding
                ? CrashFindingItem.From(finding).Title
                : null;
            _notifications.ShowGameCrashed(game.Instance.Id, game.Name, code, cause);
        }
        else if (!crashed && s.NotifyOnGameExit && game.PlayTime > TimeSpan.Zero && !IsForeground())
            _notifications.ShowGameClosed(game.Instance.Id, game.Name, game.PlayTime);

        _ = UpdateTrayAsync();
        _ = UpdateJumpListAsync();
    }

    /// <summary>The list is rebuilt from every instance (play order changed, a world was just played).</summary>
    private static async Task UpdateJumpListAsync()
    {
        // A --fresh window's (temporary) instances mustn't replace the real ones in the taskbar menu.
        if (!JumpListService.IsSupported || StartupFlags.Current.Fresh)
            return;
        try
        {
            await App.Get<JumpListService>().UpdateAsync(await App.Get<InstanceRegistry>().GetInstancesAsync());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    public async Task UpdateTrayAsync()
    {
        var s = _settings.Current;
        if (s.CloseToTray || s.OnGameStart == GameStartBehavior.HideToTray || _hiddenForGame)
            await _tray.EnsureAsync();
        else
            _tray.Remove();
    }

    // ── Window chrome, placement, closing ─────────────────────────────────

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        SavePlacement();
        if (!_exiting && _settings.Current.CloseToTray)
        {
            args.Cancel = true;
            _ = _tray.EnsureAsync();
            AppWindow.Hide();
            return;
        }
        UndoOffer.CommitAll(); // Removals still waiting for Undo are final.
        _tray.Remove();
        _notifications.Unregister();
    }

    public void ExitApplication()
    {
        _exiting = true;
        SavePlacement();
        UndoOffer.CommitAll();
        _tray.Remove();
        _notifications.Unregister();
        Application.Current.Exit();
    }

    /// <summary>
    /// Starts Fluorite again (a language only applies at startup), keeping this run's startup flags (a --fresh run
    /// keeps its folder) but not what it was opened for (no second launch of a game or install).
    /// </summary>
    /// <param name="showWelcome">Show the welcome screen again after the restart.</param>
    public async Task RestartApplicationAsync(bool showWelcome = false)
    {
        var f = StartupFlags.Current;
        var args = new List<string>();
        if (f.Fresh)
            args.Add("--fresh");
        if (f.Fresh || f.Root is not null)
            args.AddRange(["--root", AppPaths.DataRoot]);
        if (f.Theme is not null)
            args.AddRange(["--theme", f.Theme.ToLowerInvariant()]);
        if (f.PretendArm64)
            args.Add("--pretend-arm64");
        if (f.Verbose)
            args.Add("--verbose");
        if (f.TraceExceptions)
            args.Add("--trace-exceptions");
        if (showWelcome)
            args.Add("--welcome");
        // The new process waits for this one to end before taking the single-instance key.
        args.AddRange(["--restarted", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]);

        _exiting = true;
        SavePlacement();
        UndoOffer.CommitAll();
        _tray.Remove();
        _notifications.Unregister();
        // Ends this process when it works; returns only when it couldn't.
        Microsoft.Windows.AppLifecycle.AppInstance.Restart(Core.Launch.CommandLine.Join(args));
        _exiting = false;
        _notifications.Register();
        _ = UpdateTrayAsync();
        await Dialogs.ShowMessageAsync(Loc.Get("Restart_FailedTitle"), Loc.Get("Restart_Failed"));
    }

    public void BringToFront()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
            p.Restore();
        Activate();
        SetForegroundWindow(Hwnd);
    }

    private bool IsForeground() => GetForegroundWindow() == Hwnd && AppWindow.IsVisible;

    /// <summary>Settings › Launcher window size: as it was closed, at the custom size, or maximized.</summary>
    private void RestorePlacement()
    {
        var s = _settings.Current;
        var saved = s.MainWindow;
        // The screen it was closed on, while it's still connected.
        var area = saved is null ? null : DisplayArea.GetFromRect(new Windows.Graphics.RectInt32(saved.X, saved.Y, saved.Width, saved.Height), DisplayAreaFallback.None);
        var restore = area is not null && saved is { Width: >= 400, Height: >= 300 } ? saved : null;
        if (s.LauncherWindow == LauncherWindowMode.Custom)
            SizeToDips(s.LauncherWidth, s.LauncherHeight, area, share: 1);
        else if (restore is not null)
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(restore.X, restore.Y, restore.Width, restore.Height));
        else
            SizeToDips(LauncherSettings.DefaultLauncherWidth, LauncherSettings.DefaultLauncherHeight, area);
        // Maximized over the size above, which it goes back to when restored.
        var maximize = s.LauncherWindow == LauncherWindowMode.Maximized || (s.LauncherWindow == LauncherWindowMode.Remember && restore?.Maximized == true);
        if (maximize && AppWindow.Presenter is OverlappedPresenter p)
            p.Maximize();
    }

    /// <summary>
    /// Settings › Launcher window size › Custom: the window takes a size typed there right away, around its centre and
    /// inside the screen it's on (restored first when maximized).
    /// </summary>
    public void ApplyLauncherSize(int width, int height)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized } p)
            p.Restore();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var (w, h) = ToPixels(width, height, area, share: 1);
        var x = Math.Clamp(AppWindow.Position.X + (AppWindow.Size.Width - w) / 2, work.X, work.X + work.Width - w);
        var y = Math.Clamp(AppWindow.Position.Y + (AppWindow.Size.Height - h) / 2, work.Y, work.Y + work.Height - h);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, w, h));
    }

    /// <summary>Settings › Launcher window size › Reset size: the default size, centred on the screen it's on (restored first when maximized).</summary>
    public void ResetLauncherSize()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored } p)
            p.Restore();
        SizeToDips(LauncherSettings.DefaultLauncherWidth, LauncherSettings.DefaultLauncherHeight);
    }

    /// <summary>Not maximized, and as big as <see cref="ResetLauncherSize"/> would make it on this screen.</summary>
    public bool IsAtDefaultSize
    {
        get
        {
            if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored })
                return false;
            var (w, h) = ToPixels(LauncherSettings.DefaultLauncherWidth, LauncherSettings.DefaultLauncherHeight,
                DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary), share: 0.92);
            // A pixel either way is rounding.
            return Math.Abs(AppWindow.Size.Width - w) <= 1 && Math.Abs(AppWindow.Size.Height - h) <= 1;
        }
    }

    /// <summary>The window's size on screen in DIPs, as Settings › Launcher window size takes it.</summary>
    public (int Width, int Height) SizeInDips
    {
        get
        {
            var scale = ScaleOf(DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary));
            return ((int)Math.Round(AppWindow.Size.Width / scale), (int)Math.Round(AppWindow.Size.Height / scale));
        }
    }

    private void SavePlacement()
    {
        if (AppWindow.Presenter is not OverlappedPresenter p || p.State == OverlappedPresenterState.Minimized)
            return;
        var maximized = p.State == OverlappedPresenterState.Maximized;
        var previous = _settings.Current.MainWindow;
        _settings.Current.MainWindow = maximized && previous is not null
            ? new WindowPlacement { X = previous.X, Y = previous.Y, Width = previous.Width, Height = previous.Height, Maximized = true }
            : new WindowPlacement { X = AppWindow.Position.X, Y = AppWindow.Position.Y, Width = AppWindow.Size.Width, Height = AppWindow.Size.Height, Maximized = maximized };
        _settings.Save();
    }

    /// <summary>Centres the window on <paramref name="on"/> (the screen it's on when null) at a size in DIPs.</summary>
    /// <param name="share">The most of the work area it may take: the default size leaves a margin, a size set by hand needn't.</param>
    private void SizeToDips(int width, int height, DisplayArea? on = null, double share = 0.92)
    {
        var area = on ?? DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;
        var (w, h) = ToPixels(width, height, area, share);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
    }

    /// <summary>AppWindow sizes are physical pixels: DIPs scaled by the screen's DPI, kept inside its work area.</summary>
    private static (int Width, int Height) ToPixels(int width, int height, DisplayArea area, double share)
    {
        var scale = ScaleOf(area);
        var work = area.WorkArea;
        return (Math.Min((int)(width * scale), (int)(work.Width * share)), Math.Min((int)(height * scale), (int)(work.Height * share)));
    }

    /// <summary>The screen's own scale, so a window placed on another screen than the one it was made on is sized for it.</summary>
    private static double ScaleOf(DisplayArea area)
    {
        var monitor = Microsoft.UI.Win32Interop.GetMonitorFromDisplayId(area.DisplayId);
        return GetDpiForMonitor(monitor, MonitorDpiEffective, out var dpi, out _) == 0 && dpi > 0 ? dpi / 96.0 : 1;
    }

    private const int MonitorDpiEffective = 0;

    /// <summary>
    /// Done once the window's content is in the visual tree. A dialog needs its XamlRoot, which at startup (the
    /// welcome screen, a pack file opened with Fluorite) isn't there yet: showing one then throws.
    /// </summary>
    private Task WhenLoadedAsync()
    {
        if (Content.XamlRoot is not null)
            return Task.CompletedTask;
        var loaded = new TaskCompletionSource();
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            RootGrid.Loaded -= OnLoaded;
            loaded.TrySetResult();
        }
        RootGrid.Loaded += OnLoaded;
        return loaded.Task;
    }

    /// <param name="force">Show it even after the first run (<c>--welcome</c>).</param>
    public async Task ShowWelcomeIfFirstRunAsync(bool force = false)
    {
        if (_settings.Current.FirstRunCompleted && !force)
            return;
        await WhenLoadedAsync();
        var dialog = new WelcomeDialog(again: _settings.Current.FirstRunCompleted) { XamlRoot = Content.XamlRoot, RequestedTheme = RootGrid.RequestedTheme };
        var result = await dialog.ShowAsync();
        if (dialog.RestartForLanguage)
        {
            // The welcome screen comes back in the new language; on a first run it shows by itself.
            await RestartApplicationAsync(showWelcome: _settings.Current.FirstRunCompleted);
            return;
        }
        dialog.ApplyChoices();
        _settings.Current.FirstRunCompleted = true;
        _settings.Save();
        if (result == ContentDialogResult.Primary)
            NavigateTo("Accounts");
    }

    // ── Startup flags ────────────────────────────────────────────────────

    /// <summary>Says so in the title bar when the window isn't showing your usual data or this PC as it is.</summary>
    private void ShowFlagsInTitle()
    {
        var notes = new List<string>();
        if (StartupFlags.Current.Fresh)
            notes.Add(Loc.Get("Flags_TitleFresh"));
        if (StartupFlags.Current.PretendArm64)
            notes.Add(Loc.Get("Flags_TitlePretendArm64"));
        FlagsText.Text = string.Join(" · ", notes);
        FlagsText.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (notes.Count > 0)
            Title = Loc.Get("AppTitle/Text") + " · " + FlagsText.Text;
    }

    /// <summary>The list of startup flags (<c>--flags</c>, or the command palette).</summary>
    /// <param name="startupOnlyIgnored">A second launch passed flags this window can't apply any more: say so first.</param>
    public async Task ShowFlagsAsync(bool startupOnlyIgnored = false)
    {
        var panel = new StackPanel { Spacing = 12 };
        if (startupOnlyIgnored)
            panel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Message = Loc.Get("Flags_StartupOnly") });
        panel.Children.Add(Dialogs.Text(Loc.Get("Flags_Intro"), secondary: true));
        var mono = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
        foreach (var flag in StartupFlags.All)
        {
            var usage = string.Join("   ", new[]
            {
                flag.Switch.Length > 0 ? (flag.Value is null ? flag.Switch : $"{flag.Switch} {flag.Value}") : null,
                flag.Variable is null ? null : flag.Value is null ? $"{flag.Variable}=1" : $"{flag.Variable}=…",
            }.Where(s => s is not null));
            var entry = new StackPanel { Spacing = 2 };
            entry.Children.Add(new TextBlock { Text = usage, FontFamily = mono, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
            entry.Children.Add(Dialogs.Text(Loc.Get(flag.DescriptionKey), secondary: true));
            panel.Children.Add(entry);
        }
        await Dialogs.ShowAsync(Loc.Get("Flags_Title"), Dialogs.Scroll(panel, 520), close: Loc.Get("Common_Ok"));
    }

    public async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = Loc.Get("Common_Ok"),
            XamlRoot = Content.XamlRoot,
            RequestedTheme = RootGrid.RequestedTheme,
            Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"],
        };
        await dialog.ShowAsync();
    }

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}
