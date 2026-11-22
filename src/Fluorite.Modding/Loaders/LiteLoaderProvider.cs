using System.Text.Json;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;
using Fluorite.Modding.Json;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Loaders;

/// <summary>
/// LiteLoader (1.5.2–1.12.2). Metadata from liteloader.com's <c>versions.json</c>; jars from the BMCLAPI mirror
/// (Mumfrey's snapshot repository/Jenkins are gone) with the official release path as fallback.
/// </summary>
public sealed class LiteLoaderProvider(HttpClient http, MirrorTable mirrors) : ILoaderProvider
{
    private const string Manifest = "https://dl.liteloader.com/versions/versions.json";
    private const string ManifestHttp = "http://dl.liteloader.com/versions/versions.json";
    private const string Artifact = "com.mumfrey:liteloader";

    private LiteLoaderManifest? _manifest;

    public string Uid => ComponentUids.LiteLoader;
    public string DisplayName => "LiteLoader";

    private async Task<LiteLoaderManifest> GetManifestAsync(CancellationToken ct)
    {
        if (_manifest is not null)
            return _manifest;
        try
        {
            _manifest = await Remote.GetJsonAsync(http, mirrors, Manifest, ModdingJsonContext.Default.LiteLoaderManifest, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            _manifest = await Remote.GetJsonAsync(http, mirrors, ManifestHttp, ModdingJsonContext.Default.LiteLoaderManifest, ct).ConfigureAwait(false);
        }
        return _manifest;
    }

    private static IEnumerable<LiteLoaderBuild> Builds(LiteLoaderMinecraftEntry entry)
    {
        foreach (var group in new[] { entry.Artefacts, entry.Snapshots })
        {
            if (group is not { ValueKind: JsonValueKind.Object } obj || !obj.TryGetProperty(Artifact, out var builds) || builds.ValueKind != JsonValueKind.Object)
                continue;

            // Libraries shared by every build of the stream, used when a build doesn't list its own.
            List<LiteLoaderLibrary>? shared = null;
            if (obj.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                shared = libs.Deserialize(ModdingJsonContext.Default.ListLiteLoaderLibrary);

            foreach (var prop in builds.EnumerateObject())
            {
                if (prop.Value.ValueKind != JsonValueKind.Object)
                    continue;
                var build = prop.Value.Deserialize(ModdingJsonContext.Default.LiteLoaderBuild);
                if (build?.Version is null)
                    continue;
                build.Libraries ??= shared;
                yield return build;
            }
        }
    }

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        if (!manifest.Versions.TryGetValue(minecraftVersion, out var entry))
            return [];
        var versions = Builds(entry).Select(b => b.Version!).Distinct().OrderByDescending(v => v, LoaderVersionComparer.Instance).ToList();
        return versions.Select((v, i) => new LoaderVersion(Uid, minecraftVersion, v, Recommended: i == 0, Latest: i == 0)).ToList();
    }

    public async Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default)
    {
        var manifest = await GetManifestAsync(ct).ConfigureAwait(false);
        var build = manifest.Versions.TryGetValue(minecraftVersion, out var entry)
            ? Builds(entry).FirstOrDefault(b => b.Version == loaderVersion)
            : null;
        if (build is null)
            throw new LoaderInstallException($"LiteLoader {loaderVersion} is not available for Minecraft {minecraftVersion}.");

        var coord = new MavenCoordinate("com.mumfrey", "liteloader", loaderVersion, null, "jar");
        var jarPath = ctx.Dirs.LibraryPath(coord.RelativePath);
        var sources = new List<Uri> { new($"https://bmclapi2.bangbang93.com/liteloader/download?version={Uri.EscapeDataString(loaderVersion)}") };
        if (build.File is not null)
            sources.Add(new Uri($"https://dl.liteloader.com/versions/com/mumfrey/liteloader/{minecraftVersion}/{build.File}"));
        ctx.Status?.Report($"Downloading LiteLoader {loaderVersion}");
        await ctx.Downloader.DownloadAllAsync([new DownloadItem(sources, jarPath) { DisplayName = coord.ToString() }], ctx.Download, ct).ConfigureAwait(false);

        var vanilla = await ctx.Versions.GetOrFetchAsync(minecraftVersion, ct).ConfigureAwait(false);
        var libraries = new List<Library> { new() { Name = coord.ToString() } };
        libraries.AddRange((build.Libraries ?? []).Select(l => new Library { Name = l.Name, Url = l.Url }));

        var profile = new VersionJson
        {
            Id = $"{minecraftVersion}-LiteLoader{minecraftVersion}",
            InheritsFrom = minecraftVersion,
            Jar = minecraftVersion,
            Type = vanilla.Type,
            ReleaseTime = vanilla.ReleaseTime,
            Time = DateTimeOffset.UtcNow.ToString("O"),
            MainClass = "net.minecraft.launchwrapper.Launch",
            MinecraftArguments = $"{vanilla.MinecraftArguments} --tweakClass {build.TweakClass ?? "com.mumfrey.liteloader.launch.LiteLoaderTweaker"}".Trim(),
            Libraries = libraries,
        };
        return await VersionJsonWriter.WriteAsync(ctx.Dirs, profile, ct).ConfigureAwait(false);
    }
}
