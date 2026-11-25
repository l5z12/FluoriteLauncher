using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Fluorite.App.Services;
using Fluorite.Auth.Accounts;
using Fluorite.Core.Instances;

namespace Fluorite.App.ViewModels;

/// <summary>An entry in the per-instance account picker; <see cref="Id"/> null means "use the selected account".</summary>
public sealed record AccountChoice(string? Id, string Label);

public sealed partial class InstanceViewModel(
    GameRunner runner, InstanceRegistry layout, AccountService accounts, ShortcutService shortcuts,
    Core.Versions.VersionRepository versions, LoaderPickerViewModel loaderPicker, DiagnosticsService diagnostics,
    Core.Java.JavaManager java, SettingsService settings) : ObservableObject
{
    // ── Crash diagnosis ──────────────────────────────────────────────────
    public ObservableCollection<CrashFindingItem> CrashFindings { get; } = [];

    /// <summary>The crash was analyzed: findings (or "not recognised") and the share buttons show.</summary>
    [ObservableProperty] public partial bool HasDiagnosis { get; set; }
    [ObservableProperty] public partial bool NoFindings { get; set; }

    /// <summary>Minecraft's crash report, or Java's crash log, for "Open crash report".</summary>
    [ObservableProperty] public partial string? CrashReportFile { get; set; }

    private Core.Diagnostics.CrashAnalysis? _shownDiagnosis;

    /// <summary>Findings that say the loader refused the mods: what their own details (and so Fix) can tell.</summary>
    private static readonly HashSet<string> s_modFindings = ["FabricIncompatibleMods", "MissingDependencies", "DuplicateMods", "LoaderTooOld", "OptiFineIncompatible"];

    /// <summary>The crash was the mods' (the loader refused them): Fix mod problems is offered.</summary>
    [ObservableProperty] public partial bool CrashFromMods { get; set; }

    /// <summary>What Fix mod problems did, by its button.</summary>
    [ObservableProperty] public partial string? FixModsStatus { get; set; }

    [RelayCommand]
    private async Task FixModsAsync()
    {
        if (Instance is null)
            return;
        FixModsStatus = null;
        if (await App.Get<ModFixService>().FixAsync(Instance) is { Changed: true } outcome)
            FixModsStatus = $"{outcome.Summary} {Loc.Get("ModFix_PlayAgain")}";
    }

    private void UpdateDiagnosis()
    {
        var diagnosis = ShowCrash ? Game?.Diagnosis : null;
        if (!ReferenceEquals(diagnosis, _shownDiagnosis))
        {
            _shownDiagnosis = diagnosis;
            CrashFindings.Clear();
            foreach (var f in diagnosis?.Findings ?? [])
                CrashFindings.Add(CrashFindingItem.From(f));
            FixModsStatus = null;
        }
        CrashFromMods = diagnosis?.Findings.Any(f => s_modFindings.Contains(f.Id)) == true;
        HasDiagnosis = diagnosis is not null;
        NoFindings = diagnosis is { Findings.Count: 0 };
        CrashReportFile = diagnosis?.CrashReportFile ?? diagnosis?.JvmCrashFile;
    }

    [RelayCommand]
    private async Task UploadLogAsync()
    {
        if (Game is { } game)
            await diagnostics.UploadLogAsync(game);
    }

    [RelayCommand]
    private async Task SaveDiagnosticsAsync()
    {
        if (Game is { } game)
            await diagnostics.SaveBundleAsync(game);
    }

    [RelayCommand]
    private void OpenCrashReport() => DiagnosticsService.OpenFile(CrashReportFile);

    public GameInstance? Instance { get; private set; }

    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

    // ── Version tab ──────────────────────────────────────────────────────
    public LoaderPickerViewModel LoaderPicker { get; } = loaderPicker;
    public ObservableCollection<string> MinecraftVersions { get; } = [];

    [ObservableProperty] public partial string? SelectedMinecraftVersion { get; set; }
    [ObservableProperty] public partial bool ShowSnapshots { get; set; }
    [ObservableProperty] public partial string? VersionStatus { get; set; }
    [ObservableProperty] public partial string? InstalledProfile { get; set; }

    partial void OnSelectedMinecraftVersionChanged(string? value) => LoaderPicker.MinecraftVersion = value;

    partial void OnShowSnapshotsChanged(bool value) => _ = LoadMinecraftVersionsAsync();

    private async Task LoadMinecraftVersionsAsync()
    {
        var current = Instance?.Manifest.MinecraftVersion;
        try
        {
            var manifest = await versions.GetManifestAsync();
            var list = manifest.Versions
                .Where(v => v.Type == Core.Versions.VersionTypes.Release || (ShowSnapshots && v.Type == Core.Versions.VersionTypes.Snapshot) || v.Id == current)
                .OrderByDescending(v => v.ParsedReleaseTime)
                .Select(v => v.Id)
                .ToList();
            MinecraftVersions.Clear();
            foreach (var v in list)
                MinecraftVersions.Add(v);
        }
        catch (HttpRequestException)
        {
            MinecraftVersions.Clear();
            if (current is not null)
                MinecraftVersions.Add(current);
        }
        SelectedMinecraftVersion = current;
    }

    [RelayCommand]
    private async Task ApplyVersionAsync()
    {
        if (Instance is null || SelectedMinecraftVersion is null || !CanEditVersion)
            return;
        if (LoaderPicker.IsIncomplete)
        {
            VersionStatus = Loc.Get("Loader_PickVersion");
            return;
        }
        if (Game?.IsActive == true)
        {
            VersionStatus = Loc.Get("Version_StopFirst");
            return;
        }
        // Another version changes what loads: something to go back to.
        VersionStatus = Loc.Get("Snapshot_Taking");
        await App.Get<SnapshotService>().TakeBeforeUpdateAsync(Instance, Loc.Get("Snapshot_BeforeVersion"));
        LoaderPicker.ApplyTo(Instance.Manifest, SelectedMinecraftVersion);
        await layout.SaveAsync(Instance);
        Item?.Refresh();
        InstalledProfile = null;
        VersionStatus = Loc.Get("Version_Applied");
    }

    /// <summary>The Version tab shows the instance's setup again (after a snapshot put an older one back).</summary>
    public async Task ReloadVersionAsync()
    {
        if (Instance is null)
            return;
        LoaderPicker.Load(Instance.Manifest);
        InstalledProfile = ComponentResolver.PrimaryLoader(Instance.Manifest)?.ProfileId;
        VersionStatus = null;
        await LoadMinecraftVersionsAsync();
    }

    // ── Origin: another launcher's folder, a modpack ─────────────────────
    [ObservableProperty] public partial bool IsExternal { get; set; }
    [ObservableProperty] public partial string? ExternalTitle { get; set; }
    [ObservableProperty] public partial string? ExternalInfo { get; set; }
    [ObservableProperty] public partial bool HasWarnings { get; set; }
    [ObservableProperty] public partial string? WarningsText { get; set; }
    [ObservableProperty] public partial bool IsModpack { get; set; }
    [ObservableProperty] public partial string? PackInfo { get; set; }
    [ObservableProperty] public partial bool CanEditVersion { get; set; } = true;

    private void LoadOrigin()
    {
        if (Instance is null)
            return;
        IsExternal = Instance.IsExternal;
        CanEditVersion = Instance.CanEditComponents;
        if (Instance.Launcher is { } launcher)
        {
            ExternalTitle = Loc.Format("Instance_ExternalTitle", Loc.Get("Launcher_" + launcher));
            ExternalInfo = Loc.Format(Instance.CanEditComponents ? "Instance_ExternalWriteBack" : "Instance_ExternalSidecar", Instance.Directory);
        }
        HasWarnings = Instance.Warnings.Count > 0;
        WarningsText = string.Join("\n", Instance.Warnings);
        RefreshPackInfo();
    }

    public void RefreshPackInfo()
    {
        var state = Instance is null ? null : ModpackInstallService.StateOf(Instance);
        IsModpack = state is not null && state.Format is not Fluorite.Modding.Modpacks.PackFormat.MultiMC;
        PackInfo = state is null ? null : Loc.Format("Instance_PackInfo", state.Name, state.Version ?? "", state.Format switch
        {
            Fluorite.Modding.Modpacks.PackFormat.Modrinth => "Modrinth",
            Fluorite.Modding.Modpacks.PackFormat.CurseForge => "CurseForge",
            Fluorite.Modding.Modpacks.PackFormat.Ftb => "FTB",
            Fluorite.Modding.Modpacks.PackFormat.Technic => "Technic",
            Fluorite.Modding.Modpacks.PackFormat.Packwiz => "packwiz",
            _ => "Prism",
        });
    }

    [ObservableProperty] public partial InstanceItem? Item { get; set; }
    [ObservableProperty] public partial RunningGame? Game { get; set; }
    [ObservableProperty] public partial bool CanPlay { get; set; } = true;
    [ObservableProperty] public partial bool ShowCrash { get; set; }

    // Settings overrides (empty = inherit global).
    /// <summary>Empty (NaN) uses the global setting, which the box shows as its placeholder.</summary>
    [ObservableProperty] public partial double MaxMemory { get; set; } = double.NaN;
    [ObservableProperty] public partial string? GlobalMemory { get; set; }
    [ObservableProperty] public partial string JvmArguments { get; set; } = "";
    [ObservableProperty] public partial int AccountIndex { get; set; }

    /// <summary>Empty (NaN) uses the global size, which the box shows as its placeholder.</summary>
    [ObservableProperty] public partial double WindowWidth { get; set; } = double.NaN;
    [ObservableProperty] public partial double WindowHeight { get; set; } = double.NaN;
    [ObservableProperty] public partial string? GlobalWindowWidth { get; set; }
    [ObservableProperty] public partial string? GlobalWindowHeight { get; set; }

    // ── Windows on ARM ───────────────────────────────────────────────────

    /// <summary>An ARM64 PC, or <c>--pretend-arm64</c> (which only shows this UI; launches are unaffected).</summary>
    public static bool IsArm64Pc => Core.Platform.RuntimePlatform.Current.OsArch == Core.Platform.CpuArch.Arm64 || StartupFlags.Current.PretendArm64;

    /// <summary>0: native where possible; 1: always x64 emulation.</summary>
    [ObservableProperty] public partial int Arm64ModeIndex { get; set; }
    [ObservableProperty] public partial string? Arm64Explanation { get; set; }
    [ObservableProperty] public partial bool CanChooseArm64Mode { get; set; } = true;

    private Core.Libraries.Arm64Support? _arm64Support;

    /// <summary>How this instance's Minecraft version runs here, from its LWJGL (the vanilla JSON decides it).</summary>
    private async Task LoadArm64Async()
    {
        if (!IsArm64Pc || Instance?.Manifest.MinecraftVersion is not { } minecraft)
            return;
        try
        {
            var json = await versions.GetOrFetchAsync(minecraft);
            _arm64Support = Core.Libraries.Arm64Lwjgl.Classify(json.Libraries ?? []);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or Core.Versions.VersionNotFoundException or System.Text.Json.JsonException)
        {
            _arm64Support = null;
        }
        UpdateArm64Explanation();
    }

    partial void OnArm64ModeIndexChanged(int value) => UpdateArm64Explanation();

    private void UpdateArm64Explanation()
    {
        var minecraft = Instance?.Manifest.MinecraftVersion ?? "";
        CanChooseArm64Mode = _arm64Support is not Core.Libraries.Arm64Support.Emulated;
        Arm64Explanation = Arm64ModeIndex == 1 && CanChooseArm64Mode
            ? Loc.Get("Arm64_EmulateChosen")
            : _arm64Support switch
            {
                Core.Libraries.Arm64Support.Native => Loc.Format("Arm64_Native", minecraft),
                Core.Libraries.Arm64Support.SwappedLwjgl => Loc.Format("Arm64_Swapped", minecraft),
                Core.Libraries.Arm64Support.Emulated => Loc.Format("Arm64_Emulated", minecraft),
                _ => Loc.Get("Arm64_Unknown"),
            };
    }

    /// <summary>"Use the global setting", the runtimes found on this PC, a custom path, "Browse…".</summary>
    public ObservableCollection<JavaChoice> JavaChoices { get; } = [];

    [ObservableProperty] public partial int JavaIndex { get; set; } = -1;

    private async Task LoadJavaChoicesAsync(string? current)
    {
        var global = settings.Current.JavaPath;
        var first = new JavaChoice(null, Loc.Get("Java_UseGlobal"), string.IsNullOrWhiteSpace(global) ? Loc.Get("Java_Automatic") : global);
        IReadOnlyList<Core.Java.JavaInstallation> found;
        try
        {
            found = await java.GetInstallationsAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            found = [];
        }
        JavaIndex = -1;
        JavaIndex = JavaChoice.Fill(JavaChoices, first, found, current);
    }

    partial void OnJavaIndexChanged(int oldValue, int newValue)
    {
        if (newValue >= 0 && newValue < JavaChoices.Count && JavaChoices[newValue].IsBrowse)
            _ = BrowseJavaAsync(oldValue);
    }

    private async Task BrowseJavaAsync(int previous)
    {
        var picked = await JavaChoice.PickAsync();
        // Back to the previous entry first: adding before "Browse…" would otherwise move the selection onto it again.
        JavaIndex = previous >= 0 && previous < JavaChoices.Count && !JavaChoices[previous].IsBrowse ? previous : 0;
        if (picked is not null)
            JavaIndex = JavaChoice.Add(JavaChoices, picked);
    }
    [ObservableProperty] public partial string? SaveStatus { get; set; }

    public async Task InitializeAsync(InstanceItem item)
    {
        Item = item;
        Instance = item.Instance;
        // Removals an earlier run left waiting for Undo (it closed or crashed first) become final.
        var gameDirectory = Instance.GameDirectory;
        _ = Task.Run(() => Services.RemovalStash.CommitLeftovers(gameDirectory));
        var s = Instance.Manifest.Settings;
        MaxMemory = s.MaxMemoryMb ?? double.NaN;
        GlobalMemory = settings.Current.AutoMemory
            ? Loc.Get("Instance_MemoryAutomatic")
            : settings.Current.MaxMemoryMb.ToString(System.Globalization.CultureInfo.CurrentCulture);
        JvmArguments = s.JvmArguments ?? "";
        WindowWidth = s.WindowWidth ?? double.NaN;
        WindowHeight = s.WindowHeight ?? double.NaN;
        GlobalWindowWidth = settings.Current.WindowWidth.ToString(System.Globalization.CultureInfo.CurrentCulture);
        GlobalWindowHeight = settings.Current.WindowHeight.ToString(System.Globalization.CultureInfo.CurrentCulture);
        _ = LoadJavaChoicesAsync(s.JavaPath);
        Arm64ModeIndex = s.Arm64Mode == Core.Libraries.Arm64Mode.Emulate ? 1 : 0;
        _ = LoadArm64Async();
        Game = runner.Find(Instance) ?? runner.Latest(Instance);
        UpdateState();
        LoadOrigin();

        AccountChoices.Clear();
        AccountChoices.Add(new AccountChoice(null, Loc.Get("Account_UseSelected")));
        foreach (var a in await accounts.GetAccountsAsync())
            AccountChoices.Add(new AccountChoice(a.Id, $"{a.PlayerName} ({(a.Kind == AccountKind.Microsoft ? Loc.Get("Account_KindMicrosoft") : Loc.Get("Account_KindOffline"))})"));
        var index = AccountChoices.ToList().FindIndex(c => c.Id is not null && c.Id == s.DefaultAccountId);
        AccountIndex = index < 0 ? 0 : index;

        LoaderPicker.Load(Instance.Manifest);
        InstalledProfile = ComponentResolver.PrimaryLoader(Instance.Manifest)?.ProfileId;
        await LoadMinecraftVersionsAsync();
    }

    partial void OnGameChanged(RunningGame? oldValue, RunningGame? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnGamePropertyChanged;
            oldValue.Log.CollectionChanged -= OnLogChanged;
        }
        if (newValue is not null)
        {
            newValue.PropertyChanged += OnGamePropertyChanged;
            newValue.Log.CollectionChanged += OnLogChanged;
        }
        UpdateState();
    }

    private void OnGamePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => UpdateState();

    private void OnLogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => HasLog = Game is { Log.Count: > 0 };

    /// <summary>Whether this session has log lines; without any, the Log tab shows an empty state instead of a blank list.</summary>
    [ObservableProperty] public partial bool HasLog { get; set; }

    /// <summary>The game's own <c>logs/latest.log</c> from the last session, when there is one.</summary>
    [ObservableProperty] public partial string? LastLogFile { get; set; }

    public string LogEmptyHint => Loc.Get(Game?.IsActive == true ? "Instance_LogEmptyStarting" : "Instance_LogEmptyHint");

    /// <summary>The game is running: a badge by the name (the Stop button covers the rest).</summary>
    [ObservableProperty] public partial bool IsGameRunning { get; set; }

    /// <summary>Launching (status, progress, download details) or crashed; otherwise the status area takes no space.</summary>
    [ObservableProperty] public partial bool ShowLaunchStatus { get; set; }

    private void UpdateState()
    {
        CanPlay = Game is null || !Game.IsActive;
        ShowCrash = Game is { IsActive: false, CrashedOrFailed: true };
        IsGameRunning = Game is { IsRunning: true };
        ShowLaunchStatus = Game is { IsPreparing: true } || ShowCrash;
        HasLog = Game is { Log.Count: > 0 };
        UpdateDiagnosis();
        OnPropertyChanged(nameof(LogEmptyHint));
        if (Item is not null)
            Item.IsRunning = !CanPlay;
        if (!HasLog && Instance is not null)
        {
            var path = Path.Combine(Instance.GameDirectory, "logs", "latest.log");
            LastLogFile = File.Exists(path) ? path : null;
        }
    }

    [RelayCommand]
    private void OpenLastLog()
    {
        if (LastLogFile is { } path && File.Exists(path))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
    }

    [RelayCommand]
    private void Play() => Launch(null);

    /// <summary>Starts the instance, optionally straight into a world or server (Quick Play).</summary>
    public void Launch(Core.Launch.QuickPlay? quickPlay)
    {
        if (Instance is not null)
            Game = runner.Launch(Instance, quickPlay);
    }

    /// <summary>Plays as <paramref name="accountId"/> this once ("Launch as"); the instance's default is unchanged.</summary>
    public void LaunchAs(string accountId)
    {
        if (Instance is not null)
            Game = runner.Launch(Instance, accountId: accountId);
    }

    [RelayCommand]
    private void Stop() => Game?.Stop();

    [RelayCommand]
    private void CreateShortcut()
    {
        if (Instance is not null)
        {
            shortcuts.CreateDesktopShortcut(Instance);
            SaveStatus = Loc.Format("Shortcut_Created", Instance.Name);
        }
    }

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        if (Instance is null)
            return;
        var s = Instance.Manifest.Settings;
        s.MaxMemoryMb = double.IsNaN(MaxMemory) ? null : (int)MaxMemory;
        s.JvmArguments = Core.Launch.CommandLine.ToSingleLine(JvmArguments);
        s.WindowWidth = double.IsNaN(WindowWidth) ? null : (int)WindowWidth;
        s.WindowHeight = double.IsNaN(WindowHeight) ? null : (int)WindowHeight;
        if (JavaIndex >= 0 && JavaIndex < JavaChoices.Count && !JavaChoices[JavaIndex].IsBrowse)
            s.JavaPath = JavaChoices[JavaIndex].Path; // Until the list has loaded, the saved one stays.
        if (IsArm64Pc)
            s.Arm64Mode = Arm64ModeIndex == 1 ? Core.Libraries.Arm64Mode.Emulate : null;
        s.DefaultAccountId = AccountIndex > 0 && AccountIndex < AccountChoices.Count ? AccountChoices[AccountIndex].Id : null;
        await layout.SaveAsync(Instance);
        SaveStatus = Loc.Get("Settings_Saved");
    }

    /// <summary>Plain-text log for copy/upload.</summary>
    public string LogAsText() => FormatLog(Game?.Log ?? []);

    /// <summary>Log lines as text: <c>[time] [thread/LEVEL] message</c>, then any stack trace.</summary>
    public static string FormatLog(IEnumerable<Core.Logging.GameLogEntry> entries)
    {
        var sb = new StringBuilder();
        foreach (var e in entries)
        {
            sb.Append('[').Append(e.Timestamp.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)).Append("] ");
            if (e.Thread is not null)
                sb.Append('[').Append(e.Thread).Append('/').Append(e.Level.ToString().ToUpperInvariant()).Append("] ");
            sb.AppendLine(e.Message);
            if (e.Throwable is not null)
                sb.AppendLine(e.Throwable);
        }
        return sb.ToString();
    }
}
