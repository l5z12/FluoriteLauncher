using System.Reflection;
using Fluorite.App.ViewModels;
using Fluorite.Auth.Accounts;
using Fluorite.Auth.Microsoft;
using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Java;
using Fluorite.Core.Launch;
using Fluorite.Core.Net;
using Fluorite.Core.News;
using Fluorite.Core.Security;
using Fluorite.Core.Versions;
using Fluorite.Platform.Windows.Processes;
using Fluorite.Platform.Windows.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Fluorite.App.Services;

/// <summary>Composition root for the WinUI app.</summary>
public static class AppHost
{
    public static IHost Build()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((sp, lc) => lc
            .MinimumLevel.Is(StartupFlags.Current.Verbose ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Information)
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "launcher-.log"),
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                // By age, not file count, so days Fluorite isn't used don't stretch how long older logs are kept.
                retainedFileCountLimit: null,
                retainedFileTimeLimit: TimeSpan.FromDays(Math.Clamp(sp.GetRequiredService<SettingsService>().Current.LogRetentionDays, 1, 365)),
                shared: true));

        var s = builder.Services;
        s.AddSingleton<SettingsService>();
        s.AddSingleton(_ => DownloadEngine.CreateDefaultHttpClient());
        s.AddSingleton(sp => new MirrorTable(sp.GetRequiredService<SettingsService>().Current.DownloadSource));
        s.AddSingleton(sp => new DownloadEngine(
            sp.GetRequiredService<HttpClient>(),
            new DownloadEngineOptions { MaxParallelism = sp.GetRequiredService<SettingsService>().Current.MaxParallelDownloads },
            sp.GetRequiredService<ILogger<DownloadEngine>>()));
        s.AddSingleton(_ => new GameDirectories(AppPaths.DataRoot));
        s.AddSingleton(sp => new FluoriteLayout(AppPaths.DataRoot));
        s.AddSingleton(sp => new InstanceRegistry(sp.GetRequiredService<FluoriteLayout>(), () => sp.GetRequiredService<SettingsService>().Current.ExternalRoots));
        s.AddSingleton<VersionRepository>();
        s.AddSingleton<MojangRuntimeInstaller>();
        s.AddSingleton<JavaManager>();
        s.AddSingleton<IGameProcessLauncher, BreakawayProcessLauncher>();
        s.AddSingleton<LaunchPipeline>();
        s.AddSingleton<Fluorite.Modding.Loaders.LoaderService>();
        s.AddSingleton(sp => new Fluorite.Modding.Platforms.Modrinth.ModrinthClient(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<MirrorTable>()));
        s.AddSingleton(sp =>
        {
            List<Fluorite.Modding.Platforms.IContentPlatform> platforms = [sp.GetRequiredService<Fluorite.Modding.Platforms.Modrinth.ModrinthClient>()];
            // Builds without a CurseForge key (see CurseForgeApiKey) run with Modrinth only.
            if (Fluorite.Modding.Platforms.CurseForge.CurseForgeApiKey.Resolve(BuildMetadata("FluoriteCurseForgeApiKey")) is { } curseForgeKey)
                platforms.Add(new Fluorite.Modding.Platforms.CurseForge.CurseForgeClient(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<MirrorTable>(), curseForgeKey));
            return new Fluorite.Modding.Content.ContentService(platforms, sp.GetRequiredService<DownloadEngine>(), sp.GetRequiredService<MirrorTable>());
        });
        s.AddSingleton<ContentInstallService>();
        s.AddSingleton<UpdateBadgeService>();
        s.AddSingleton<SnapshotService>();
        s.AddSingleton(sp => new Fluorite.Modding.Modpacks.ModpackService(sp.GetRequiredService<Fluorite.Modding.Content.ContentService>(), sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<DownloadEngine>(), sp.GetRequiredService<MirrorTable>()));
        s.AddSingleton<ModpackInstallService>();
        s.AddSingleton<OtherLaunchersService>();

        s.AddSingleton<ISecretProtector, DpapiSecretProtector>();
        s.AddSingleton(sp => new AccountStore(Path.Combine(AppPaths.DataRoot, "accounts.dat"), sp.GetRequiredService<ISecretProtector>()));
        s.AddSingleton(new MicrosoftAuthOptions());
        s.AddSingleton<MicrosoftOAuthClient>();
        s.AddSingleton<MinecraftAuthChain>();
        s.AddSingleton<Fluorite.Auth.Yggdrasil.YggdrasilClient>();
        s.AddSingleton(sp => new Fluorite.Auth.Yggdrasil.AuthlibInjector(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<MirrorTable>(), Path.Combine(AppPaths.DataRoot, "authlib-injector")));
        s.AddSingleton<Fluorite.Auth.Yggdrasil.LocalSkinServer>();
        s.AddSingleton(sp => new AccountService(
            sp.GetRequiredService<AccountStore>(),
            sp.GetRequiredService<MicrosoftOAuthClient>(),
            sp.GetRequiredService<MinecraftAuthChain>(),
            sp.GetRequiredService<MicrosoftAuthOptions>(),
            sp.GetRequiredService<Fluorite.Auth.Yggdrasil.YggdrasilClient>(),
            sp.GetRequiredService<Fluorite.Auth.Yggdrasil.AuthlibInjector>(),
            developerOfflineOverride: IsDeveloperOfflineOverride())
        {
            LocalSkins = sp.GetRequiredService<Fluorite.Auth.Yggdrasil.LocalSkinServer>(),
            Broker = new Fluorite.Platform.Windows.Security.WamAccountBroker(
                sp.GetRequiredService<MicrosoftAuthOptions>().ClientId,
                () => App.Window is { } window ? WinRT.Interop.WindowNative.GetWindowHandle(window) : 0),
        });

        s.AddSingleton<MinecraftProfileClient>();
        s.AddSingleton(sp => new AccountSkins(
            sp.GetRequiredService<AccountService>(), sp.GetRequiredService<MinecraftAuthChain>(), sp.GetRequiredService<MinecraftProfileClient>(),
            sp.GetRequiredService<Fluorite.Auth.Yggdrasil.YggdrasilClient>(), sp.GetRequiredService<HttpClient>(),
            Path.Combine(AppPaths.DataRoot, "skins"), Path.Combine(AppPaths.DataRoot, "cache", "skins")));

        s.AddSingleton<GameRunner>();
        s.AddSingleton(sp => new Fluorite.Core.Diagnostics.MclogsClient(sp.GetRequiredService<HttpClient>()));
        s.AddSingleton<DiagnosticsService>();
        s.AddSingleton<DiscordService>();
        s.AddSingleton<NotificationService>();
        s.AddSingleton<TrayService>();
        s.AddSingleton<JumpListService>();
        s.AddSingleton<ShortcutService>();
        s.AddSingleton<QuickPlaySupport>();
        s.AddSingleton<Fluorite.Core.Servers.ISrvResolver, Fluorite.Platform.Windows.Network.DnsSrvResolver>();
        s.AddSingleton(sp => new Fluorite.Core.Servers.ServerPinger(sp.GetRequiredService<Fluorite.Core.Servers.ISrvResolver>()));
        s.AddSingleton(sp => new MojangNewsClient(sp.GetRequiredService<HttpClient>(), Path.Combine(AppPaths.DataRoot, "cache", "news")));

        s.AddSingleton<HomeViewModel>();
        s.AddSingleton<LibraryViewModel>();
        s.AddSingleton<AccountsViewModel>();
        s.AddSingleton<SettingsViewModel>();
        s.AddTransient<InstanceViewModel>();
        s.AddTransient<LoaderPickerViewModel>();
        s.AddTransient<BrowseViewModel>();
        s.AddTransient<ProjectViewModel>();
        s.AddTransient<ContentListViewModel>();
        s.AddTransient<WorldsViewModel>();
        s.AddTransient<ServersViewModel>();
        s.AddTransient<ScreenshotsViewModel>();
        s.AddTransient<SnapshotsViewModel>();
        s.AddTransient<SkinViewModel>();

        return builder.Build();
    }

    private static string? BuildMetadata(string key) =>
        typeof(AppHost).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;

    private static bool IsDeveloperOfflineOverride()
    {
#if DEBUG
        return Environment.GetEnvironmentVariable("FLUORITE_DEV_ALLOW_OFFLINE") == "1";
#else
        return false;
#endif
    }
}
