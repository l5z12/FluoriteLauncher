namespace Fluorite.Core.Versions;

/// <summary>
/// Merges version JSONs the way the official launcher does for <c>inheritsFrom</c>:
/// scalar fields are inherited unless overridden, libraries are child-first, structured
/// arguments are concatenated parent-first, legacy <c>minecraftArguments</c> are replaced.
/// </summary>
public static class VersionMerger
{
    /// <param name="chain">Ordered from base (vanilla) to top (the version being launched).</param>
    public static ResolvedVersion Merge(IReadOnlyList<VersionJson> chain)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Count == 0)
            throw new ArgumentException("Version chain must not be empty.", nameof(chain));

        string? mainClass = null, type = null, mcArgs = null, assets = null;
        AssetIndexReference? assetIndex = null;
        JavaVersionInfo? java = null;
        DownloadArtifact? client = null;
        LoggingConfig? logging = null;
        List<ArgumentEntry>? game = null, jvm = null;
        string jarSource = chain[0].Id;
        DateTimeOffset? releaseTime = null;

        foreach (var v in chain)
        {
            mainClass = v.MainClass ?? mainClass;
            type = v.Type ?? type;
            mcArgs = v.MinecraftArguments ?? mcArgs;
            assets = v.Assets ?? assets;
            assetIndex = v.AssetIndex ?? assetIndex;
            java = v.JavaVersion ?? java;
            releaseTime ??= v.ParsedReleaseTime;

            if (v.Downloads?.TryGetValue("client", out var c) == true && c is not null)
            {
                client = c;
                jarSource = v.Id;
            }
            if (!string.IsNullOrEmpty(v.Jar))
                jarSource = v.Jar;

            if (v.Logging?.TryGetValue("client", out var log) == true && log is not null)
                logging = log;

            if (v.Arguments?.Game is { } g)
                (game ??= []).AddRange(g);
            if (v.Arguments?.Jvm is { } j)
                (jvm ??= []).AddRange(j);
        }

        // Libraries: top patch first so loaders can override vanilla artifacts (dedupe happens after rule evaluation).
        var libraries = new List<Library>();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Libraries is { } libs)
                libraries.AddRange(libs);
        }

        var top = chain[^1];
        return new ResolvedVersion
        {
            Id = top.Id,
            Chain = chain,
            MinecraftVersion = chain[0].Id,
            MainClass = mainClass ?? throw new InvalidDataException($"Version '{top.Id}' has no mainClass."),
            Type = type,
            MinecraftArguments = mcArgs,
            GameArguments = game,
            JvmArguments = jvm,
            AssetIndex = assetIndex,
            Assets = assets,
            JavaVersion = java,
            Libraries = libraries,
            ClientDownload = client,
            ClientLogging = logging,
            JarSourceId = jarSource,
            ReleaseTime = releaseTime,
        };
    }
}
