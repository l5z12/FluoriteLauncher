using Fluorite.Core;
using Fluorite.Core.Java;
using Fluorite.Core.Net;
using Fluorite.Core.Versions;

namespace Fluorite.Modding.Loaders;

/// <param name="Version">Loader version as the user sees it (e.g. <c>0.16.10</c>, <c>47.2.0</c>, <c>HD_U_I6</c>).</param>
/// <param name="Recommended">Upstream "recommended"/stable flag.</param>
public sealed record LoaderVersion(string Uid, string MinecraftVersion, string Version, bool Recommended, bool Latest = false)
{
    /// <summary>Used by screen readers/UI Automation for list items.</summary>
    public override string ToString() => Version;
}

/// <summary>Everything an installer needs. One per install operation.</summary>
public sealed class LoaderInstallContext(
    GameDirectories dirs, HttpClient http, DownloadEngine downloader, MirrorTable mirrors, VersionRepository versions, JavaManager java)
{
    public GameDirectories Dirs { get; } = dirs;
    public HttpClient Http { get; } = http;
    public DownloadEngine Downloader { get; } = downloader;
    public MirrorTable Mirrors { get; } = mirrors;
    public VersionRepository Versions { get; } = versions;
    public JavaManager Java { get; } = java;

    /// <summary>Human-readable step descriptions ("Running processor 3/9 …").</summary>
    public IProgress<string>? Status { get; init; }

    public IProgress<DownloadProgress>? Download { get; init; }

    public string CacheDir => Path.Combine(Dirs.Root, "cache", "installers");
}

public interface ILoaderProvider
{
    /// <summary>Component uid (see <see cref="Core.Instances.ComponentUids"/>).</summary>
    string Uid { get; }

    string DisplayName { get; }

    /// <summary>Versions available for <paramref name="minecraftVersion"/>, newest first. Empty when unsupported.</summary>
    Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default);

    /// <summary>Whether any build exists for <paramref name="minecraftVersion"/>; providers override it when a cheaper answer exists.</summary>
    async Task<bool> IsAvailableAsync(string minecraftVersion, CancellationToken ct = default) =>
        (await GetVersionsAsync(minecraftVersion, ct).ConfigureAwait(false)).Count > 0;

    /// <summary>Installs into <see cref="LoaderInstallContext.Dirs"/> and returns the id of the created version JSON.</summary>
    Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default);
}
