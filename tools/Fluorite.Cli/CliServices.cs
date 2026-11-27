using System.Reflection;
using System.Text.Json;
using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Java;
using Fluorite.Core.Json;
using Fluorite.Core.Settings;
using Fluorite.Core.Launch;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;
using Fluorite.Platform.Windows.Processes;

namespace Fluorite.Cli;

/// <summary>Composition root for CLI commands (the WinUI app uses DI for the same graph).</summary>
internal sealed class CliServices : IDisposable
{
    public CliServices(string root, DownloadSourceKind source, bool managedProcess, int parallelism)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Http = DownloadEngine.CreateDefaultHttpClient();
        Mirrors = new MirrorTable(source);
        Dirs = new GameDirectories(Root);
        Downloader = new DownloadEngine(Http, new DownloadEngineOptions { MaxParallelism = parallelism });
        Versions = new VersionRepository(Dirs, Http, Mirrors);
        RuntimeInstaller = new MojangRuntimeInstaller(Http, Downloader, Mirrors);
        Java = new JavaManager(Dirs, RuntimeInstaller);
        ProcessLauncher = managedProcess ? new ManagedGameProcessLauncher() : new BreakawayProcessLauncher();
        Pipeline = new LaunchPipeline(Versions, Java, Downloader, Mirrors, ProcessLauncher);
        Loaders = new Fluorite.Modding.Loaders.LoaderService(Dirs, Http, Downloader, Mirrors, Versions, Java);
        List<Fluorite.Modding.Platforms.IContentPlatform> platforms = [new Fluorite.Modding.Platforms.Modrinth.ModrinthClient(Http, Mirrors)];
        // Builds without a CurseForge key (see CurseForgeApiKey) run with Modrinth only.
        var buildKey = typeof(CliServices).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "FluoriteCurseForgeApiKey")?.Value;
        if (Fluorite.Modding.Platforms.CurseForge.CurseForgeApiKey.Resolve(buildKey) is { } curseForgeKey)
            platforms.Add(new Fluorite.Modding.Platforms.CurseForge.CurseForgeClient(Http, Mirrors, curseForgeKey));
        Content = new Fluorite.Modding.Content.ContentService(platforms, Downloader, Mirrors);
        Layout = new FluoriteLayout(Root);
        Registry = new InstanceRegistry(Layout, () => Settings.ExternalRoots);
        Packs = new Fluorite.Modding.Modpacks.ModpackService(Content, Http, Downloader, Mirrors);
    }

    public Fluorite.Modding.Loaders.LoaderService Loaders { get; }
    public Fluorite.Modding.Content.ContentService Content { get; }
    public FluoriteLayout Layout { get; }
    public InstanceRegistry Registry { get; }
    public Fluorite.Modding.Modpacks.ModpackService Packs { get; }

    /// <summary><c>&lt;root&gt;/settings.json</c>, shared with the app (external roots live there).</summary>
    public LauncherSettings Settings => _settings ??= LoadSettings();
    private LauncherSettings? _settings;

    private string SettingsPath => Path.Combine(Root, "settings.json");

    private LauncherSettings LoadSettings()
    {
        try
        {
            return File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(SettingsPath), FluoriteJsonContext.Default.LauncherSettings) ?? new LauncherSettings()
                : new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void SaveSettings() => AtomicFile.WriteAllBytes(SettingsPath, JsonSerializer.SerializeToUtf8Bytes(Settings, FluoriteJsonContext.Default.LauncherSettings));

    /// <summary>The pipeline for the instance's store (another launcher's .minecraft when used in place).</summary>
    public LaunchPipeline PipelineFor(GameInstance instance) => Pipeline.ForStore(instance.Store, Http);

    public string Root { get; }
    public HttpClient Http { get; }
    public MirrorTable Mirrors { get; }
    public GameDirectories Dirs { get; }
    public DownloadEngine Downloader { get; }
    public VersionRepository Versions { get; }
    public MojangRuntimeInstaller RuntimeInstaller { get; }
    public JavaManager Java { get; }
    public IGameProcessLauncher ProcessLauncher { get; }
    public LaunchPipeline Pipeline { get; }

    public static string DefaultRoot =>
        Environment.GetEnvironmentVariable("FLUORITE_ROOT") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluoriteLauncher");

    public void Dispose()
    {
        Content.Dispose();
        Java.Dispose();
        Versions.Dispose();
        Http.Dispose();
    }
}
