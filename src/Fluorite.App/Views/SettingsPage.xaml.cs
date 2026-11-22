using Fluorite.App.Services;
using Fluorite.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Fluorite.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void OnTrayRelevantChanged(object? sender, EventArgs e) => _ = App.Window?.UpdateTrayAsync();

    public SettingsViewModel ViewModel { get; } = App.Get<SettingsViewModel>();

    /// <summary>Upper bound for the memory slider: total physical memory minus 1 GB for Windows.</summary>
    public static double MemoryMax => Math.Max(1024, Math.Floor((SettingsViewModel.SystemMemoryMb - 1024) / 256) * 256);

    /// <summary>Other launchers' folders used in place.</summary>
    public System.Collections.ObjectModel.ObservableCollection<ExternalRootItem> Roots { get; } = [];

    private static Services.OtherLaunchersService Launchers => App.Get<Services.OtherLaunchersService>();

    private void LoadRoots()
    {
        Roots.Clear();
        foreach (var r in Launchers.Roots)
            Roots.Add(new ExternalRootItem(r, Launchers));
    }

    private void OnRootsChanged(object? sender, EventArgs e)
    {
        if (Launchers.Roots.Count != Roots.Count)
            LoadRoots();
    }

    /// <summary>The page is cached (one instance), so it subscribes on every visit and unsubscribes on leaving.</summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        ViewModel.ThemeChanged += OnThemeChanged;
        ViewModel.TrayRelevantChanged += OnTrayRelevantChanged;
        ViewModel.LauncherSizeChanged += OnLauncherSizeChanged;
        ViewModel.PropertyChanged += OnViewModelChanged;
        if (App.Window is { } window)
            window.AppWindow.Changed += OnWindowChanged;
        ToolTipService.SetToolTip(ResetSizeButton, Loc.Format("Settings_ResetSizeTip",
            Core.Settings.LauncherSettings.DefaultLauncherWidth, Core.Settings.LauncherSettings.DefaultLauncherHeight));
        ShowCurrentSize();
        LoadRoots();
        Launchers.RootsChanged += OnRootsChanged;
        if (e.Parameter is string setting && setting.StartsWith("Setting:", StringComparison.Ordinal))
        {
            Reveal(setting["Setting:".Length..]);
        }
        if (ViewModel.JavaChoices.Count == 0)
            await ViewModel.ScanJavaAsync();
    }

    /// <summary>
    /// From the command palette: scrolls to one setting (named by its x:Uid, found by its header), opens it if it
    /// expands, and puts keyboard focus on it so it's easy to spot.
    /// </summary>
    private void Reveal(string uid)
    {
        var header = Loc.Get(uid + "/Header");
        void Show()
        {
            if (Find(this, header) is not { } target)
                return;
            if (target is CommunityToolkit.WinUI.Controls.SettingsExpander expander)
                expander.IsExpanded = true;
            target.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.1, AnimationDesired = true });
            if (FocusManager.FindFirstFocusableElement(target) is Control control)
                control.Focus(FocusState.Keyboard);
        }
        if (IsLoaded)
        {
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Show);
            return;
        }
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            Loaded -= OnLoaded;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Show);
        }
        Loaded += OnLoaded;
    }

    private static FrameworkElement? Find(DependencyObject root, string header)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            // An expander is met before the card inside its template, which shows the same header.
            if (child is CommunityToolkit.WinUI.Controls.SettingsExpander { Header: string h1 } expander && h1 == header)
                return expander;
            if (child is CommunityToolkit.WinUI.Controls.SettingsCard { Header: string h2 } card && h2 == header)
                return card;
            if (Find(child, header) is { } found)
                return found;
        }
        return null;
    }

    private async void RestartNow_Click(object sender, RoutedEventArgs e)
    {
        // A running game carries on (it isn't Fluorite's child), but its log, crash check and play time stop here.
        if (App.Get<GameRunner>().ActiveCount > 0
            && await Dialogs.ShowAsync(Loc.Get("Settings_RestartGameTitle"), Dialogs.Text(Loc.Get("Settings_RestartGameRunning")), Loc.Get("Settings_RestartAnyway")) != ContentDialogResult.Primary)
            return;
        if (App.Window is { } window)
            await window.RestartApplicationAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        ViewModel.ThemeChanged -= OnThemeChanged;
        ViewModel.TrayRelevantChanged -= OnTrayRelevantChanged;
        ViewModel.LauncherSizeChanged -= OnLauncherSizeChanged;
        ViewModel.PropertyChanged -= OnViewModelChanged;
        if (App.Window is { } window)
            window.AppWindow.Changed -= OnWindowChanged;
        Launchers.RootsChanged -= OnRootsChanged;
    }

    // ── Launcher window size ─────────────────────────────────────────────

    private void OnLauncherSizeChanged(object? sender, (int Width, int Height) size) => App.Window?.ApplyLauncherSize(size.Width, size.Height);

    private void OnWindowChanged(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
            ShowCurrentSize();
    }

    /// <summary>
    /// "Use current size (1280 × 840)": the size it would take, kept up to date while the window is resized. Reset
    /// size is greyed out once there's nothing to reset.
    /// </summary>
    private void ShowCurrentSize()
    {
        if (App.Window is not { } window)
            return;
        var (w, h) = window.SizeInDips;
        UseCurrentSizeButton.Content = Loc.Format("Settings_UseCurrentSize", w, h);
        ResetSizeButton.IsEnabled = !window.IsAtDefaultSize || (ViewModel.CustomLauncherSize && !ViewModel.IsDefaultLauncherSize);
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.LauncherWidth) or nameof(SettingsViewModel.LauncherHeight) or nameof(SettingsViewModel.CustomLauncherSize))
            ShowCurrentSize();
    }

    private void ResetSize_Click(object sender, RoutedEventArgs e)
    {
        ViewModel.ClearLauncherSize();
        App.Window?.ResetLauncherSize();
        ShowCurrentSize();
    }

    private void UseCurrentSize_Click(object sender, RoutedEventArgs e)
    {
        if (App.Window is { } window)
        {
            var (w, h) = window.SizeInDips;
            ViewModel.TakeLauncherSize(w, h);
        }
    }

    private async void FindLaunchers_Click(object sender, RoutedEventArgs e) => await Launchers.ShowImportDialogAsync();

    private async void AddLauncherFolder_Click(object sender, RoutedEventArgs e) => await Launchers.AddFolderAsync();

    private void RemoveLauncherFolder_Click(object sender, RoutedEventArgs e)
    {
        if (Bind.ItemOf<ExternalRootItem>(sender) is { } item)
            Launchers.Remove(item.Root.Id);
    }

    private void OnThemeChanged(object? sender, int index) =>
        App.Window?.ApplyTheme(index switch { 1 => "Light", 2 => "Dark", _ => "System" });

    private async void ChangeDataRoot_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window!));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            ViewModel.ChangeDataRoot(folder.Path);
    }
}
