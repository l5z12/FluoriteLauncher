namespace Fluorite.Core.Versions;

/// <summary>
/// The result of merging an inheritance chain (or component stack) of version JSONs into one launchable description.
/// </summary>
public sealed record ResolvedVersion
{
    /// <summary>Id of the top-most (launched) version, used for <c>${version_name}</c> and the client jar name.</summary>
    public required string Id { get; init; }

    /// <summary>Patches from base (vanilla) to top (loader), for diagnostics/UI.</summary>
    public required IReadOnlyList<VersionJson> Chain { get; init; }

    /// <summary>The vanilla Minecraft id at the bottom of the chain (e.g. <c>1.21.4</c>).</summary>
    public required string MinecraftVersion { get; init; }

    public required string MainClass { get; init; }
    public string? Type { get; init; }
    public string? MinecraftArguments { get; init; }
    public IReadOnlyList<ArgumentEntry>? GameArguments { get; init; }
    public IReadOnlyList<ArgumentEntry>? JvmArguments { get; init; }
    public AssetIndexReference? AssetIndex { get; init; }
    public string? Assets { get; init; }
    public JavaVersionInfo? JavaVersion { get; init; }

    /// <summary>Libraries in classpath order (top patch first), not yet rule-filtered or de-duplicated.</summary>
    public required IReadOnlyList<Library> Libraries { get; init; }

    public DownloadArtifact? ClientDownload { get; init; }
    public LoggingConfig? ClientLogging { get; init; }

    /// <summary>Id of the version whose jar provides the game (the version that owns <c>downloads.client</c>).</summary>
    public required string JarSourceId { get; init; }

    public DateTimeOffset? ReleaseTime { get; init; }

    /// <summary>True when <see cref="GameArguments"/> exist, i.e. the version uses the 1.13+ argument format.</summary>
    public bool UsesModernArguments => GameArguments is not null;

    /// <summary>Asset index id, falling back to the legacy <c>assets</c> field, then "legacy".</summary>
    public string AssetIndexId => AssetIndex?.Id ?? Assets ?? "legacy";
}
