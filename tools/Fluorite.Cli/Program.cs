using System.CommandLine;
using System.Diagnostics;
using Fluorite.Auth.Accounts;
using Fluorite.Cli;
using Fluorite.Core.Instances;
using Fluorite.Core.Launch;
using Fluorite.Core.Logging;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var rootOption = new Option<string>("--root") { Description = "Data root (shared store + instances)", Recursive = true, DefaultValueFactory = _ => CliServices.DefaultRoot };
var sourceOption = new Option<DownloadSourceKind>("--source") { Description = "Download source", Recursive = true, DefaultValueFactory = _ => DownloadSourceKind.Official };
var managedOption = new Option<bool>("--managed") { Description = "Use System.Diagnostics.Process instead of the Windows breakaway launcher", Recursive = true };
var parallelOption = new Option<int>("--parallel") { Description = "Parallel downloads", Recursive = true, DefaultValueFactory = _ => 32 };

CliServices Services(ParseResult pr) =>
    new(pr.GetValue(rootOption)!, pr.GetValue(sourceOption), pr.GetValue(managedOption), pr.GetValue(parallelOption));

var root = new RootCommand("Fluorite Launcher developer CLI");
root.Options.Add(rootOption);
root.Options.Add(sourceOption);
root.Options.Add(managedOption);
root.Options.Add(parallelOption);

// ── versions ─────────────────────────────────────────────────────────────
var typeOption = new Option<string>("--type") { Description = "release|snapshot|old_beta|old_alpha|all", DefaultValueFactory = _ => "release" };
var limitOption = new Option<int>("--limit") { DefaultValueFactory = _ => 25 };
var versionsCmd = new Command("versions", "List versions from the Mojang manifest") { typeOption, limitOption };
versionsCmd.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var manifest = await s.Versions.GetManifestAsync(ct: ct);
    var type = pr.GetValue(typeOption);
    Console.WriteLine($"Latest release: {manifest.Latest?.Release}   Latest snapshot: {manifest.Latest?.Snapshot}");
    foreach (var v in manifest.Versions.Where(v => type == "all" || v.Type == type).OrderByDescending(v => v.ParsedReleaseTime).Take(pr.GetValue(limitOption)))
        Console.WriteLine($"  {v.Id,-24} {v.Type,-10} {v.ParsedReleaseTime:yyyy-MM-dd}");
    return 0;
});
root.Subcommands.Add(versionsCmd);

// ── java ─────────────────────────────────────────────────────────────────
var javaCmd = new Command("java", "Java runtime management");
var javaList = new Command("list", "Discover installed Java runtimes");
javaList.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    foreach (var j in await s.Java.GetInstallationsAsync(ct: ct))
        Console.WriteLine($"  Java {j.Major,-3} {j.Version,-14} {j.Arch,-6} {j.Source,-16} {j.Vendor}  {j.Home}");
    return 0;
});
javaCmd.Subcommands.Add(javaList);
root.Subcommands.Add(javaCmd);

// ── prepare / launch / smoke ─────────────────────────────────────────────
var versionArg = new Argument<string>("version") { Description = "Version id (vanilla id or any JSON in versions/)" };
var nameOption = new Option<string>("--name") { Description = "Offline player name (dev harness only)", DefaultValueFactory = _ => "FluoriteDev" };
var memoryOption = new Option<int>("--memory") { DefaultValueFactory = _ => 2048 };
var javaOption = new Option<string?>("--java") { Description = "Java home or executable" };
var gameDirOption = new Option<string?>("--game-dir") { Description = "Game directory (default: <root>/dev-instances/<version>)" };
var serverOption = new Option<string?>("--server") { Description = "Quick-play multiplayer target host[:port]" };
var printOption = new Option<bool>("--print-command") { Description = "Print the redacted command line" };

LaunchOptions BuildOptions(ParseResult pr, CliServices s, string version)
{
    var offline = OfflineAccounts.Create(pr.GetValue(nameOption)!);
    var gameDir = Path.GetFullPath(pr.GetValue(gameDirOption) ?? Path.Combine(s.Root, "dev-instances", version));
    return new LaunchOptions
    {
        GameDirectory = gameDir,
        NativesDirectory = Path.GetFullPath(Path.Combine(gameDir, "..", version + "-natives")),
        Session = new GameSession(offline.PlayerName, offline.Uuid, "0", "legacy"),
        MaxMemoryMb = pr.GetValue(memoryOption),
        JavaPath = pr.GetValue(javaOption),
        QuickPlay = pr.GetValue(serverOption) is { } server ? new QuickPlay(QuickPlayKind.Multiplayer, server) : null,
    };
}

var prepareCmd = new Command("prepare", "Download everything for a version and print the launch command") { versionArg, nameOption, memoryOption, javaOption, gameDirOption };
prepareCmd.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var version = pr.GetValue(versionArg)!;
    var progress = new ConsoleProgress();
    var sw = Stopwatch.StartNew();
    var prepared = await s.Pipeline.PrepareAsync(version, BuildOptions(pr, s, version), progress, ct);
    progress.Done();
    Console.WriteLine($"Prepared {prepared.Version.Id} (MC {prepared.Version.MinecraftVersion}) in {sw.Elapsed.TotalSeconds:F1}s with {prepared.Java}");
    foreach (var w in prepared.Warnings)
        Console.WriteLine($"  warning: {w}");
    Console.WriteLine(prepared.Command.ToDisplayString([]));
    return 0;
});
root.Subcommands.Add(prepareCmd);

var launchCmd = new Command("launch", "Launch a version with an offline dev session and stream its log") { versionArg, nameOption, memoryOption, javaOption, gameDirOption, serverOption, printOption };
launchCmd.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var version = pr.GetValue(versionArg)!;
    var progress = new ConsoleProgress();
    var game = await s.Pipeline.LaunchAsync(version, BuildOptions(pr, s, version), progress, ct);
    progress.Done();
    PrintPrepared(game.Prepared, pr.GetValue(printOption));
    using var reg = ct.Register(game.Process.Kill);
    var exit = await PumpLogAsync(game, _ => false, ct);
    Console.WriteLine($"Game exited with code {exit}.");
    return exit;
});
root.Subcommands.Add(launchCmd);

var timeoutOption = new Option<int>("--timeout") { Description = "Seconds to wait for a startup marker", DefaultValueFactory = _ => 240 };
var aliveOption = new Option<int>("--alive-seconds") { Description = "Treat the run as healthy if still running after N seconds without a marker", DefaultValueFactory = _ => 90 };
var smokeCmd = new Command("smoke", "Install + launch a version, wait for a startup marker, then stop it (exit 0 = healthy)")
    { versionArg, nameOption, memoryOption, javaOption, gameDirOption, timeoutOption, aliveOption, printOption };
smokeCmd.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var version = pr.GetValue(versionArg)!;
    var progress = new ConsoleProgress();
    var sw = Stopwatch.StartNew();
    var game = await s.Pipeline.LaunchAsync(version, BuildOptions(pr, s, version), progress, ct);
    progress.Done();
    return await SmokeAsync(pr, version, game, sw, ct);
});
root.Subcommands.Add(smokeCmd);

async Task<int> SmokeAsync(ParseResult pr, string version, LaunchedGame game, Stopwatch sw, CancellationToken ct)
{
    PrintPrepared(game.Prepared, pr.GetValue(printOption));
    Console.WriteLine($"Started pid {game.Process.Id} after {sw.Elapsed.TotalSeconds:F1}s; waiting for startup marker…");

    // Strong markers mean the renderer/sound initialised. Weak ones happen early (before LWJGL), so on very old
    // versions with sparse logging the run counts as healthy if the game is still alive after --alive-seconds.
    string[] strongMarkers =
    [
        "Backend library: LWJGL", "LWJGL Version", "Sound engine started", "OpenAL initialized",
        "Created: 1024x", "Created: 512x", "Created: 2048x", "Reloading ResourceManager",
    ];
    string[] weakMarkers = ["Setting user:", "Launching wrapped minecraft"];
    string? strong = null, weak = null;

    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(pr.GetValue(timeoutOption)));

    var pump = PumpLogAsync(game, e =>
    {
        weak ??= weakMarkers.FirstOrDefault(m => e.Message.Contains(m, StringComparison.Ordinal));
        strong ??= strongMarkers.FirstOrDefault(m => e.Message.Contains(m, StringComparison.Ordinal));
        return strong is not null;
    }, timeout.Token);
    var alive = Task.Delay(TimeSpan.FromSeconds(pr.GetValue(aliveOption)), timeout.Token);

    try
    {
        await Task.WhenAny(pump, alive, game.Process.Exited);
        if (strong is not null)
            await Task.WhenAny(game.Process.Exited, Task.Delay(5000, ct)); // Must survive a few more seconds.
    }
    catch (OperationCanceledException)
    {
    }

    var exited = game.Process.Exited.IsCompleted;
    var exitCode = exited ? await game.Process.Exited : (int?)null;
    var aliveElapsed = alive.IsCompletedSuccessfully;
    game.Process.Kill();
    await Task.WhenAny(game.Process.Exited, Task.Delay(10_000, CancellationToken.None));

    var healthy = !exited && (strong is not null || aliveElapsed);
    var marker = strong ?? (aliveElapsed ? $"alive {pr.GetValue(aliveOption)}s" + (weak is null ? "" : $" after '{weak}'") : weak);
    Console.WriteLine(healthy
        ? $"SMOKE OK: {version} reached '{marker}' in {sw.Elapsed.TotalSeconds:F1}s"
        : $"SMOKE FAIL: {version} {(exited ? $"exited with code {exitCode}" : "timed out")} (marker: {marker ?? "none"})");
    return healthy ? 0 : 1;
}

// ── instances ────────────────────────────────────────────────────────────
var instanceCmd = new Command("instance", "Instances (Fluorite's own and other launchers' folders used in place)");
var instList = new Command("list");
instList.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    foreach (var i in await s.Registry.GetInstancesAsync(ct))
    {
        Console.WriteLine($"  {i.Id,-36} {i.Name,-28} {i.LaunchVersionId,-32} {(i.Launcher?.ToString() ?? "Fluorite"),-14} {i.GameDirectory}");
        foreach (var w in i.Warnings)
            Console.WriteLine($"      warning: {w}");
    }
    foreach (var (root, error) in s.Registry.RootErrors)
        Console.WriteLine($"  [{root}] {error}");
    return 0;
});
var instName = new Argument<string>("name");
var instVersion = new Option<string>("--version") { Required = true };
var instLoaderOption = new Option<string?>("--loader") { Description = "fabric|quilt|forge|neoforge|… (installs on first launch)" };
var instLoaderVersionOption = new Option<string?>("--loader-version") { Description = "Default: recommended" };
var instCreate = new Command("create") { instName, instVersion, instLoaderOption, instLoaderVersionOption };
instCreate.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var layout = new FluoriteLayout(s.Root);
    var mc = pr.GetValue(instVersion)!;
    var manifest = new InstanceManifest
    {
        Name = pr.GetValue(instName)!,
        Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = mc }],
    };
    if (pr.GetValue(instLoaderOption) is { } alias)
    {
        var provider = s.Loaders.Get(LoaderUid(alias));
        var loaderVersion = pr.GetValue(instLoaderVersionOption);
        if (loaderVersion is null)
        {
            var versions = await provider.GetVersionsAsync(mc, ct);
            loaderVersion = (versions.FirstOrDefault(v => v.Recommended) ?? (versions.Count > 0 ? versions[0] : null))?.Version
                ?? throw new InvalidOperationException($"{provider.DisplayName} has no versions for Minecraft {mc}.");
        }
        manifest.Components.Add(new ComponentRef { Uid = provider.Uid, Version = loaderVersion });
    }
    var inst = await layout.CreateAsync(manifest, ct);
    Console.WriteLine($"Created instance '{inst.Id}' at {inst.Directory}");
    return 0;
});
var instIdArg = new Argument<string>("id");
var instSmoke = new Command("smoke", "Install the instance's loader if needed, launch it, wait for a startup marker, then stop it")
    { instIdArg, nameOption, memoryOption, javaOption, timeoutOption, aliveOption, printOption };
instSmoke.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var instance = await ContentCommands.RequireInstanceAsync(s, pr.GetValue(instIdArg)!, ct);
    foreach (var w in instance.Warnings)
        Console.WriteLine($"  warning: {w}");
    var sw = Stopwatch.StartNew();
    var version = await s.Loaders.EnsureInstalledAsync(instance, () => s.Registry.SaveAsync(instance, ct), new Progress<string>(m => Console.WriteLine($"  … {m}")), ct: ct);
    var o = instance.Manifest.Settings;
    var options = BuildOptions(pr, s, version) with
    {
        GameDirectory = instance.GameDirectory,
        NativesDirectory = instance.NativesDirectory,
        MaxMemoryMb = o.MaxMemoryMb ?? pr.GetValue(memoryOption),
        JavaPath = pr.GetValue(javaOption) ?? o.JavaPath,
        ExtraJvmArguments = CommandLine.Split(o.JvmArguments),
    };
    var progress = new ConsoleProgress();
    var game = await s.PipelineFor(instance).LaunchAsync(version, options, progress, ct);
    progress.Done();
    return await SmokeAsync(pr, $"{instance.Id} ({version})", game, sw, ct);
});
instanceCmd.Subcommands.Add(instList);
instanceCmd.Subcommands.Add(instCreate);
instanceCmd.Subcommands.Add(instSmoke);
root.Subcommands.Add(instanceCmd);
root.Subcommands.Add(ContentCommands.Build(Services));
foreach (var c in PackCommands.Build(Services))
    root.Subcommands.Add(c);

// ── loaders ──────────────────────────────────────────────────────────────
string LoaderUid(string alias) => alias.ToLowerInvariant() switch
{
    "fabric" => ComponentUids.FabricLoader,
    "quilt" => ComponentUids.QuiltLoader,
    "forge" => ComponentUids.Forge,
    "neoforge" => ComponentUids.NeoForge,
    "legacyfabric" => ComponentUids.LegacyFabric,
    "liteloader" => ComponentUids.LiteLoader,
    "optifine" => ComponentUids.OptiFine,
    _ => alias,
};
var loaderArg = new Argument<string>("loader") { Description = "fabric|quilt|forge|neoforge|legacyfabric|liteloader|optifine" };
var mcArg = new Argument<string>("minecraft");
var loaderVersionArg = new Argument<string>("loader-version") { Description = "Exact version, or 'recommended'/'latest'", DefaultValueFactory = _ => "recommended" };
var loaderCmd = new Command("loader", "Mod loaders");
var loaderVersions = new Command("versions", "List loader versions for a Minecraft version") { loaderArg, mcArg, limitOption };
loaderVersions.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var list = await s.Loaders.Get(LoaderUid(pr.GetValue(loaderArg)!)).GetVersionsAsync(pr.GetValue(mcArg)!, ct);
    foreach (var v in list.Take(pr.GetValue(limitOption)))
        Console.WriteLine($"  {v.Version,-28} {(v.Recommended ? "recommended " : "")}{(v.Latest ? "latest" : "")}");
    Console.WriteLine($"{list.Count} version(s)");
    return 0;
});
var loaderInstall = new Command("install", "Install a loader profile into versions/ and print its id") { loaderArg, mcArg, loaderVersionArg };
loaderInstall.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    var provider = s.Loaders.Get(LoaderUid(pr.GetValue(loaderArg)!));
    var mc = pr.GetValue(mcArg)!;
    var requested = pr.GetValue(loaderVersionArg)!;
    var version = requested;
    if (requested is "recommended" or "latest")
    {
        var list = await provider.GetVersionsAsync(mc, ct);
        version = (requested == "latest" ? list.FirstOrDefault(v => v.Latest) : list.FirstOrDefault(v => v.Recommended))?.Version
            ?? (list.Count > 0 ? list[0].Version : null)
            ?? throw new InvalidOperationException($"{provider.DisplayName} has no versions for Minecraft {mc}.");
    }
    var sw = Stopwatch.StartNew();
    var ctx = s.Loaders.CreateContext(new Progress<string>(msg => Console.WriteLine($"  … {msg}")));
    var id = await provider.InstallAsync(mc, version, ctx, ct);
    Console.WriteLine($"Installed {provider.DisplayName} {version} for {mc} as '{id}' in {sw.Elapsed.TotalSeconds:F1}s");
    Console.WriteLine($"PROFILE={id}");
    return 0;
});
loaderCmd.Subcommands.Add(loaderVersions);
loaderCmd.Subcommands.Add(loaderInstall);
root.Subcommands.Add(loaderCmd);

// ── accounts (dev) ───────────────────────────────────────────────────────
var accountCmd = new Command("account", "Account store (developer harness)");
var offName = new Argument<string>("name");
var addOffline = new Command("add-offline", "Add an offline account to <root>/accounts.dat, bypassing ownership gating (dev only)") { offName };
addOffline.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    using var store = new AccountStore(Path.Combine(s.Root, "accounts.dat"), new Fluorite.Platform.Windows.Security.DpapiSecretProtector());
    var data = await store.LoadAsync(ct);
    var account = OfflineAccounts.Create(pr.GetValue(offName)!);
    data.Accounts.Add(account);
    data.SelectedAccountId ??= account.Id;
    await store.SaveAsync(ct);
    Console.WriteLine($"Added offline account {account.PlayerName} ({account.Uuid}); selected: {data.SelectedAccountId == account.Id}");
    return 0;
});
var listAccounts = new Command("list");
listAccounts.SetAction(async (pr, ct) =>
{
    using var s = Services(pr);
    using var store = new AccountStore(Path.Combine(s.Root, "accounts.dat"), new Fluorite.Platform.Windows.Security.DpapiSecretProtector());
    var data = await store.LoadAsync(ct);
    foreach (var a in data.Accounts)
        Console.WriteLine($"  {(a.Id == data.SelectedAccountId ? "*" : " ")} {a.PlayerName,-16} {a.Kind,-10} {a.Uuid}");
    return 0;
});
accountCmd.Subcommands.Add(addOffline);
accountCmd.Subcommands.Add(listAccounts);
root.Subcommands.Add(accountCmd);

return await root.Parse(args).InvokeAsync();

static void PrintPrepared(PreparedLaunch p, bool printCommand)
{
    Console.WriteLine($"Launching {p.Version.Id} with {p.Java}");
    foreach (var w in p.Warnings)
        Console.WriteLine($"  warning: {w}");
    if (printCommand)
        Console.WriteLine(p.Command.ToDisplayString([]));
}

static async Task<int> PumpLogAsync(LaunchedGame game, Func<GameLogEntry, bool> stopWhen, CancellationToken ct)
{
    var parser = new GameLogParser();
    try
    {
        await foreach (var line in game.Process.Output.ReadAllAsync(ct))
        {
            foreach (var entry in parser.Feed(line.Text, line.IsError, line.Timestamp))
            {
                var color = entry.Level switch
                {
                    GameLogLevel.Error or GameLogLevel.Fatal => ConsoleColor.Red,
                    GameLogLevel.Warn => ConsoleColor.Yellow,
                    GameLogLevel.Debug or GameLogLevel.Trace => ConsoleColor.DarkGray,
                    _ => Console.ForegroundColor,
                };
                var prev = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(entry.Thread is null
                    ? entry.Message
                    : $"[{entry.Timestamp:HH:mm:ss}] [{entry.Thread}/{entry.Level.ToString().ToUpperInvariant()}]: {entry.Message}");
                if (entry.Throwable is not null)
                    Console.WriteLine(entry.Throwable);
                Console.ForegroundColor = prev;
                if (stopWhen(entry))
                    return 0;
            }
        }
        return await game.Process.Exited;
    }
    catch (OperationCanceledException)
    {
        return -1;
    }
}
