using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Core.Logging;
using Fluorite.Platform.Windows.Shell;
using Microsoft.UI.Dispatching;

namespace Fluorite.App.Services;

/// <summary>A launch in progress or a running game, observable by the UI.</summary>
public sealed partial class RunningGame : ProgressItem
{
    public RunningGame(GameInstance instance) => Instance = instance;

    public GameInstance Instance { get; }
    public string Name => Instance.Name;
    public ObservableCollection<GameLogEntry> Log { get; } = [];
    internal LaunchedGame? Launched { get; set; }
    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>
    /// Every line of the session, kept off the UI thread for crash analysis and uploads (<see cref="Log"/> is trimmed
    /// for the view and fills in asynchronously).
    /// </summary>
    internal LogArchive Archive { get; } = new();

    /// <summary>What must never leave the PC in a shared log (the access token, the sign-in name).</summary>
    internal List<string> Secrets { get; } = [];

    /// <summary>Why it crashed, when the analyzer could tell (set before <see cref="GameRunner.GameEnded"/>).</summary>
    [ObservableProperty] public partial Core.Diagnostics.CrashAnalysis? Diagnosis { get; set; }

    /// <summary>The user pressed Stop: the forced exit isn't a crash.</summary>
    public bool StoppedByUser { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive))]
    public partial bool IsPreparing { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CrashedOrFailed))]
    public partial int? ExitCode { get; set; }

    [ObservableProperty] public partial TimeSpan PlayTime { get; set; }

    /// <summary>When the JVM started (null while preparing).</summary>
    public DateTimeOffset? StartedAt { get; internal set; }

    public bool IsActive => IsRunning || IsPreparing;
    public bool CrashedOrFailed => ExitCode is not null and not 0 && !StoppedByUser;

    public void Stop()
    {
        StoppedByUser = true;
        Cancellation.Cancel();
        Launched?.Process.Kill();
    }
}

/// <summary>A session's log lines, capped (oldest dropped first), safe to add to from the output thread.</summary>
internal sealed class LogArchive
{
    private const int MaxEntries = 100_000;
    private readonly List<GameLogEntry> _entries = [];
    private readonly Lock _lock = new();

    public void Add(GameLogEntry entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxEntries + 10_000)
                _entries.RemoveRange(0, _entries.Count - MaxEntries);
        }
    }

    public void AddRange(IEnumerable<GameLogEntry> entries)
    {
        foreach (var e in entries)
            Add(e);
    }

    public string ToText()
    {
        lock (_lock)
            return ViewModels.InstanceViewModel.FormatLog(_entries);
    }
}

/// <summary>Launches instances, streams their logs to the UI thread in batches and records play statistics.</summary>
public sealed class GameRunner(
    LaunchPipeline pipeline, AccountService accounts, AccountSignIn signIn, SettingsService settings, InstanceRegistry registry,
    Fluorite.Modding.Loaders.LoaderService loaders, Fluorite.Modding.Content.ContentService content, HttpClient http)
{
    private const int MaxLogLines = 20_000;
    private const int LogTrimChunk = 2_000;
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();

    public ObservableCollection<RunningGame> Games { get; } = [];

    /// <summary>Raised on the UI thread when the JVM has started.</summary>
    public event EventHandler<RunningGame>? GameStarted;

    /// <summary>Raised on the UI thread when a launch finished (exited, crashed, failed or was cancelled).</summary>
    public event EventHandler<RunningGame>? GameEnded;

    /// <summary>Raised on the UI thread whenever any game's status/progress changes.</summary>
    public event EventHandler? ActivityChanged;

    public int ActiveCount => Games.Count(g => g.IsActive);

    public RunningGame? Find(GameInstance instance) =>
        Games.FirstOrDefault(g => g.Instance.Directory == instance.Directory && g.IsActive);

    public RunningGame? Latest(GameInstance instance) =>
        Games.LastOrDefault(g => g.Instance.Directory == instance.Directory);

    /// <param name="accountId">Play as this account this once ("Launch as"); null uses the instance's default or the selected one.</param>
    public RunningGame Launch(GameInstance instance, QuickPlay? quickPlay = null, string? accountId = null)
    {
        if (Find(instance) is { } existing)
            return existing;

        var game = new RunningGame(instance) { Status = Loc.Get("Launch_Preparing") };
        game.PropertyChanged += (_, _) => ActivityChanged?.Invoke(this, EventArgs.Empty);
        Games.Add(game);
        ActivityChanged?.Invoke(this, EventArgs.Empty);
        _ = RunAsync(game, quickPlay, accountId);
        return game;
    }

    public void ClearFinished()
    {
        foreach (var g in Games.Where(g => !g.IsActive).ToList())
            Games.Remove(g);
        ActivityChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Combined progress for the taskbar button.</summary>
    public (TaskbarProgressState State, double Value) AggregateProgress()
    {
        var preparing = Games.Where(g => g.IsPreparing).ToList();
        if (preparing.Count == 0)
            return (TaskbarProgressState.None, 0);
        if (preparing.Any(g => g.IsIndeterminate))
            return (TaskbarProgressState.Indeterminate, 0);
        return (TaskbarProgressState.Normal, preparing.Average(g => g.Progress) / 100.0);
    }

    private async Task<Account> ResolveAccountAsync(GameInstance instance, string? chosenId, CancellationToken ct)
    {
        if (chosenId is not null && (await accounts.GetAccountsAsync(ct)).FirstOrDefault(a => a.Id == chosenId) is { } chosen)
            return chosen;
        if (instance.Manifest.Settings.DefaultAccountId is { } id
            && (await accounts.GetAccountsAsync(ct)).FirstOrDefault(a => a.Id == id) is { } preferred)
            return preferred;
        return await accounts.GetSelectedAsync(ct) ?? throw new InvalidOperationException(Loc.Get("Launch_NoAccount"));
    }

    private async Task RunAsync(RunningGame game, QuickPlay? quickPlay, string? accountId)
    {
        var ct = game.Cancellation.Token;
        var started = new Stopwatch();
        try
        {
            foreach (var w in game.Instance.Warnings)
                AppendLocal(game, GameLogLevel.Warn, w);
            var account = await ResolveAccountAsync(game.Instance, accountId, ct);
            if (settings.Current.CheckModsBeforeLaunch && !await CheckModsAsync(game, ct))
            {
                game.Status = Loc.Get("Launch_Cancelled");
                return;
            }
            var session = await GetSessionAsync(game, account, ct);
            game.Secrets.Add(session.AccessToken);
            if (account.LoginName is { } login)
                game.Secrets.Add(login);
            var options = BuildOptions(game.Instance, session, quickPlay, out var autoMemory);
            if (autoMemory is { } auto)
                AppendLocal(game, GameLogLevel.Info, Loc.Format("Launch_AutoMemory", options.MaxMemoryMb ?? 0, auto.Mods, auto.SystemGb));

            // Install the mod loader profile on first launch (or after the user changed it).
            var versionId = await EnsureLoaderAsync(game, ct);

            var progress = new Progress<LaunchProgress>(p => OnProgress(game, p));
            // Instances of an official/HMCL .minecraft launch from that folder's versions, libraries and assets.
            var launched = await Task.Run(() => pipeline.ForStore(game.Instance.Store, http).LaunchAsync(versionId, options, progress, ct), ct);
            game.Launched = launched;
            foreach (var w in launched.Prepared.Warnings)
                AppendLocal(game, GameLogLevel.Warn, w);

            game.ClearDownload();
            game.IsPreparing = false;
            game.IsRunning = true;
            game.Status = Loc.Get("Launch_Running");
            var sessionStart = DateTimeOffset.UtcNow;
            game.StartedAt = sessionStart;
            started.Start();
            GameStarted?.Invoke(this, game);

            await PumpAsync(game, launched);
            var exitCode = await launched.Process.Exited;
            started.Stop();

            game.PlayTime = started.Elapsed;
            if (exitCode != 0 && !game.StoppedByUser)
            {
                // Before the exit is announced, so the page and the toast can say why.
                game.Status = Loc.Get("Launch_Analyzing");
                game.Diagnosis = await AnalyzeAsync(game, launched, exitCode, sessionStart);
            }
            game.IsRunning = false;
            game.ExitCode = exitCode;
            game.Status = game.StoppedByUser ? Loc.Get("Launch_Stopped")
                : exitCode == 0 ? Loc.Get("Launch_Exited")
                : Loc.Format("Launch_Crashed", exitCode);
            await RecordStatsAsync(game.Instance, started.Elapsed);
            // In the background: the window, toast and tray react to the exit without waiting for big worlds to zip.
            if (settings.Current.AutoBackupWorlds)
                _ = BackUpPlayedWorldsAsync(game, sessionStart);
        }
        catch (Exception ex) when (ex is OperationCanceledException or AuthException { Failure: AuthFailure.Cancelled })
        {
            // Stopped, or a sign-in Windows asked for was cancelled: not a failure.
            game.Status = Loc.Get("Launch_Cancelled");
        }
        catch (Exception ex)
        {
            var message = ex is AuthException auth ? ViewModels.AccountsViewModel.DescribeFailure(auth) : ex.Message;
            game.Status = Loc.Format("Launch_Failed", message);
            game.ExitCode = -1;
            AppendLocal(game, GameLogLevel.Error, ex.ToString());
        }
        finally
        {
            game.ClearDownload();
            game.IsPreparing = false;
            game.IsRunning = false;
            GameEnded?.Invoke(this, game);
        }
    }

    /// <summary>
    /// The launch session. When the account's sign-in stopped working (a skin site's token, or a Microsoft sign-in that
    /// Windows or Microsoft no longer renews), it's signed in again, in place, and the launch goes on; cancelling that
    /// cancels the launch.
    /// </summary>
    private async Task<GameSession> GetSessionAsync(RunningGame game, Account account, CancellationToken ct)
    {
        try
        {
            return await accounts.GetSessionAsync(account, ct);
        }
        catch (AuthException ex) when (ex.Failure == AuthFailure.SignInAgain && account.Kind != AccountKind.Offline)
        {
            AppendLocal(game, GameLogLevel.Warn, ex.Message); // Why, for the log.
            game.Status = Loc.Get("Auth_SignInAgain");
            if (await signIn.SignInAgainAsync(account, forLaunch: true, ct) != SignInAgainResult.SignedIn)
                throw new OperationCanceledException();
            game.Status = Loc.Get("Launch_Preparing");
            return await accounts.GetSessionAsync(account, ct);
        }
    }

    /// <summary>
    /// Pre-launch mod checks: everything goes to the log; errors (missing dependencies, wrong loader…) that weren't
    /// dismissed ask the user whether to fix them (when Fix mod problems is on in Settings), or launch anyway. After a
    /// fix the mods are checked again (and asked about again, without the fix, while errors are left). Returns false to
    /// cancel.
    /// </summary>
    private async Task<bool> CheckModsAsync(RunningGame game, CancellationToken ct)
    {
        var offerFix = settings.Current.FixModProblems;
        for (var pass = 0; ; pass++)
        {
            game.Status = Loc.Get("Launch_CheckingMods");
            var (choice, asked) = await CheckModsOnceAsync(game, offerFix, log: pass == 0, ct);
            switch (choice)
            {
                case ContentInstallService.LaunchChoice.Launch:
                    return true;
                case ContentInstallService.LaunchChoice.Fix:
                    game.Status = Loc.Get("Launch_FixingMods");
                    var outcome = await App.Get<ModFixService>().FixAsync(game.Instance, asked, forLaunch: true);
                    ct.ThrowIfCancellationRequested(); // Stopped meanwhile.
                    if (outcome is not null)
                    {
                        // Fixed (or nothing could be): checked again, without offering the same fix twice. Cancelled:
                        // asked again as it was.
                        offerFix = false;
                        if (outcome.Changed)
                            AppendLocal(game, GameLogLevel.Info, outcome.Summary);
                    }
                    break;
                default:
                    return false;
            }
        }
    }

    /// <summary>One check, and what the user chose; with the keys of the problems asked about (what Fix fixes).</summary>
    /// <param name="log">Write the problems found to the game's log (once: a check after a fix would repeat them).</param>
    private async Task<(ContentInstallService.LaunchChoice Choice, IReadOnlyList<string> Asked)> CheckModsOnceAsync(RunningGame game, bool offerFix, bool log, CancellationToken ct)
    {
        const ContentInstallService.LaunchChoice Launch = ContentInstallService.LaunchChoice.Launch;
        var instance = game.Instance;
        var manager = content.For(instance);
        IReadOnlyList<Fluorite.Modding.Content.ContentFile> mods;
        IReadOnlyList<Fluorite.Modding.Content.ModIssue> issues;
        IReadOnlySet<string> dismissed;
        try
        {
            mods = await Task.Run(() => manager.ScanAsync(new Fluorite.Modding.Content.ContentLocation(Fluorite.Modding.Content.ContentKind.Mod), ct), ct);
            issues = Fluorite.Modding.Content.ModChecker.Check(mods, instance.Manifest);
            dismissed = await manager.GetDismissedIssuesAsync(instance.Id, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (Launch, []); // A check that can't run never blocks playing.
        }
        try
        {
            // Fixed since: forgotten, so if they come back they're asked about.
            if (Fluorite.Modding.Content.ModChecker.ResolvedDismissals(dismissed, issues, mods) is { Count: > 0 } resolved)
            {
                await manager.SetIssuesDismissedAsync(instance.Id, resolved, dismissed: false, ct);
                App.Get<ContentInstallService>().NotifyDismissalsChanged(instance); // Or an open Mods tab would still hide them.
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Forgotten next time.
        }
        foreach (var issue in log ? issues : [])
        {
            // Dismissed ones too: if the game then crashes, the log still says what was off.
            var text = ContentInstallService.Describe(issue);
            AppendLocal(game, issue.Severity == Fluorite.Modding.Content.ModIssueSeverity.Error ? GameLogLevel.Error : GameLogLevel.Warn,
                dismissed.Contains(issue.Key) ? Loc.Format("ModIssue_Dismissed", text) : text);
        }
        var active = issues.Where(i => !dismissed.Contains(i.Key)).ToList();
        if (!active.Any(i => i.Severity == Fluorite.Modding.Content.ModIssueSeverity.Error))
            return (Launch, []);
        var keys = active.Select(i => i.Key).ToList();
        var (choice, dontAskAgain) = await ContentInstallService.ConfirmLaunchAsync(instance.Name, active, offerFix);
        if (choice == Launch && dontAskAgain)
        {
            try
            {
                // The errors, which are what asks; warnings never do, and stay shown in the Mods tab.
                var errors = active.Where(i => i.Severity == Fluorite.Modding.Content.ModIssueSeverity.Error).Select(i => i.Key);
                await manager.SetIssuesDismissedAsync(instance.Id, errors, dismissed: true, ct);
                App.Get<ContentInstallService>().NotifyDismissalsChanged(instance); // An open Mods tab shows them dismissed.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Asked again next time; nothing worse.
            }
        }
        return (choice, keys);
    }

    private async Task<string> EnsureLoaderAsync(RunningGame game, CancellationToken ct)
    {
        var instance = game.Instance;
        var loader = ComponentResolver.PrimaryLoader(instance.Manifest);
        if (loader is not null)
        {
            var name = loaders.Get(loader.Uid).DisplayName;
            game.Status = Loc.Format("Launch_InstallingLoader", name);
        }
        // Detailed installer steps are English diagnostics; they go to the log, the status line stays localized.
        var status = new Progress<string>(msg => AppendLocal(game, GameLogLevel.Info, msg));
        var download = new Progress<Core.Net.DownloadProgress>(game.Report);
        return await Task.Run(() => loaders.EnsureInstalledAsync(instance, () => registry.SaveAsync(instance), status, download, ct), ct);
    }

    /// <summary>Reads the session's log, Minecraft's crash report and Java's crash log for the cause of a crash.</summary>
    private static async Task<Core.Diagnostics.CrashAnalysis?> AnalyzeAsync(RunningGame game, LaunchedGame launched, int exitCode, DateTimeOffset since)
    {
        try
        {
            return await Task.Run(() => Core.Diagnostics.CrashAnalyzer.Analyze(new Core.Diagnostics.CrashContext
            {
                Log = game.Archive.ToText(),
                ExitCode = exitCode,
                GameDirectory = game.Instance.GameDirectory,
                Since = since,
                JavaMajor = launched.Prepared.Java.Major,
                JavaArch = launched.Prepared.Java.Arch,
                RequiredJavaMajor = launched.Prepared.Version.JavaVersion?.MajorVersion,
            }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return null; // Analysis is a bonus; the crash itself is still reported.
        }
    }

    /// <param name="autoMemory">Set when the memory was sized automatically: the inputs, for the log.</param>
    private LaunchOptions BuildOptions(GameInstance instance, GameSession session, QuickPlay? quickPlay, out (int Mods, long SystemGb)? autoMemory)
    {
        var g = settings.Current;
        var o = instance.Manifest.Settings;
        var jvm = CommandLine.Split(g.JvmArguments);
        jvm.AddRange(CommandLine.Split(o.JvmArguments));
        var env = new Dictionary<string, string?>();
        foreach (var (k, v) in o.Environment ?? [])
            env[k] = v;

        autoMemory = null;
        var maxMemory = o.MaxMemoryMb;
        if (maxMemory is null && g.AutoMemory)
        {
            var mods = MemoryAdvisor.CountMods(instance.GameDirectory);
            var system = MemoryAdvisor.SystemMemoryMb;
            maxMemory = MemoryAdvisor.Recommend(system, mods);
            autoMemory = (mods, (system + 512) / 1024);
        }

        return new LaunchOptions
        {
            GameDirectory = instance.GameDirectory,
            NativesDirectory = instance.NativesDirectory,
            Session = session,
            MaxMemoryMb = maxMemory ?? g.MaxMemoryMb,
            MinMemoryMb = o.MinMemoryMb ?? g.MinMemoryMb,
            Priority = g.ProcessPriority,
            JvmPreset = g.JvmPreset,
            GpuPreference = g.GpuPreference,
            Arm64Mode = o.Arm64Mode ?? Core.Libraries.Arm64Mode.Automatic,
            ExtraJvmArguments = jvm,
            ExtraGameArguments = CommandLine.Split(o.GameArguments),
            JavaPath = o.JavaPath ?? g.JavaPath,
            Resolution = (o.WindowWidth ?? g.WindowWidth, o.WindowHeight ?? g.WindowHeight),
            Fullscreen = o.Fullscreen ?? g.Fullscreen,
            QuickPlay = quickPlay,
            Environment = env,
            WrapperCommand = o.WrapperCommand,
        };
    }

    private static void OnProgress(RunningGame game, LaunchProgress p)
    {
        game.Status = p.Stage switch
        {
            LaunchStage.ResolvingVersion => Loc.Get("Launch_Resolving"),
            LaunchStage.PreparingJava => p.Download is null ? Loc.Get("Launch_Java") : Loc.Get("Launch_InstallingJava"),
            LaunchStage.DownloadingLibraries => Loc.Get("Launch_Libraries"),
            LaunchStage.DownloadingAssets => Loc.Get("Launch_Assets"),
            LaunchStage.ExtractingNatives => Loc.Get("Launch_Natives"),
            LaunchStage.Starting => Loc.Get("Launch_Starting"),
            _ => game.Status,
        };
        if (p.Download is { } d)
            game.Report(d);
        else
            game.ClearDownload();
    }

    /// <summary>Reads output on a background thread and appends to the UI collection in ~100 ms batches.</summary>
    private async Task PumpAsync(RunningGame game, LaunchedGame launched)
    {
        var parser = new GameLogParser();
        var batch = new List<GameLogEntry>();
        var lastFlush = Stopwatch.StartNew();

        await Task.Run(async () =>
        {
            await foreach (var line in launched.Process.Output.ReadAllAsync())
            {
                foreach (var entry in parser.Feed(line.Text, line.IsError, line.Timestamp))
                {
                    batch.Add(entry);
                    game.Archive.Add(entry);
                }
                if (lastFlush.ElapsedMilliseconds > 100 || batch.Count > 500)
                {
                    Flush();
                    lastFlush.Restart();
                }
            }
            foreach (var entry in parser.Flush(DateTimeOffset.Now))
            {
                batch.Add(entry);
                game.Archive.Add(entry);
            }
            Flush();
        });

        void Flush()
        {
            if (batch.Count == 0)
                return;
            var items = batch.ToArray();
            batch.Clear();
            _ui.TryEnqueue(() =>
            {
                foreach (var e in items)
                    game.Log.Add(e);
                // Trim in chunks: every removal shifts the list, so doing it per line keeps the view moving.
                if (game.Log.Count > MaxLogLines + LogTrimChunk)
                {
                    while (game.Log.Count > MaxLogLines)
                        game.Log.RemoveAt(0);
                }
            });
        }
    }

    /// <summary>Zips the worlds this session saved (automatic backups, pruned to the configured count).</summary>
    private async Task BackUpPlayedWorldsAsync(RunningGame game, DateTimeOffset since)
    {
        var store = new Core.Worlds.WorldStore(game.Instance.GameDirectory);
        var keep = Math.Max(1, settings.Current.AutoBackupsToKeep);
        var finalStatus = game.Status;
        try
        {
            var worlds = await Task.Run(() => store.GetWorldsSavedSince(since));
            foreach (var world in worlds)
            {
                game.Status = Loc.Format("Worlds_BackingUp", world.Name);
                await Task.Run(() =>
                {
                    store.Backup(world, automatic: true);
                    store.PruneAutomaticBackups(world.FolderName, keep);
                });
                AppendLocal(game, GameLogLevel.Info, Loc.Format("Worlds_AutoBackedUp", world.Name));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppendLocal(game, GameLogLevel.Warn, Loc.Format("Worlds_AutoBackupFailed", ex.Message));
        }
        finally
        {
            game.Status = finalStatus;
        }
    }

    private void AppendLocal(RunningGame game, GameLogLevel level, string message)
    {
        var entry = new GameLogEntry(DateTimeOffset.Now, level, "Fluorite", null, message, null, false);
        game.Archive.Add(entry);
        _ui.TryEnqueue(() => game.Log.Add(entry));
    }

    private async Task RecordStatsAsync(GameInstance instance, TimeSpan played)
    {
        var stats = instance.Manifest.Stats;
        stats.LastPlayed = DateTimeOffset.Now;
        stats.LastSessionSeconds = (long)played.TotalSeconds;
        stats.TotalPlaySeconds += (long)played.TotalSeconds;
        stats.LaunchCount++;
        try
        {
            await registry.SaveAsync(instance); // Another launcher's instance: recorded in Fluorite's sidecar.
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
        }
    }
}
