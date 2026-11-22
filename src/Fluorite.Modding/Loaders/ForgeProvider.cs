using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Json;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Loaders;

/// <summary>Minecraft Forge: versions from maven-metadata.xml + promotions; install via <see cref="ForgeInstaller"/>.</summary>
public sealed class ForgeProvider(HttpClient http, MirrorTable mirrors) : ILoaderProvider
{
    private const string Maven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    private const string Promotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";

    private IReadOnlyList<string>? _all;
    private ForgePromotions? _promos;

    public string Uid => ComponentUids.Forge;
    public string DisplayName => "Forge";

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        // Forge for 1.5.1 and older has no installer (jar mods only).
        if (LoaderVersionComparer.Instance.Compare(minecraftVersion, "1.5.2") < 0 && minecraftVersion.StartsWith("1.", StringComparison.Ordinal))
            return [];

        _all ??= Remote.ParseMavenVersions(await Remote.GetBytesAsync(http, mirrors, $"{Maven}/maven-metadata.xml", ct).ConfigureAwait(false));
        try
        {
            _promos ??= await Remote.GetJsonAsync(http, mirrors, Promotions, ModdingJsonContext.Default.ForgePromotions, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            _promos = new ForgePromotions(); // Promotions are cosmetic.
        }

        var prefix = minecraftVersion + "-";
        _promos.Promos.TryGetValue(minecraftVersion + "-recommended", out var recommended);
        _promos.Promos.TryGetValue(minecraftVersion + "-latest", out var latest);

        return _all
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => v[prefix.Length..])
            .OrderByDescending(v => v, LoaderVersionComparer.Instance)
            .Select(v => new LoaderVersion(Uid, minecraftVersion, v,
                Recommended: recommended is not null && (v == recommended || v.StartsWith(recommended + "-", StringComparison.Ordinal)),
                Latest: latest is not null && (v == latest || v.StartsWith(latest + "-", StringComparison.Ordinal))))
            .ToList();
    }

    public Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default)
    {
        var full = $"{minecraftVersion}-{loaderVersion}";
        var file = $"forge-{full}-installer.jar";
        return ForgeInstaller.InstallAsync($"{Maven}/{full}/{file}", file, minecraftVersion, ctx, ct);
    }
}

/// <summary>
/// NeoForge: <c>net.neoforged:neoforge</c> (1.20.2+; version encodes the Minecraft version) plus the 1.20.1 fork
/// published as <c>net.neoforged:forge</c>.
/// </summary>
public sealed class NeoForgeProvider(HttpClient http, MirrorTable mirrors) : ILoaderProvider
{
    private const string Maven = "https://maven.neoforged.net/releases/net/neoforged";

    private IReadOnlyList<string>? _neoforge;
    private IReadOnlyList<string>? _legacyForge;

    public string Uid => ComponentUids.NeoForge;
    public string DisplayName => "NeoForge";

    /// <summary>
    /// <c>20.4.237</c> → 1.20.4, <c>21.0.1-beta</c> → 1.21, <c>21.1.77</c> → 1.21.1;
    /// year-based <c>26.1.2.126</c> → 26.1.2, <c>26.3.0.101</c> → 26.3.
    /// </summary>
    public static string? MinecraftVersionOf(string neoforgeVersion)
    {
        var parts = neoforgeVersion.Split('-')[0].Split('.');
        if (parts.Length < 2 || !int.TryParse(parts[0], out var major))
            return null;
        if (major >= 25)
        {
            var patch = parts.Length >= 4 ? parts[2] : "0";
            return patch == "0" ? $"{major}.{parts[1]}" : $"{major}.{parts[1]}.{patch}";
        }
        return parts[1] == "0" ? $"1.{major}" : $"1.{major}.{parts[1]}";
    }

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        IEnumerable<string> versions;
        if (minecraftVersion == "1.20.1")
        {
            _legacyForge ??= Remote.ParseMavenVersions(await Remote.GetBytesAsync(http, mirrors, $"{Maven}/forge/maven-metadata.xml", ct).ConfigureAwait(false));
            versions = _legacyForge.Where(v => v.StartsWith("1.20.1-", StringComparison.Ordinal)).Select(v => v["1.20.1-".Length..]);
        }
        else
        {
            _neoforge ??= Remote.ParseMavenVersions(await Remote.GetBytesAsync(http, mirrors, $"{Maven}/neoforge/maven-metadata.xml", ct).ConfigureAwait(false));
            versions = _neoforge.Where(v => MinecraftVersionOf(v) == minecraftVersion);
        }

        var sorted = versions.Distinct().OrderByDescending(v => v, LoaderVersionComparer.Instance).ToList();
        var recommended = sorted.FirstOrDefault(v => !v.Contains("beta", StringComparison.OrdinalIgnoreCase) && !v.Contains("alpha", StringComparison.OrdinalIgnoreCase));
        return sorted.Select((v, i) => new LoaderVersion(Uid, minecraftVersion, v, Recommended: v == recommended, Latest: i == 0)).ToList();
    }

    public Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default)
    {
        if (minecraftVersion == "1.20.1")
        {
            var full = $"1.20.1-{loaderVersion}";
            var legacyFile = $"forge-{full}-installer.jar";
            return ForgeInstaller.InstallAsync($"{Maven}/forge/{full}/{legacyFile}", "neoforged-" + legacyFile, minecraftVersion, ctx, ct);
        }
        var file = $"neoforge-{loaderVersion}-installer.jar";
        return ForgeInstaller.InstallAsync($"{Maven}/neoforge/{loaderVersion}/{file}", file, minecraftVersion, ctx, ct);
    }
}
