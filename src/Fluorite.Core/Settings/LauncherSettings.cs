using Fluorite.Core.Net;

namespace Fluorite.Core.Settings;

public enum GameStartBehavior
{
    KeepOpen,
    Minimize,
    HideToTray,
    Close,
}

/// <summary>What Home shows while what it gets from the internet (the latest versions, news) is still on its way.</summary>
public enum HomeLoadingStyle
{
    /// <summary>A small window, as Microsoft 365 apps show, until Home is ready; the main window then opens complete.</summary>
    LaunchWindow,

    /// <summary>The cards show a progress ring in place of what they're waiting for.</summary>
    Indicator,

    /// <summary>The cards appear only once they have something to show.</summary>
    Hide,
}

/// <summary>What clicking Home's Latest release or Latest snapshot card does. Its right-click menu always has all three.</summary>
public enum VersionCardAction
{
    /// <summary>The new-instance dialog, with the card's version picked.</summary>
    NewInstance,

    /// <summary>The version's patch notes; until the feed has them, the card's menu opens instead.</summary>
    PatchNotes,

    /// <summary>The version number, to the clipboard.</summary>
    CopyVersion,
}

/// <summary>How the launcher's own window opens (Settings › Launcher window size). It can be resized either way.</summary>
public enum LauncherWindowMode
{
    /// <summary>As it was closed: its size, its place and whether it was maximized.</summary>
    Remember,

    /// <summary>At <see cref="LauncherSettings.LauncherWidth"/> × <see cref="LauncherSettings.LauncherHeight"/>, centred on the screen it was closed on.</summary>
    Custom,

    /// <summary>Maximized on the screen it was closed on.</summary>
    Maximized,
}

public enum LibrarySort
{
    LastPlayed,
    Name,
    Created,
    MostPlayed,
}

/// <summary>Global settings stored as <c>settings.json</c> in the data root (portable with it).</summary>
public sealed class LauncherSettings
{
    public int FormatVersion { get; set; } = 1;
    public bool FirstRunCompleted { get; set; }

    public DownloadSourceKind DownloadSource { get; set; } = DownloadSourceKind.Official;
    public int MaxParallelDownloads { get; set; } = DefaultMaxParallelDownloads;

    public const int DefaultMaxParallelDownloads = 32;

    /// <summary>Show the file being downloaded, file counts, sizes and speed under downloads in progress.</summary>
    public bool ShowDownloadDetails { get; set; } = true;

    public const int DefaultMaxMemoryMb = 4096;

    public int MaxMemoryMb { get; set; } = DefaultMaxMemoryMb;
    public int? MinMemoryMb { get; set; }

    /// <summary>Size each instance's memory from the PC's RAM and its mod count, instead of <see cref="MaxMemoryMb"/>.</summary>
    public bool AutoMemory { get; set; } = true;

    public Launch.JvmPreset JvmPreset { get; set; } = Launch.JvmPreset.Balanced;

    /// <summary>Asked of Windows for the Java executable that runs the game (it's a per-program Windows setting).</summary>
    public Launch.GpuPreference GpuPreference { get; set; } = Launch.GpuPreference.HighPerformance;

    public Launch.GameProcessPriority ProcessPriority { get; set; } = Launch.GameProcessPriority.Normal;
    public string? JvmArguments { get; set; }
    public string? JavaPath { get; set; }
    public bool AutoInstallJava { get; set; } = true;

    /// <summary>Minecraft's own default window size.</summary>
    public const int DefaultWindowWidth = 854, DefaultWindowHeight = 480;

    public int WindowWidth { get; set; } = DefaultWindowWidth;
    public int WindowHeight { get; set; } = DefaultWindowHeight;
    public bool Fullscreen { get; set; }

    public GameStartBehavior OnGameStart { get; set; } = GameStartBehavior.KeepOpen;
    public HomeLoadingStyle HomeLoading { get; set; } = HomeLoadingStyle.Indicator;
    public VersionCardAction VersionCardClick { get; set; } = VersionCardAction.NewInstance;
    public bool CloseToTray { get; set; }
    public bool ShowLogOnLaunch { get; set; }
    public bool ShowLogOnCrash { get; set; } = true;
    public bool NotifyOnGameExit { get; set; } = true;

    /// <summary>Show the instance being played on Discord (Rich Presence), in builds that have a Discord application. Off until the user turns it on.</summary>
    public bool ShowDiscordPresence { get; set; }

    public LibrarySort LibrarySort { get; set; } = LibrarySort.LastPlayed;

    /// <summary>The Library shows instances as rows instead of cards.</summary>
    public bool LibraryListView { get; set; }

    /// <summary>The export format picked last time (Modrinth, CurseForge, Prism, packwiz), offered first next time.</summary>
    public int LastExportFormat { get; set; }

    /// <summary>Library groups the user collapsed; "" stands for the ungrouped instances.</summary>
    public List<string> CollapsedGroups { get; set; } = [];

    /// <summary>Groups made with "New group…": kept (as an empty section) until removed, even with no instance in them.</summary>
    public List<string> CreatedGroups { get; set; } = [];

    /// <summary>Groups hidden in the Library: every instance in one counts as hidden, wherever it's listed.</summary>
    public List<string> HiddenGroups { get; set; } = [];

    /// <summary>Hidden itself, or in a hidden group: out of the Library (unless shown), Home, the tray and the jump list.</summary>
    public bool IsHidden(Instances.InstanceManifest manifest) => manifest.Hidden || IsGroupHidden(manifest.Group);

    public bool IsGroupHidden(string? group) =>
        !string.IsNullOrWhiteSpace(group) && HiddenGroups.Contains(group.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Scan mods for missing dependencies, wrong loader/version, duplicates… before every launch.</summary>
    public bool CheckModsBeforeLaunch { get; set; } = true;

    /// <summary>Snapshot an instance's mods, configs and loader setup before updating its mods or modpack.</summary>
    public bool SnapshotBeforeUpdates { get; set; } = true;

    /// <summary>Mark mod loaders that have no build for the chosen Minecraft version (checked online) in loader pickers.</summary>
    public bool ShowLoaderAvailability { get; set; } = true;

    /// <summary>Zip the worlds a play session saved into <c>backups/</c> when the game closes.</summary>
    public bool AutoBackupWorlds { get; set; }

    /// <summary>Automatic backups kept per world; older ones are deleted. Backups made by hand are never deleted.</summary>
    public int AutoBackupsToKeep { get; set; } = 5;

    public const int DefaultLogRetentionDays = 14;

    /// <summary>Days Fluorite's own logs (<c>logs/launcher-*.log</c>) are kept, 1–365. Applied when Fluorite starts.</summary>
    public int LogRetentionDays { get; set; } = DefaultLogRetentionDays;

    /// <summary>Platform the Browse page opens with: <c>Modrinth</c> or <c>CurseForge</c>.</summary>
    public string BrowsePlatform { get; set; } = "Modrinth";

    /// <summary>Extra layout roots (official .minecraft, HMCL/PCL, Prism/MultiMC) browsed in place.</summary>
    public List<ExternalRoot> ExternalRoots { get; set; } = [];

    /// <summary>BCP-47 tag (e.g. <c>zh-Hans</c>) or null to follow Windows.</summary>
    public string? Language { get; set; }

    public string Theme { get; set; } = "System";

    /// <summary>Saved on every close, whatever <see cref="LauncherWindow"/> is: the other modes use the screen it was on.</summary>
    public WindowPlacement? MainWindow { get; set; }

    public LauncherWindowMode LauncherWindow { get; set; } = LauncherWindowMode.Remember;

    /// <summary>The launcher window's size on a first start, and with <see cref="LauncherWindowMode.Custom"/>; in DIPs (pixels at 100% scale).</summary>
    public const int DefaultLauncherWidth = 1280, DefaultLauncherHeight = 840;

    public const int MinLauncherWidth = 640, MinLauncherHeight = 480;

    public int LauncherWidth { get; set; } = DefaultLauncherWidth;
    public int LauncherHeight { get; set; } = DefaultLauncherHeight;
}

public sealed class WindowPlacement
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool Maximized { get; set; }
}

/// <summary>Another launcher's folder whose instances Fluorite shows and launches in place.</summary>
public sealed class ExternalRoot
{
    /// <summary>Short stable id (<c>prism</c>, <c>official</c>, <c>hmcl-2</c>…); prefixes the ids of its instances.</summary>
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public Instances.LayoutKind Kind { get; set; }
    public Instances.OtherLauncher Launcher { get; set; }

    /// <summary>Show its instances in the Library.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Let Fluorite write the launcher's own metadata (Prism/MultiMC <c>instance.cfg</c>/<c>mmc-pack.json</c>). Off by
    /// default: Fluorite's changes then go to a sidecar and the other launcher never sees them.
    /// </summary>
    public bool AllowWriteBack { get; set; }
}
