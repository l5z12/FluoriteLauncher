using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Fluorite.Core.Instances;
using Fluorite.Core.Java;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;
using Fluorite.Modding.Json;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Loaders;

/// <summary>
/// OptiFine from optifine.net (scraped: it has no API) or the BMCLAPI mirror, in the order the download source prefers,
/// each falling back to the other. Standalone installs follow the same approach as HMCL: patch the client with
/// <c>optifine.Patcher</c> (newer builds) into a library jar, and launch through LaunchWrapper (OptiFine's bundled
/// <c>launchwrapper-of</c> for modern Java, Mojang's 1.12 otherwise) with <c>OptiFineTweaker</c>.
/// With Forge, OptiFine is simply dropped into <c>mods/</c>.
/// </summary>
public sealed class OptiFineProvider(HttpClient http, MirrorTable mirrors) : ILoaderProvider
{
    private const string Bmclapi = "https://bmclapi2.bangbang93.com/optifine";

    public string Uid => ComponentUids.OptiFine;
    public string DisplayName => "OptiFine";

    private static bool MirrorFirst(MirrorTable mirrors) =>
        mirrors.Kind == DownloadSourceKind.Bmclapi || (mirrors.Kind == DownloadSourceKind.Auto && mirrors.Prefer);

    public async Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default)
    {
        Func<Task<List<(string Version, bool Preview)>>>[] sources = MirrorFirst(mirrors)
            ? [() => FromBmclapiAsync(minecraftVersion, ct), () => FromOfficialAsync(minecraftVersion, ct)]
            : [() => FromOfficialAsync(minecraftVersion, ct), () => FromBmclapiAsync(minecraftVersion, ct)];

        List<(string Version, bool Preview)> builds = [];
        Exception? failure = null;
        foreach (var source in sources)
        {
            try
            {
                builds = await source().ConfigureAwait(false);
                failure = null;
                if (builds.Count > 0)
                    break; // An empty answer may just be a stale mirror: ask the other source.
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                failure ??= ex;
            }
        }
        if (failure is not null && builds.Count == 0)
            throw failure as HttpRequestException ?? new HttpRequestException(failure.Message, failure);

        var sorted = builds
            .DistinctBy(b => b.Version)
            .OrderBy(b => b.Preview)
            .ThenByDescending(b => b.Version, LoaderVersionComparer.Instance)
            .ToList();
        var recommended = sorted.FirstOrDefault(b => !b.Preview).Version;
        return sorted.Select((b, i) => new LoaderVersion(Uid, minecraftVersion, b.Version, b.Version == recommended, i == 0)).ToList();
    }

    private async Task<List<(string, bool)>> FromOfficialAsync(string minecraftVersion, CancellationToken ct)
    {
        var all = await OptiFineOfficialSite.GetBuildsAsync(http, ct).ConfigureAwait(false);
        return all.Where(b => b.MinecraftVersion == minecraftVersion).Select(b => (b.Version, b.IsPreview)).ToList();
    }

    private async Task<List<(string, bool)>> FromBmclapiAsync(string minecraftVersion, CancellationToken ct)
    {
        try
        {
            var entries = await Remote.GetJsonAsync(http, MirrorTable.Official, $"{Bmclapi}/{Uri.EscapeDataString(minecraftVersion)}",
                ModdingJsonContext.Default.ListOptiFineEntry, ct).ConfigureAwait(false);
            return entries.Select(e => (e.FullVersion, e.IsPreview)).ToList();
        }
        catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound)
        {
            return [];
        }
    }

    /// <summary>Downloads the raw OptiFine jar (cached) from the preferred source, falling back to the other, and returns its path.</summary>
    public static async Task<string> DownloadAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct)
    {
        var path = Path.Combine(ctx.CacheDir, $"OptiFine_{minecraftVersion}_{loaderVersion}.jar");
        if (File.Exists(path) && new FileInfo(path).Length > 1024)
            return path;
        ctx.Status?.Report($"Downloading OptiFine {loaderVersion}");

        Func<Task>[] sources = MirrorFirst(ctx.Mirrors)
            ? [() => FromBmclapiFileAsync(minecraftVersion, loaderVersion, path, ctx, ct), () => FromOfficialFileAsync(minecraftVersion, loaderVersion, path, ctx, ct)]
            : [() => FromOfficialFileAsync(minecraftVersion, loaderVersion, path, ctx, ct), () => FromBmclapiFileAsync(minecraftVersion, loaderVersion, path, ctx, ct)];
        Exception? failure = null;
        foreach (var source in sources)
        {
            try
            {
                await source().ConfigureAwait(false);
                return path;
            }
            catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or IOException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                ctx.Status?.Report($"OptiFine source failed: {ex.Message}");
                failure ??= ex;
            }
        }
        throw new LoaderInstallException($"Could not download OptiFine {loaderVersion} for {minecraftVersion}: {failure?.Message}");
    }

    private static async Task FromOfficialFileAsync(string minecraftVersion, string loaderVersion, string path, LoaderInstallContext ctx, CancellationToken ct)
    {
        IReadOnlyList<OptiFineBuild>? builds = null;
        try
        {
            builds = await OptiFineOfficialSite.GetBuildsAsync(ctx.Http, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            // The file name can be derived without the list.
        }
        var fileName = OptiFineOfficialSite.FileNameFor(minecraftVersion, loaderVersion, builds);
        ctx.Status?.Report($"Downloading {fileName} from optifine.net");
        await OptiFineOfficialSite.DownloadAsync(fileName, path, ct).ConfigureAwait(false);
    }

    private static async Task FromBmclapiFileAsync(string minecraftVersion, string loaderVersion, string path, LoaderInstallContext ctx, CancellationToken ct)
    {
        var (type, patch) = SplitVersion(loaderVersion);
        var url = $"{Bmclapi}/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(type)}/{Uri.EscapeDataString(patch)}";
        ctx.Status?.Report("Downloading OptiFine from BMCLAPI");
        await ctx.Downloader.DownloadAllAsync([new DownloadItem([new Uri(url)], path) { DisplayName = Path.GetFileName(path) }], ctx.Download, ct).ConfigureAwait(false);
    }

    /// <summary>For Forge instances: copy OptiFine into the instance's mods folder.</summary>
    public static async Task InstallAsModAsync(string minecraftVersion, string loaderVersion, string modsDirectory, LoaderInstallContext ctx, CancellationToken ct)
    {
        var target = Path.Combine(modsDirectory, $"OptiFine_{minecraftVersion}_{loaderVersion}.jar");
        if (File.Exists(target))
            return;
        var jar = await DownloadAsync(minecraftVersion, loaderVersion, ctx, ct).ConfigureAwait(false);
        Directory.CreateDirectory(modsDirectory);
        foreach (var old in Directory.EnumerateFiles(modsDirectory, "OptiFine_*.jar"))
            File.Delete(old); // Only one OptiFine at a time.
        File.Copy(jar, target);
    }

    public async Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default)
    {
        var dirs = ctx.Dirs;
        var raw = await DownloadAsync(minecraftVersion, loaderVersion, ctx, ct).ConfigureAwait(false);

        ctx.Status?.Report($"Downloading Minecraft {minecraftVersion}");
        var vanilla = await ctx.Versions.GetOrFetchAsync(minecraftVersion, ct).ConfigureAwait(false);
        var vanillaJar = dirs.VersionJar(minecraftVersion);
        if (vanilla.Downloads?.TryGetValue("client", out var client) == true && client?.Url is { Length: > 0 } url)
            await ctx.Downloader.DownloadAllAsync([new DownloadItem(ctx.Mirrors.Candidates(url), vanillaJar, client.Sha1, client.Size)], ctx.Download, ct).ConfigureAwait(false);

        var libVersion = $"{minecraftVersion}_{loaderVersion}";
        var libPath = dirs.LibraryPath(new MavenCoordinate("optifine", "OptiFine", libVersion, null, "jar").RelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(libPath)!);

        string launchwrapper = "net.minecraft:launchwrapper:1.12";
        using (var zip = ZipFile.OpenRead(raw))
        {
            if (zip.GetEntry("optifine/Patcher.class") is not null)
            {
                ctx.Status?.Report("Patching Minecraft with OptiFine");
                var java = await ctx.Java.EnsureAsync(
                    JavaRequirement.For(vanilla.JavaVersion?.MajorVersion, vanilla.JavaVersion?.Component, true, RuntimePlatform.Current.OsArch),
                    ctx.Download, ct: ct).ConfigureAwait(false);
                await RunPatcherAsync(java, raw, vanillaJar, libPath, ct).ConfigureAwait(false);
            }
            else
            {
                File.Copy(raw, libPath, overwrite: true);
            }

            if (JarTools.ReadText(zip, "launchwrapper-of.txt")?.Trim() is { Length: > 0 } lwVersion)
            {
                var lwCoord = new MavenCoordinate("optifine", "launchwrapper-of", lwVersion, null, "jar");
                JarTools.Extract(zip, $"launchwrapper-of-{lwVersion}.jar", dirs.LibraryPath(lwCoord.RelativePath));
                launchwrapper = lwCoord.ToString();
            }
        }

        const string tweak = "optifine.OptiFineTweaker";
        var profile = new VersionJson
        {
            Id = $"{minecraftVersion}-OptiFine_{loaderVersion}",
            InheritsFrom = minecraftVersion,
            Jar = minecraftVersion,
            Type = vanilla.Type,
            ReleaseTime = vanilla.ReleaseTime,
            Time = DateTimeOffset.UtcNow.ToString("O"),
            MainClass = "net.minecraft.launchwrapper.Launch",
            Libraries =
            [
                new Library { Name = $"optifine:OptiFine:{libVersion}" },
                new Library { Name = launchwrapper },
            ],
        };
        if (vanilla.Arguments?.Game is not null)
            profile.Arguments = new VersionArguments { Game = ["--tweakClass", tweak] };
        else
            profile.MinecraftArguments = $"{vanilla.MinecraftArguments} --tweakClass {tweak}".Trim();

        return await VersionJsonWriter.WriteAsync(dirs, profile, ct).ConfigureAwait(false);
    }

    private static (string Type, string Patch) SplitVersion(string loaderVersion)
    {
        // "HD_U_I6" → ("HD_U", "I6"); "HD_U_I5_pre4" → ("HD_U", "I5_pre4")
        var parts = loaderVersion.Split('_');
        if (parts.Length < 3)
            throw new ArgumentException($"Unrecognised OptiFine version '{loaderVersion}'.", nameof(loaderVersion));
        return ($"{parts[0]}_{parts[1]}", string.Join('_', parts[2..]));
    }

    private static async Task RunPatcherAsync(JavaInstallation java, string optifineJar, string vanillaJar, string output, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(java.JavaExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-cp", optifineJar, "optifine.Patcher", vanillaJar, optifineJar, output })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start Java for the OptiFine patcher.");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0 || !File.Exists(output))
            throw new LoaderInstallException($"OptiFine patcher failed ({p.ExitCode}): {await stderr.ConfigureAwait(false)}{await stdout.ConfigureAwait(false)}");
    }
}
