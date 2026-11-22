using Fluorite.Core.Net;

namespace Fluorite.Core.Java;

/// <summary>Caches discovered runtimes and installs Mojang runtimes on demand.</summary>
public sealed class JavaManager(GameDirectories dirs, MojangRuntimeInstaller installer) : IDisposable
{
    public void Dispose() => _lock.Dispose();

    private IReadOnlyList<JavaInstallation>? _cache;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<JavaInstallation>> GetInstallationsAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is null || refresh)
                _cache = await JavaDiscovery.DiscoverAsync([dirs.Runtime], ct).ConfigureAwait(false);
            return _cache;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Returns a runtime with the official launcher's priorities: an installed runtime of the exact major version,
    /// else the exact Mojang component (installed on demand), else — e.g. offline — the closest newer installed Java
    /// when the requirement allows it.
    /// </summary>
    public async Task<JavaInstallation> EnsureAsync(JavaRequirement req, IProgress<DownloadProgress>? progress = null, bool allowInstall = true, CancellationToken ct = default)
    {
        var installedList = await GetInstallationsAsync(ct: ct).ConfigureAwait(false);
        var exact = JavaSelector.Select(installedList, req with { StrictMajor = true });
        if (exact is not null)
            return exact;

        Exception? installError = null;
        if (allowInstall)
        {
            try
            {
                var component = req.Component ?? await FindComponentForMajorAsync(req.Major, req.PreferredArch, ct).ConfigureAwait(false);
                if (component is not null)
                {
                    var installed = await installer.InstallAsync(component, req.PreferredArch, dirs.Runtime, progress, ct).ConfigureAwait(false);
                    await GetInstallationsAsync(refresh: true, ct).ConfigureAwait(false);
                    return installed;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or DownloadFailedException or NotSupportedException or IOException)
            {
                installError = ex;
            }
        }

        return JavaSelector.Select(installedList, req)
            ?? throw new JavaNotFoundException(req.Major, installError);
    }

    /// <summary>Probes a user-supplied path, which may be a Java home, a bin folder, or java(w).exe.</summary>
    public static async Task<JavaInstallation?> ProbeUserPathAsync(string path, CancellationToken ct = default)
    {
        var p = path.Trim().Trim('"');
        if (File.Exists(p))
            p = Path.GetDirectoryName(p)!;
        if (Path.GetFileName(p).Equals("bin", StringComparison.OrdinalIgnoreCase))
            p = Path.GetDirectoryName(p)!;
        return await JavaProbe.ProbeAsync(p, JavaSource.User, ct).ConfigureAwait(false);
    }

    internal async Task<string?> FindComponentForMajorAsync(int major, Platform.CpuArch arch, CancellationToken ct)
    {
        var all = await installer.GetAllAsync(ct).ConfigureAwait(false);
        foreach (var candidateArch in MojangRuntimeInstaller.PlatformFallbacks(arch))
        {
            if (!all.TryGetValue(Platform.CpuArchExtensions.MojangRuntimePlatform(candidateArch), out var components))
                continue;
            foreach (var (name, entries) in components)
            {
                if (entries.Count > 0 && entries[0].Version?.Name is { } v && JavaInstallation.ParseMajor(v) == major)
                    return name;
            }
        }
        return null;
    }
}
