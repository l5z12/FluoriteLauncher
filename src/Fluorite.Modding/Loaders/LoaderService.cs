using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Java;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;

namespace Fluorite.Modding.Loaders;

/// <summary>Registry of loader providers plus "make sure this instance's loader is installed".</summary>
public sealed class LoaderService
{
    private readonly Dictionary<string, ILoaderProvider> _providers;
    private readonly GameDirectories _dirs;
    private readonly HttpClient _http;
    private readonly DownloadEngine _downloader;
    private readonly MirrorTable _mirrors;
    private readonly VersionRepository _versions;
    private readonly JavaManager _java;

    public LoaderService(GameDirectories dirs, HttpClient http, DownloadEngine downloader, MirrorTable mirrors, VersionRepository versions, JavaManager java)
    {
        _dirs = dirs;
        _http = http;
        _downloader = downloader;
        _mirrors = mirrors;
        _versions = versions;
        _java = java;
        ILoaderProvider[] all =
        [
            FabricLikeProvider.Fabric(http, mirrors),
            FabricLikeProvider.Quilt(http, mirrors),
            new ForgeProvider(http, mirrors),
            new NeoForgeProvider(http, mirrors),
            FabricLikeProvider.LegacyFabric(http, mirrors),
            new LiteLoaderProvider(http, mirrors),
            new OptiFineProvider(http, mirrors),
        ];
        _providers = all.ToDictionary(p => p.Uid);
        Providers = all;
    }

    /// <summary>In display order.</summary>
    public IReadOnlyList<ILoaderProvider> Providers { get; }

    public ILoaderProvider Get(string uid) =>
        _providers.TryGetValue(uid, out var p) ? p : throw new NotSupportedException($"Unknown loader '{uid}'.");

    private readonly System.Collections.Concurrent.ConcurrentDictionary<(string Uid, string Mc), bool> _availability = new();

    /// <summary>
    /// Which loaders have a build for <paramref name="minecraftVersion"/>, asked of every provider in parallel.
    /// A provider that can't be reached maps to null (unknown); answers are remembered for the session.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, bool?>> GetAvailabilityAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var checks = Providers.Select(async p =>
        {
            if (_availability.TryGetValue((p.Uid, minecraftVersion), out var known))
                return (p.Uid, (bool?)known);
            try
            {
                var available = await p.IsAvailableAsync(minecraftVersion, ct).ConfigureAwait(false);
                _availability[(p.Uid, minecraftVersion)] = available;
                return (p.Uid, available);
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or System.Xml.XmlException
                or TaskCanceledException && !ct.IsCancellationRequested)
            {
                return (p.Uid, (bool?)null);
            }
        });
        var results = await Task.WhenAll(checks).ConfigureAwait(false);
        return results.ToDictionary(r => r.Uid, r => r.Item2);
    }

    public LoaderInstallContext CreateContext(IProgress<string>? status = null, IProgress<DownloadProgress>? download = null) =>
        new(_dirs, _http, _downloader, _mirrors, _versions, _java) { Status = status, Download = download };

    /// <summary>
    /// Installs the instance's loader if its profile JSON is missing, records <see cref="ComponentRef.ProfileId"/>, makes
    /// sure OptiFine is in <c>mods/</c> for Forge instances, and returns the version id to launch.
    /// </summary>
    /// <param name="save">Persists the manifest after <see cref="ComponentRef.ProfileId"/> changes.</param>
    public async Task<string> EnsureInstalledAsync(GameInstance instance, Func<Task> save, IProgress<string>? status = null, IProgress<DownloadProgress>? download = null, CancellationToken ct = default)
    {
        var manifest = instance.Manifest;
        var mc = manifest.MinecraftVersion;
        var loader = ComponentResolver.PrimaryLoader(manifest);
        if (mc is null || loader is null)
            return ComponentResolver.LaunchVersionId(manifest);

        var store = instance.Store;
        if (loader.ProfileId is null || !File.Exists(store.VersionJson(loader.ProfileId)))
        {
            var ctx = new LoaderInstallContext(store, _http, _downloader, _mirrors, StoreVersions(store), _java) { Status = status, Download = download };
            loader.ProfileId = await Get(loader.Uid).InstallAsync(mc, loader.Version, ctx, ct).ConfigureAwait(false);
            await save().ConfigureAwait(false);
        }

        if (ComponentResolver.OptiFineAsMod(manifest) is { } optifine)
        {
            var ctx = CreateContext(status, download);
            await OptiFineProvider.InstallAsModAsync(mc, optifine.Version, Path.Combine(instance.GameDirectory, "mods"), ctx, ct).ConfigureAwait(false);
        }

        return ComponentResolver.LaunchVersionId(manifest);
    }

    private VersionRepository StoreVersions(GameDirectories store) =>
        string.Equals(store.Root, _dirs.Root, StringComparison.OrdinalIgnoreCase) ? _versions : new VersionRepository(store, _http, _mirrors);
}
