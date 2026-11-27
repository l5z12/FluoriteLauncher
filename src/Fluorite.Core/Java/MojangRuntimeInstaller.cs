using System.Text.Json;
using Fluorite.Core.Json;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Java;

/// <summary>
/// Installs Mojang's Java runtimes (the ones the official launcher uses) into
/// <c>runtime/&lt;component&gt;/&lt;platform&gt;/&lt;component&gt;</c>, the official layout.
/// Component names are always taken from the version JSON, never hardcoded.
/// </summary>
public sealed class MojangRuntimeInstaller(HttpClient http, DownloadEngine downloader, MirrorTable mirrors)
{
    public const string AllJsonUrl = "https://launchermeta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private Dictionary<string, Dictionary<string, List<MojangRuntimeEntry>>>? _all;

    public async Task<Dictionary<string, Dictionary<string, List<MojangRuntimeEntry>>>> GetAllAsync(CancellationToken ct = default)
    {
        if (_all is not null)
            return _all;
        Exception? last = null;
        foreach (var uri in mirrors.Candidates(AllJsonUrl))
        {
            try
            {
                await using var s = await http.GetStreamAsync(uri, ct).ConfigureAwait(false);
                _all = await JsonSerializer.DeserializeAsync(s, FluoriteJsonContext.Default.DictionaryStringDictionaryStringListMojangRuntimeEntry, ct).ConfigureAwait(false);
                if (_all is not null)
                    return _all;
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidDataException("Mojang runtime index unavailable.");
    }

    /// <summary>Platforms to try, in order: the requested arch, then x64 (which ARM64 Windows can emulate).</summary>
    public static IEnumerable<CpuArch> PlatformFallbacks(CpuArch preferred)
    {
        yield return preferred;
        if (preferred == CpuArch.Arm64)
            yield return CpuArch.X64;
    }

    public async Task<JavaInstallation> InstallAsync(string component, CpuArch arch, string runtimeRoot, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        var all = await GetAllAsync(ct).ConfigureAwait(false);

        foreach (var candidateArch in PlatformFallbacks(arch))
        {
            var platform = candidateArch.MojangRuntimePlatform();
            if (!all.TryGetValue(platform, out var components)
                || !components.TryGetValue(component, out var entries)
                || entries.Count == 0
                || entries[0].Manifest is not { } manifestRef)
                continue;

            var home = Path.Combine(runtimeRoot, component, platform, component);
            var manifest = await FetchManifestAsync(manifestRef, ct).ConfigureAwait(false);

            var items = new List<DownloadItem>();
            foreach (var (relPath, file) in manifest.Files)
            {
                var target = Path.GetFullPath(Path.Combine(home, relPath.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(home, StringComparison.OrdinalIgnoreCase))
                    continue;
                switch (file.Type)
                {
                    case "directory":
                        Directory.CreateDirectory(target);
                        break;
                    case "file" when file.Downloads?.TryGetValue("raw", out var raw) == true && raw is not null:
                        items.Add(new DownloadItem(mirrors.Candidates(raw.Url), target, raw.Sha1, raw.Size)
                        {
                            DisplayName = $"{component}/{relPath}",
                            Executable = file.Executable,
                        });
                        break;
                    // "link" entries only occur on macOS/Linux runtimes.
                }
            }

            await downloader.DownloadAllAsync(items, progress, ct).ConfigureAwait(false);
            await AtomicFile.WriteAllTextAsync(Path.Combine(runtimeRoot, component, platform, ".version"), entries[0].Version?.Name ?? "", ct: ct).ConfigureAwait(false);

            return await JavaProbe.ProbeAsync(home, JavaSource.Fluorite, ct).ConfigureAwait(false)
                ?? throw new InvalidDataException($"Installed runtime '{component}' at {home} could not be probed.");
        }

        throw new NotSupportedException($"Mojang does not publish runtime '{component}' for {arch}.");
    }

    private async Task<MojangRuntimeFileManifest> FetchManifestAsync(MojangRuntimeFileRef reference, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var uri in mirrors.Candidates(reference.Url))
        {
            try
            {
                var bytes = await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false);
                if (reference.Sha1 is not null && !HashUtil.HashEquals(reference.Sha1, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes))))
                {
                    last = new InvalidDataException($"Runtime manifest hash mismatch from {uri.Host}");
                    continue;
                }
                return JsonSerializer.Deserialize(bytes, FluoriteJsonContext.Default.MojangRuntimeFileManifest)
                    ?? throw new InvalidDataException("Empty runtime manifest.");
            }
            catch (HttpRequestException ex)
            {
                last = ex;
            }
        }
        throw last ?? new InvalidDataException("Runtime manifest unavailable.");
    }
}
