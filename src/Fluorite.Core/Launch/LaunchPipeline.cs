using System.Globalization;
using Fluorite.Core.Assets;
using Fluorite.Core.Java;
using Fluorite.Core.Libraries;
using Fluorite.Core.Logging;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fluorite.Core.Launch;

public enum LaunchStage
{
    ResolvingVersion,
    PreparingJava,
    DownloadingLibraries,
    DownloadingAssets,
    ExtractingNatives,
    Starting,
    Running,
}

public readonly record struct LaunchProgress(LaunchStage Stage, string? Detail = null, DownloadProgress? Download = null);

/// <summary>Everything needed to start the JVM, plus the inputs that produced it (for diagnostics).</summary>
public sealed record LaunchCommand(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?> Environment)
{
    /// <summary>Command line with secrets redacted, safe to log or upload.</summary>
    public string ToDisplayString(IEnumerable<string> secrets)
    {
        var text = CommandLine.Quote(Executable) + " " + CommandLine.Join(Arguments);
        foreach (var s in secrets.Where(s => s.Length >= 8))
            text = text.Replace(s, "********", StringComparison.Ordinal);
        return text;
    }
}

public sealed record PreparedLaunch(ResolvedVersion Version, JavaInstallation Java, LaunchCommand Command, IReadOnlyList<string> Warnings)
{
    /// <summary>The Java @argfile the command reads its arguments from, when they're too long for a command line.</summary>
    public string? ArgumentFile { get; init; }
}

public sealed class LaunchedGame(PreparedLaunch prepared, IGameProcess process)
{
    public PreparedLaunch Prepared { get; } = prepared;
    public IGameProcess Process { get; } = process;
}

/// <summary>
/// Resolve → Java → libraries/client/logging → assets → natives → arguments → start.
/// Shared by the WinUI app and the CLI.
/// </summary>
public sealed class LaunchPipeline(
    VersionRepository versions,
    JavaManager java,
    DownloadEngine downloader,
    MirrorTable mirrors,
    IGameProcessLauncher processLauncher,
    ILogger<LaunchPipeline>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<LaunchPipeline>.Instance;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, LaunchPipeline> _stores = new(StringComparer.OrdinalIgnoreCase);
    private GameDirectories Dirs => versions.Directories;

    /// <summary>
    /// A pipeline that launches from another store (an official/HMCL/PCL <c>.minecraft</c> used in place). Java
    /// runtimes stay Fluorite's; versions, libraries and assets come from (and download into) <paramref name="store"/>.
    /// </summary>
    public LaunchPipeline ForStore(GameDirectories store, HttpClient http) =>
        string.Equals(store.Root, Dirs.Root, StringComparison.OrdinalIgnoreCase)
            ? this
            : _stores.GetOrAdd(store.Root, _ => new LaunchPipeline(new VersionRepository(store, http, mirrors), java, downloader, mirrors, processLauncher, logger));

    /// <summary><c>latest-release</c>/<c>latest-snapshot</c> (official launcher profiles) → the current version id.</summary>
    public async Task<string> ResolveAliasAsync(string versionId, CancellationToken ct = default)
    {
        if (versionId is not ("latest-release" or "latest-snapshot"))
            return versionId;
        var manifest = await versions.GetManifestAsync(ct: ct).ConfigureAwait(false);
        return (versionId == "latest-release" ? manifest.Latest?.Release : manifest.Latest?.Snapshot)
            ?? throw new VersionNotFoundException(versionId);
    }

    /// <summary>Downloads and verifies everything a version needs without starting it.</summary>
    public async Task<PreparedLaunch> PrepareAsync(string versionId, LaunchOptions options, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();

        progress?.Report(new(LaunchStage.ResolvingVersion, versionId));
        versionId = await ResolveAliasAsync(versionId, ct).ConfigureAwait(false);
        var version = await versions.ResolveAsync(versionId, ct).ConfigureAwait(false);

        // ── Java ──────────────────────────────────────────────────────────
        progress?.Report(new(LaunchStage.PreparingJava));
        JavaInstallation runtime;
        if (!string.IsNullOrWhiteSpace(options.JavaPath))
        {
            runtime = await JavaManager.ProbeUserPathAsync(options.JavaPath, ct).ConfigureAwait(false)
                ?? throw new FileNotFoundException($"'{options.JavaPath}' is not a usable Java installation.");
        }
        else
        {
            // ARM64 PCs: ARM64 Java when the version has (or can be given) ARM64 natives, unless emulation is asked for.
            var arm64 = Arm64Lwjgl.Classify(version.Libraries);
            var supportsArm64 = options.Arm64Mode != Arm64Mode.Emulate && arm64 != Arm64Support.Emulated;
            var req = JavaRequirement.For(version.JavaVersion?.MajorVersion, version.JavaVersion?.Component, supportsArm64, RuntimePlatform.Current.OsArch);
            runtime = await java.EnsureAsync(req, new Progress<DownloadProgress>(d => progress?.Report(new(LaunchStage.PreparingJava, "Installing Java", d))), ct: ct).ConfigureAwait(false);
        }
        if (runtime.Arch == CpuArch.Arm64)
        {
            switch (Arm64Lwjgl.Classify(version.Libraries))
            {
                case Arm64Support.SwappedLwjgl:
                    version = version with { Libraries = Arm64Lwjgl.Apply(version.Libraries) };
                    CoreLog.SwappedLwjgl(_logger, Arm64Lwjgl.Version, version.Id);
                    break;
                case Arm64Support.Emulated:
                    warnings.Add("This Minecraft version has no ARM64 build (it uses LWJGL 2), so it needs an x64 Java, which Windows runs under emulation.");
                    break;
            }
        }
        var required = version.JavaVersion?.MajorVersion ?? 8;
        if (runtime.Major < required)
            warnings.Add($"Java {runtime.Major} is older than the required Java {required}; the game will likely fail to start.");
        if (runtime.Arch == CpuArch.X86)
            warnings.Add("32-bit Java limits memory to about 1 GB.");

        // ── Libraries, client jar, logging config ─────────────────────────
        var features = new LaunchFeatures
        {
            IsDemoUser = options.Session.IsDemo,
            HasCustomResolution = options.Resolution is not null,
            HasQuickPlaysSupport = options.QuickPlay is not null,
            IsQuickPlaySingleplayer = options.QuickPlay?.Kind == QuickPlayKind.Singleplayer,
            IsQuickPlayMultiplayer = options.QuickPlay?.Kind == QuickPlayKind.Multiplayer,
            IsQuickPlayRealms = options.QuickPlay?.Kind == QuickPlayKind.Realms,
        };
        var ctx = RuleContext.ForCurrentOs(runtime.Arch, features);
        var libs = LibraryPlanner.Plan(version, Dirs, ctx, mirrors);

        var items = new List<DownloadItem>(libs.Downloads);
        var sourceJar = Dirs.VersionJar(version.JarSourceId);
        if (version.ClientDownload is { Url: { Length: > 0 } clientUrl } client)
            items.Add(new DownloadItem(mirrors.Candidates(clientUrl), sourceJar, client.Sha1, client.Size) { DisplayName = $"{version.JarSourceId}.jar" });

        string? loggingArgument = null;
        if (version.ClientLogging is { Argument: { } logArg, File: { } logFile })
        {
            var logPath = Path.Combine(Dirs.LogConfigs, logFile.Id);
            if (!string.IsNullOrEmpty(logFile.Url))
                items.Add(new DownloadItem(mirrors.Candidates(logFile.Url), logPath, logFile.Sha1, logFile.Size) { DisplayName = logFile.Id });
            loggingArgument = logArg.Replace("${path}", logPath, StringComparison.Ordinal);
        }

        progress?.Report(new(LaunchStage.DownloadingLibraries));
        await downloader.DownloadAllAsync(items, new Progress<DownloadProgress>(d => progress?.Report(new(LaunchStage.DownloadingLibraries, null, d))), ct).ConfigureAwait(false);

        var missing = libs.LocallyGeneratedArtifacts.Where(p => !File.Exists(p)).ToList();
        if (missing.Count > 0)
            throw new LoaderNotInstalledException(version.Id, missing);

        if (!File.Exists(sourceJar))
            throw new FileNotFoundException($"The game jar for '{version.JarSourceId}' is missing and has no download.", sourceJar);

        // The jar on the classpath is named after the launched id (Forge/NeoForge ignore "${version_name}.jar").
        var clientJar = Dirs.VersionJar(version.Id);
        if (!string.Equals(sourceJar, clientJar, StringComparison.OrdinalIgnoreCase))
            MaterializeJar(sourceJar, clientJar);

        // ── Assets ────────────────────────────────────────────────────────
        progress?.Report(new(LaunchStage.DownloadingAssets));
        Directory.CreateDirectory(options.GameDirectory);
        var assets = new AssetManager(Dirs, downloader, mirrors);
        var assetPlan = await assets.PlanAsync(version, ct).ConfigureAwait(false);
        await downloader.DownloadAllAsync(assetPlan.Downloads, new Progress<DownloadProgress>(d => progress?.Report(new(LaunchStage.DownloadingAssets, null, d))), ct).ConfigureAwait(false);
        var gameAssets = assets.MaterializeLegacyLayouts(assetPlan, options.GameDirectory);

        // ── Natives ───────────────────────────────────────────────────────
        progress?.Report(new(LaunchStage.ExtractingNatives));
        // Per architecture: an ARM64 run must not find an emulated x64 run's DLLs on java.library.path.
        var archSuffix = runtime.Arch.ToString().ToLowerInvariant();
        var nativesDir = options.NativesDirectory is { } own
            ? (runtime.Arch is CpuArch.X64 or CpuArch.Unknown ? own : own + "-" + archSuffix)
            : Path.Combine(Dirs.VersionDir(version.Id), "natives-" + archSuffix);
        NativesExtractor.Extract(libs.Natives, nativesDir);

        // ── Arguments ─────────────────────────────────────────────────────
        var classpath = new List<string>(libs.Classpath) { clientJar };
        var vars = BuildVariables(version, options, runtime, classpath, nativesDir, gameAssets);
        var userJvm = BuildUserJvmArgs(options, runtime, warnings);
        var built = ArgumentBuilder.Build(version, ctx, vars, userJvm, loggingArgument, options);
        if (options.QuickPlay is { Kind: QuickPlayKind.Singleplayer or QuickPlayKind.Realms } qp
            && !built.Game.Contains(qp.Kind == QuickPlayKind.Singleplayer ? "--quickPlaySingleplayer" : "--quickPlayRealms"))
            warnings.Add($"Minecraft {version.Id} can't open a world or realm directly (that needs 23w14a or later); it starts at the title screen.");

        var args = new List<string>(built.Jvm.Count + built.Game.Count + 1);
        args.AddRange(built.Jvm);
        args.Add(built.MainClass);
        args.AddRange(built.Game);

        (args, var argFile) = ApplyCommandLineLimits(args, runtime, classpath, vars["classpath"], options, version.Id);

        var executable = runtime.LaunchExecutable;
        if (!string.IsNullOrWhiteSpace(options.WrapperCommand))
        {
            var wrapper = CommandLine.Split(options.WrapperCommand);
            if (wrapper.Count > 0)
            {
                args.Insert(0, executable);
                args.InsertRange(0, wrapper.Skip(1));
                executable = wrapper[0];
            }
        }

        var command = new LaunchCommand(executable, args, options.GameDirectory, options.Environment);
        if (_logger.IsEnabled(LogLevel.Information))
            CoreLog.Prepared(_logger, version.Id, runtime.ToString(), command.ToDisplayString([options.Session.AccessToken]));
        return new PreparedLaunch(version, runtime, command, warnings) { ArgumentFile = argFile };
    }

    public async Task<LaunchedGame> LaunchAsync(string versionId, LaunchOptions options, IProgress<LaunchProgress>? progress = null, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(versionId, options, progress, ct).ConfigureAwait(false);
        progress?.Report(new(LaunchStage.Starting));
        var cmd = prepared.Command;
        IGameProcess process;
        try
        {
            process = processLauncher.Start(new ProcessSpec(cmd.Executable, cmd.Arguments, cmd.WorkingDirectory, cmd.Environment, options.Priority,
                options.GpuPreference, prepared.Java.LaunchExecutable));
        }
        catch when (prepared.ArgumentFile is { } unused)
        {
            ClasspathStrategy.DeleteArgFile(unused);
            throw;
        }
        // The argfile holds the account's token; once the game is over nothing needs it.
        if (prepared.ArgumentFile is { } argFile)
            _ = process.Exited.ContinueWith(_ => ClasspathStrategy.DeleteArgFile(argFile), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        progress?.Report(new(LaunchStage.Running, process.Id.ToString(CultureInfo.InvariantCulture)));
        return new LaunchedGame(prepared, process);
    }

    private Dictionary<string, string> BuildVariables(ResolvedVersion version, LaunchOptions o, JavaInstallation runtime, List<string> classpath, string nativesDir, string gameAssets)
    {
        var s = o.Session;
        var (host, port) = o.QuickPlay is { Kind: QuickPlayKind.Multiplayer } mp ? ArgumentBuilder.SplitHostPort(mp.Target) : ("", "");
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = s.PlayerName,
            ["auth_uuid"] = s.Uuid,
            ["auth_access_token"] = s.AccessToken,
            ["auth_session"] = $"token:{s.AccessToken}:{s.Uuid}",
            ["auth_xuid"] = s.Xuid ?? "0",
            ["clientid"] = s.ClientId ?? "0",
            ["user_type"] = s.UserType,
            ["user_properties"] = s.UserProperties,
            ["version_name"] = version.Id,
            ["version_type"] = o.VersionTypeOverride ?? version.Type ?? "release",
            ["game_directory"] = o.GameDirectory,
            ["assets_root"] = Dirs.Assets,
            ["game_assets"] = gameAssets,
            ["assets_index_name"] = version.AssetIndexId,
            ["natives_directory"] = nativesDir,
            ["library_directory"] = Dirs.Libraries,
            ["classpath_separator"] = ";",
            ["classpath"] = string.Join(';', classpath),
            ["launcher_name"] = FluoriteInfo.LauncherName,
            ["launcher_version"] = FluoriteInfo.Version,
            ["resolution_width"] = o.Resolution?.Width.ToString(CultureInfo.InvariantCulture) ?? "854",
            ["resolution_height"] = o.Resolution?.Height.ToString(CultureInfo.InvariantCulture) ?? "480",
            ["quickPlayPath"] = Path.Combine(o.GameDirectory, "quickPlay", "java", "fluorite.json"),
            ["quickPlaySingleplayer"] = o.QuickPlay is { Kind: QuickPlayKind.Singleplayer } sp ? sp.Target : "",
            ["quickPlayMultiplayer"] = o.QuickPlay is { Kind: QuickPlayKind.Multiplayer } ? (port == "25565" ? host : $"{host}:{port}") : "",
            ["quickPlayRealms"] = o.QuickPlay is { Kind: QuickPlayKind.Realms } rq ? rq.Target : "",
            ["primary_jar"] = classpath[^1],
            ["primary_jar_name"] = Path.GetFileName(classpath[^1]),
            ["java_major"] = runtime.Major.ToString(CultureInfo.InvariantCulture),
        };
    }

    private static List<string> BuildUserJvmArgs(LaunchOptions o, JavaInstallation runtime, List<string> warnings)
    {
        var args = new List<string>();
        var max = o.MaxMemoryMb;
        if (runtime.Arch == CpuArch.X86 && max > 1024)
        {
            warnings.Add($"Maximum memory reduced from {max} MB to 1024 MB for 32-bit Java.");
            max = 1024;
        }
        if (o.MinMemoryMb is { } min && (max is null || min <= max))
            args.Add($"-Xms{min}m");
        if (max is not null)
            args.Add($"-Xmx{max}m");

        // Make stdout/stderr UTF-8 regardless of the system code page (GBK etc.) so logs aren't garbled.
        args.Add("-Dsun.stdout.encoding=UTF-8");
        args.Add("-Dsun.stderr.encoding=UTF-8");
        if (runtime.Major >= 19)
        {
            args.Add("-Dstdout.encoding=UTF-8");
            args.Add("-Dstderr.encoding=UTF-8");
        }

        // Garbage collector tuning comes first, so the user's own arguments can override any part of it.
        args.AddRange(JvmPresets.For(o.JvmPreset, runtime.Major, runtime.Arch == CpuArch.X86, o.ExtraJvmArguments));
        args.AddRange(o.Session.ExtraJvmArguments);
        args.AddRange(o.ExtraJvmArguments);
        return args;
    }

    /// <returns>The arguments to launch with, and the argfile they're in when that's how they go.</returns>
    private (List<string> Args, string? ArgFile) ApplyCommandLineLimits(List<string> args, JavaInstallation runtime, List<string> classpath, string classpathValue, LaunchOptions o, string versionId)
    {
        var length = CommandLine.Quote(runtime.LaunchExecutable).Length + 1 + CommandLine.Join(args).Length;
        if (length <= o.ArgFileThreshold)
            return (args, null);

        var scratch = Path.Combine(Dirs.VersionDir(versionId), ".fluorite");
        if (runtime.Major >= 9)
        {
            var file = ClasspathStrategy.WriteArgFile(args, scratch);
            CoreLog.UsingArgFile(_logger, length, file);
            return (["@" + file], file);
        }

        // Java 8: replace the classpath with a pathing jar.
        var jar = ClasspathStrategy.WritePathingJar(classpath, scratch);
        CoreLog.UsingPathingJar(_logger, length, jar);
        return (args.Select(a => a == classpathValue ? jar : a).ToList(), null);
    }

    private static void MaterializeJar(string source, string target)
    {
        var src = new FileInfo(source);
        var dst = new FileInfo(target);
        if (dst.Exists && dst.Length == src.Length && dst.LastWriteTimeUtc >= src.LastWriteTimeUtc)
            return;
        Directory.CreateDirectory(dst.DirectoryName!);
        AtomicFile.Copy(source, target);
    }
}

public sealed class LoaderNotInstalledException(string versionId, IReadOnlyList<string> missing)
    : Exception($"'{versionId}' is missing {missing.Count} file(s) produced by its mod loader installer. Reinstall the loader.")
{
    public IReadOnlyList<string> MissingFiles { get; } = missing;
}
