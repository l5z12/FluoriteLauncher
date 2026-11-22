using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Core;
using Fluorite.Core.Java;
using Fluorite.Core.Net;

namespace Fluorite.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly JavaManager _java;
    private bool _loading;

    private readonly DiscordService _discord;

    public SettingsViewModel(SettingsService settings, JavaManager java, DiscordService discord)
    {
        _settings = settings;
        _java = java;
        _discord = discord;
        _loading = true;
        var s = settings.Current;
        ShowDiscordPresence = s.ShowDiscordPresence;
        DownloadSourceIndex = (int)s.DownloadSource;
        MaxParallelDownloads = s.MaxParallelDownloads;
        ShowDownloadDetails = s.ShowDownloadDetails;
        MaxMemoryMb = s.MaxMemoryMb;
        JvmArguments = s.JvmArguments ?? "";
        // The default shows as the empty box's placeholder, so a box with a number in it is always a change.
        WindowWidth = s.WindowWidth == DefaultWidth ? double.NaN : s.WindowWidth;
        WindowHeight = s.WindowHeight == DefaultHeight ? double.NaN : s.WindowHeight;
        ThemeIndex = s.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
        LanguageIndex = Array.IndexOf(s_languages, s.Language) is var li and >= 0 ? li : 0;
        OnGameStartIndex = (int)s.OnGameStart;
        HomeLoadingIndex = (int)s.HomeLoading;
        LauncherWindowIndex = (int)s.LauncherWindow;
        LauncherWidth = s.LauncherWidth == DefaultLauncherWidth ? double.NaN : s.LauncherWidth;
        LauncherHeight = s.LauncherHeight == DefaultLauncherHeight ? double.NaN : s.LauncherHeight;
        CloseToTray = s.CloseToTray;
        ShowLogOnCrash = s.ShowLogOnCrash;
        NotifyOnGameExit = s.NotifyOnGameExit;
        CheckModsBeforeLaunch = s.CheckModsBeforeLaunch;
        SnapshotBeforeUpdates = s.SnapshotBeforeUpdates;
        ShowLoaderAvailability = s.ShowLoaderAvailability;
        AutoBackupWorlds = s.AutoBackupWorlds;
        AutoBackupsToKeep = s.AutoBackupsToKeep;
        AutoMemory = s.AutoMemory;
        JvmPresetIndex = (int)s.JvmPreset;
        GpuPreferenceIndex = (int)s.GpuPreference;
        PriorityIndex = Math.Max(0, Array.IndexOf(s_priorities, s.ProcessPriority));
        _loading = false;
    }

    // ── Performance ──────────────────────────────────────────────────────

    /// <summary>Priority choices in the order the combo box lists them.</summary>
    private static readonly Core.Launch.GameProcessPriority[] s_priorities =
    [
        Core.Launch.GameProcessPriority.BelowNormal,
        Core.Launch.GameProcessPriority.Normal,
        Core.Launch.GameProcessPriority.AboveNormal,
        Core.Launch.GameProcessPriority.High,
    ];

    [ObservableProperty] public partial bool AutoMemory { get; set; }
    [ObservableProperty] public partial int JvmPresetIndex { get; set; }
    [ObservableProperty] public partial int GpuPreferenceIndex { get; set; }
    [ObservableProperty] public partial int PriorityIndex { get; set; }

    public bool ManualMemory => !AutoMemory;

    /// <summary>What automatic memory gives on this PC, for vanilla and for a modpack.</summary>
    public static string AutoMemoryHint
    {
        get
        {
            var system = Core.Launch.MemoryAdvisor.SystemMemoryMb;
            return Loc.Format("Settings_AutoMemoryHint", (system + 512) / 1024,
                Core.Launch.MemoryAdvisor.Recommend(system, 0), Core.Launch.MemoryAdvisor.Recommend(system, 100));
        }
    }

    partial void OnAutoMemoryChanged(bool value)
    {
        Persist(s => s.AutoMemory = value);
        OnPropertyChanged(nameof(ManualMemory));
    }

    partial void OnJvmPresetIndexChanged(int value) => Persist(s => s.JvmPreset = (Core.Launch.JvmPreset)Math.Clamp(value, 0, 2));
    partial void OnGpuPreferenceIndexChanged(int value) => Persist(s => s.GpuPreference = (Core.Launch.GpuPreference)Math.Clamp(value, 0, 2));
    partial void OnPriorityIndexChanged(int value) => Persist(s => s.ProcessPriority = s_priorities[Math.Clamp(value, 0, s_priorities.Length - 1)]);

    /// <summary>Index 0 follows Windows; the rest are the shipped UI languages.</summary>
    private static readonly string?[] s_languages = [null, "en-US", "zh-Hans"];

    /// <summary>The language choices in the order the combo boxes list them (Settings, the welcome screen).</summary>
    public static IReadOnlyList<string?> Languages => s_languages;

    [ObservableProperty] public partial int LanguageIndex { get; set; }
    [ObservableProperty] public partial int OnGameStartIndex { get; set; }
    [ObservableProperty] public partial bool CloseToTray { get; set; }
    [ObservableProperty] public partial bool ShowLogOnCrash { get; set; }
    [ObservableProperty] public partial bool NotifyOnGameExit { get; set; }
    [ObservableProperty] public partial bool CheckModsBeforeLaunch { get; set; }
    [ObservableProperty] public partial bool SnapshotBeforeUpdates { get; set; }
    [ObservableProperty] public partial bool ShowLoaderAvailability { get; set; }

    /// <summary>Raised when a setting affecting the tray icon changes.</summary>
    public event EventHandler? TrayRelevantChanged;

    partial void OnLanguageIndexChanged(int value) =>
        Persist(s => s.Language = s_languages[Math.Clamp(value, 0, s_languages.Length - 1)], restart: true);

    /// <summary>Settings › While Home loads, in <see cref="Core.Settings.HomeLoadingStyle"/>'s order.</summary>
    [ObservableProperty] public partial int HomeLoadingIndex { get; set; }

    partial void OnHomeLoadingIndexChanged(int value) =>
        Persist(s => s.HomeLoading = (Core.Settings.HomeLoadingStyle)Math.Clamp(value, 0, 2));

    // ── Launcher window ──────────────────────────────────────────────────

    private const int DefaultLauncherWidth = Core.Settings.LauncherSettings.DefaultLauncherWidth;
    private const int DefaultLauncherHeight = Core.Settings.LauncherSettings.DefaultLauncherHeight;

    /// <summary>Settings › Launcher window size, in <see cref="Core.Settings.LauncherWindowMode"/>'s order.</summary>
    [ObservableProperty] public partial int LauncherWindowIndex { get; set; }

    /// <summary>In DIPs; empty (NaN) is the default, shown as the placeholder.</summary>
    [ObservableProperty] public partial double LauncherWidth { get; set; }

    [ObservableProperty] public partial double LauncherHeight { get; set; }

    public bool CustomLauncherSize => LauncherWindowIndex == (int)Core.Settings.LauncherWindowMode.Custom;

    public static string DefaultLauncherWidthText => DefaultLauncherWidth.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public static string DefaultLauncherHeightText => DefaultLauncherHeight.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public static double MinLauncherWidth => Core.Settings.LauncherSettings.MinLauncherWidth;
    public static double MinLauncherHeight => Core.Settings.LauncherSettings.MinLauncherHeight;

    /// <summary>A size was typed: the window takes it now, so the size is seen as it's set (width, height in DIPs).</summary>
    public event EventHandler<(int Width, int Height)>? LauncherSizeChanged;

    private bool _takingWindowSize;

    partial void OnLauncherWindowIndexChanged(int value)
    {
        Persist(s => s.LauncherWindow = (Core.Settings.LauncherWindowMode)Math.Clamp(value, 0, 2));
        OnPropertyChanged(nameof(CustomLauncherSize));
    }

    partial void OnLauncherWidthChanged(double value) =>
        SetLauncherSize(s => s.LauncherWidth = double.IsNaN(value) ? DefaultLauncherWidth : Math.Max((int)MinLauncherWidth, (int)value));

    partial void OnLauncherHeightChanged(double value) =>
        SetLauncherSize(s => s.LauncherHeight = double.IsNaN(value) ? DefaultLauncherHeight : Math.Max((int)MinLauncherHeight, (int)value));

    private void SetLauncherSize(Action<Core.Settings.LauncherSettings> apply)
    {
        if (_loading)
            return;
        Persist(apply);
        if (!_takingWindowSize)
            LauncherSizeChanged?.Invoke(this, (_settings.Current.LauncherWidth, _settings.Current.LauncherHeight));
    }

    /// <summary>Reset size: the boxes go back to the default (empty). The window is reset by the page, centred.</summary>
    public void ClearLauncherSize() => TakeLauncherSize(DefaultLauncherWidth, DefaultLauncherHeight);

    /// <summary>The custom size is the default one (both boxes empty).</summary>
    public bool IsDefaultLauncherSize => double.IsNaN(LauncherWidth) && double.IsNaN(LauncherHeight);

    /// <summary>"Use current size": the boxes take the window's size, and the window stays as it is (maximized too).</summary>
    public void TakeLauncherSize(int width, int height)
    {
        _takingWindowSize = true;
        try
        {
            LauncherWidth = Math.Max(width, (int)MinLauncherWidth) is var w && w == DefaultLauncherWidth ? double.NaN : w;
            LauncherHeight = Math.Max(height, (int)MinLauncherHeight) is var h && h == DefaultLauncherHeight ? double.NaN : h;
        }
        finally
        {
            _takingWindowSize = false;
        }
    }

    partial void OnOnGameStartIndexChanged(int value)
    {
        Persist(s => s.OnGameStart = (Core.Settings.GameStartBehavior)Math.Clamp(value, 0, 3));
        TrayRelevantChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        Persist(s => s.CloseToTray = value);
        TrayRelevantChanged?.Invoke(this, EventArgs.Empty);
    }

    partial void OnShowLogOnCrashChanged(bool value) => Persist(s => s.ShowLogOnCrash = value);
    partial void OnNotifyOnGameExitChanged(bool value) => Persist(s => s.NotifyOnGameExit = value);

    /// <summary>Only builds with a Discord application can show Rich Presence; the setting is hidden otherwise.</summary>
    public bool DiscordAvailable => _discord.IsAvailable;

    [ObservableProperty] public partial bool ShowDiscordPresence { get; set; }

    partial void OnShowDiscordPresenceChanged(bool value)
    {
        Persist(s => s.ShowDiscordPresence = value);
        if (!_loading)
            _discord.Update(); // Shows or clears it right away for a game already running.
    }
    partial void OnCheckModsBeforeLaunchChanged(bool value) => Persist(s => s.CheckModsBeforeLaunch = value);
    partial void OnSnapshotBeforeUpdatesChanged(bool value) => Persist(s => s.SnapshotBeforeUpdates = value);
    partial void OnShowLoaderAvailabilityChanged(bool value) => Persist(s => s.ShowLoaderAvailability = value);

    [ObservableProperty] public partial bool ShowDownloadDetails { get; set; }

    partial void OnShowDownloadDetailsChanged(bool value) => Persist(s => s.ShowDownloadDetails = value);

    [ObservableProperty] public partial bool AutoBackupWorlds { get; set; }
    [ObservableProperty] public partial double AutoBackupsToKeep { get; set; }

    partial void OnAutoBackupWorldsChanged(bool value) => Persist(s => s.AutoBackupWorlds = value);

    partial void OnAutoBackupsToKeepChanged(double value)
    {
        if (!double.IsNaN(value))
            Persist(s => s.AutoBackupsToKeep = Math.Clamp((int)value, 1, 100));
    }

    public string DataRoot => AppPaths.DataRoot;
    public string Version => FluoriteInfo.Version;

    /// <summary>"Automatic", the runtimes found on this PC, a custom path, "Browse…".</summary>
    public ObservableCollection<JavaChoice> JavaChoices { get; } = [];

    [ObservableProperty] public partial int JavaIndex { get; set; } = -1;

    private bool _fillingJava;

    partial void OnJavaIndexChanged(int oldValue, int newValue)
    {
        if (_fillingJava || newValue < 0 || newValue >= JavaChoices.Count)
            return;
        if (JavaChoices[newValue].IsBrowse)
            _ = BrowseJavaAsync(oldValue);
        else
            Persist(s => s.JavaPath = JavaChoices[newValue].Path);
    }

    private async Task BrowseJavaAsync(int previous)
    {
        var picked = await JavaChoice.PickAsync();
        _fillingJava = true;
        // Back to the previous entry first: adding before "Browse…" would otherwise move the selection onto it again.
        JavaIndex = previous >= 0 && previous < JavaChoices.Count && !JavaChoices[previous].IsBrowse ? previous : 0;
        _fillingJava = false;
        if (picked is not null)
            JavaIndex = JavaChoice.Add(JavaChoices, picked);
    }

    [ObservableProperty] public partial int DownloadSourceIndex { get; set; }
    [ObservableProperty] public partial double MaxParallelDownloads { get; set; }
    [ObservableProperty] public partial double MaxMemoryMb { get; set; }
    [ObservableProperty] public partial string JvmArguments { get; set; } = "";
    [ObservableProperty] public partial double WindowWidth { get; set; }
    [ObservableProperty] public partial double WindowHeight { get; set; }
    [ObservableProperty] public partial int ThemeIndex { get; set; }
    [ObservableProperty] public partial bool RestartRequired { get; set; }
    [ObservableProperty] public partial bool IsScanningJava { get; set; }

    public static double SystemMemoryMb => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1048576.0;

    public event EventHandler<int>? ThemeChanged;

    partial void OnDownloadSourceIndexChanged(int value) => Persist(s => s.DownloadSource = (DownloadSourceKind)value, restart: true);
    partial void OnMaxParallelDownloadsChanged(double value)
    {
        if (double.IsNaN(value)) // Cleared: back to the default, as memory does.
        {
            MaxParallelDownloads = Core.Settings.LauncherSettings.DefaultMaxParallelDownloads;
            return;
        }
        Persist(s => s.MaxParallelDownloads = (int)value, restart: true);
    }
    partial void OnMaxMemoryMbChanged(double value)
    {
        if (double.IsNaN(value)) // Cleared: back to the default (the slider can't show an empty value).
        {
            MaxMemoryMb = Core.Settings.LauncherSettings.DefaultMaxMemoryMb;
            return;
        }
        Persist(s => s.MaxMemoryMb = (int)value);
        OnPropertyChanged(nameof(MemorySizeText));
    }

    /// <summary>"4 GB of 32 GB", beside the megabytes.</summary>
    public string MemorySizeText => double.IsNaN(MaxMemoryMb) ? "" : Loc.Format("Settings_MemoryOf", Formats.Gigabytes(MaxMemoryMb), Math.Round(SystemMemoryMb / 1024));
    partial void OnJvmArgumentsChanged(string value) => Persist(s => s.JvmArguments = Core.Launch.CommandLine.ToSingleLine(value));
    private const int DefaultWidth = Core.Settings.LauncherSettings.DefaultWindowWidth;
    private const int DefaultHeight = Core.Settings.LauncherSettings.DefaultWindowHeight;

    /// <summary>Placeholders for the window size boxes: an empty box means Minecraft's default.</summary>
    public static string DefaultWidthText => DefaultWidth.ToString(System.Globalization.CultureInfo.CurrentCulture);
    public static string DefaultHeightText => DefaultHeight.ToString(System.Globalization.CultureInfo.CurrentCulture);

    partial void OnWindowWidthChanged(double value) => Persist(s => s.WindowWidth = double.IsNaN(value) ? DefaultWidth : (int)value);
    partial void OnWindowHeightChanged(double value) => Persist(s => s.WindowHeight = double.IsNaN(value) ? DefaultHeight : (int)value);

    partial void OnThemeIndexChanged(int value)
    {
        Persist(s => s.Theme = value switch { 1 => "Light", 2 => "Dark", _ => "System" });
        ThemeChanged?.Invoke(this, value);
    }

    private void Persist(Action<Core.Settings.LauncherSettings> apply, bool restart = false)
    {
        if (_loading)
            return;
        apply(_settings.Current);
        _settings.Save();
        if (restart)
            RestartRequired = true;
    }

    [RelayCommand]
    public async Task ScanJavaAsync()
    {
        IsScanningJava = true;
        try
        {
            var found = await _java.GetInstallationsAsync(refresh: true);
            _fillingJava = true;
            JavaIndex = -1;
            var index = JavaChoice.Fill(JavaChoices, new JavaChoice(null, Loc.Get("Java_Automatic"), Loc.Get("Java_AutomaticDetail")), found, _settings.Current.JavaPath);
            JavaIndex = index;
        }
        finally
        {
            _fillingJava = false;
            IsScanningJava = false;
        }
    }

    [RelayCommand]
    private static void OpenDataFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true });

    public void ChangeDataRoot(string path)
    {
        AppPaths.SetDataRoot(path);
        RestartRequired = true;
    }
}
