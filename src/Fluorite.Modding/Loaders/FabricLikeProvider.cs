using System.Text;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Json;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Loaders;

/// <summary>
/// Fabric, Quilt and Legacy Fabric share one meta API shape:
/// <c>{meta}/versions/loader/{mc}</c> lists loaders and <c>{meta}/versions/loader/{mc}/{loader}/profile/json</c>
/// returns a ready-made version JSON that inherits from vanilla.
/// </summary>
public sealed class FabricLikeProvider(string uid, string displayName, string metaBase, HttpClient http, MirrorTable mirrors) : ILoaderProvider
{
    public static FabricLikeProvider Fabric(HttpClient http, MirrorTable mirrors) =>
        new(ComponentUids.FabricLoader, "Fabric", "https://meta.fabricmc.net/v2", http, mirrors);

    public static FabricLikeProvider Quilt(HttpClient http, MirrorTable mirrors) =>
        new(ComponentUids.QuiltLoader, "Quilt", "https://meta.quiltmc.org/v3", http, mirrors);

    public static FabricLikeProvider LegacyFabric(HttpClient http, MirrorTable mirrors) =>
        new(ComponentUids.LegacyFabric, "Legacy Fabric", "https://meta.legacyfabric.net/v2", http, mirrors);

    private static readonly TimeSpan s_gamesTtl = TimeSpan.FromMinutes(30);
    private (DateTimeOffset At, HashSet<string> Versions)? _games;

    public string Uid => uid;
    public string DisplayName => displayName;

    /// <summary>
    /// Uses <c>{meta}/versions/game</c> (every version with mappings, ~30 KB, cached) instead of the per-version loader
    /// list, which repeats the launcher metadata for every build.
    /// </summary>
    public async Task<bool> IsAvailableAsync(string minecraftVersion, CancellationToken ct = default)
    {
        if (_games is not { } cached || DateTimeOffset.UtcNow - cached.At > s_gamesTtl)
        {
            var list = await Remote.GetJsonAsync(http, mirrors, $"{metaBase}/versions/game", ModdingJsonContext.Default.ListFabricGameEntry, ct).ConfigureAwait(false);
            _games = cached = (DateTimeOffset.UtcNow, list.Select(g => g.Version).ToHashSet(StringComparer.Ordinal));
        }
        return cached.Versions.Contains(minecraftVersion);
    }

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        List<FabricLoaderEntry> entries;
        try
        {
            entries = await Remote.GetJsonAsync(http, mirrors, $"{metaBase}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}",
                ModdingJsonContext.Default.ListFabricLoaderEntry, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.BadRequest)
        {
            return []; // Minecraft version not supported by this loader.
        }

        var versions = entries
            .Where(e => e.Loader is not null)
            .Select(e => e.Loader!)
            .DistinctBy(l => l.Version)
            .OrderByDescending(l => l.Version, LoaderVersionComparer.Instance)
            .ToList();

        // Quilt has no "stable" flag; treat non-beta builds as stable.
        bool IsStable(FabricLoaderInfo l) => l.Stable ?? !l.Version.Contains("beta", StringComparison.OrdinalIgnoreCase);
        var latestStable = versions.FirstOrDefault(IsStable)?.Version;
        return versions
            .Select(l => new LoaderVersion(uid, minecraftVersion, l.Version, Recommended: l.Version == latestStable, Latest: l == versions[0]))
            .ToList();
    }

    public async Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default)
    {
        ctx.Status?.Report($"Installing {displayName} {loaderVersion}");
        var url = $"{metaBase}/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";
        var json = Encoding.UTF8.GetString(await Remote.GetBytesAsync(http, mirrors, url, ct).ConfigureAwait(false));

        // Make sure the vanilla parent exists locally so the profile resolves offline later.
        await ctx.Versions.GetOrFetchAsync(minecraftVersion, ct).ConfigureAwait(false);
        return await VersionJsonWriter.WriteRawAsync(ctx.Dirs, json, patch: null, ct).ConfigureAwait(false);
    }
}
