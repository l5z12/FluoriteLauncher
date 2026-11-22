using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Libraries;

/// <summary>A native archive to extract before launch (legacy <c>natives</c> libraries).</summary>
public sealed record NativeArchive(string Path, IReadOnlyList<string> ExcludePrefixes);

public sealed record LibraryPlan(
    IReadOnlyList<string> Classpath,
    IReadOnlyList<NativeArchive> Natives,
    IReadOnlyList<DownloadItem> Downloads,
    IReadOnlyList<string> LocallyGeneratedArtifacts,
    bool HasArm64Natives);

/// <summary>
/// Turns merged libraries into a classpath, a native extraction list and download items.
/// <list type="bullet">
/// <item>Mojang <c>downloads.artifact</c> → classpath.</item>
/// <item>Legacy <c>natives</c> map (with <c>${arch}</c>) → classifier jar extracted; the plain artifact is only
/// added when a <c>downloads.artifact</c> exists (1.13–1.18 LWJGL), never for natives-only libraries.</item>
/// <item>Modern per-platform natives (<c>:natives-windows-arm64</c> in the name) → classpath (LWJGL 3.3 self-extracts).</item>
/// <item>Fabric/Quilt/legacy-Forge maven-style entries (<c>url</c>, <c>sha1</c>, <c>checksums</c>) → classpath.</item>
/// <item>Artifacts with an empty URL are produced by Forge/NeoForge installer processors.</item>
/// </list>
/// Rules are evaluated first, then entries are de-duplicated by <c>group:artifact[:classifier]</c> keeping the first
/// occurrence — libraries arrive top-patch-first so loaders override vanilla.
/// </summary>
public static class LibraryPlanner
{
    public const string DefaultMaven = "https://libraries.minecraft.net/";

    /// <summary>
    /// Extra repositories tried for maven-style libraries whose own repository is dead or incomplete (legacy Forge's
    /// scala/akka libs, LiteLoader's asm-all, …). Checksums still apply, so a wrong artifact can't slip in.
    /// </summary>
    private static readonly string[] s_fallbackRepos = [DefaultMaven, "https://repo1.maven.org/maven2/"];

    public static LibraryPlan Plan(ResolvedVersion version, GameDirectories dirs, RuleContext ctx, MirrorTable mirrors) =>
        Plan(version.Libraries, dirs, ctx, mirrors);

    /// <param name="deduplicate">
    /// True for a game classpath (one version per group:artifact, first wins). False for installer/processor
    /// libraries, where different processors legitimately need different versions of the same artifact.
    /// </param>
    public static LibraryPlan Plan(IReadOnlyList<Library> libraries, GameDirectories dirs, RuleContext ctx, MirrorTable mirrors, bool deduplicate = true)
    {
        var classpath = new List<string>();
        var natives = new List<NativeArchive>();
        var downloads = new List<DownloadItem>();
        var generated = new List<string>();
        var classpathKeys = new HashSet<string>(StringComparer.Ordinal);
        var nativeKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasArm64Natives = false;

        foreach (var lib in libraries)
        {
            if (!RuleEvaluator.IsAllowed(lib.Rules, ctx))
                continue;
            if (!MavenCoordinate.TryParse(lib.Name, out var coord))
                continue;

            if (coord.Classifier is { } cls
                && cls.Contains("windows", StringComparison.OrdinalIgnoreCase)
                && cls.Contains("arm64", StringComparison.OrdinalIgnoreCase))
                hasArm64Natives = true;

            var hasLegacyNatives = lib.Natives is not null;
            var addToClasspath = !hasLegacyNatives || lib.Downloads?.Artifact is not null;

            if (addToClasspath && classpathKeys.Add(deduplicate ? coord.DedupeKey : coord.ToString()))
            {
                var (path, item) = ResolveArtifact(lib, coord, lib.Downloads?.Artifact, dirs, mirrors);
                if (seenPaths.Add(path))
                {
                    classpath.Add(path);
                    if (item is not null)
                        downloads.Add(item);
                    else
                        generated.Add(path);
                }
            }

            if (hasLegacyNatives && ResolveNativeClassifier(lib, ctx) is { } nativeClassifier)
            {
                var nativeCoord = coord.WithClassifier(nativeClassifier);
                if (!nativeKeys.Add(nativeCoord.DedupeKey))
                    continue;

                DownloadArtifact? artifact = null;
                lib.Downloads?.Classifiers?.TryGetValue(nativeClassifier, out artifact);
                var (path, item) = ResolveArtifact(lib, nativeCoord, artifact, dirs, mirrors);
                if (item is not null)
                    downloads.Add(item);
                natives.Add(new NativeArchive(path, lib.Extract?.Exclude ?? []));
            }
        }

        return new LibraryPlan(classpath, natives, downloads, generated, hasArm64Natives);
    }

    private static string? ResolveNativeClassifier(Library lib, RuleContext ctx)
    {
        if (lib.Natives is null || !lib.Natives.TryGetValue(ctx.OsName, out var classifier) || string.IsNullOrEmpty(classifier))
            return null;
        return classifier.Replace("${arch}", ctx.Arch.LegacyBits(), StringComparison.Ordinal);
    }

    /// <returns>Local path, and a download item (null when the artifact is generated locally by an installer).</returns>
    private static (string Path, DownloadItem? Item) ResolveArtifact(
        Library lib, MavenCoordinate coord, DownloadArtifact? artifact, GameDirectories dirs, MirrorTable mirrors)
    {
        var relative = !string.IsNullOrEmpty(artifact?.Path) ? artifact.Path! : coord.RelativePath;
        var path = dirs.LibraryPath(relative);

        var sources = new List<Uri>();
        if (artifact is not null)
        {
            if (string.IsNullOrEmpty(artifact.Url))
                return (path, null);
            sources.AddRange(mirrors.Candidates(artifact.Url));
        }
        else
        {
            var baseUrl = string.IsNullOrEmpty(lib.Url) ? DefaultMaven : lib.Url!;
            sources.AddRange(mirrors.Candidates(baseUrl.TrimEnd('/') + "/" + relative));
            foreach (var repo in s_fallbackRepos)
            {
                foreach (var uri in mirrors.Candidates(repo + relative))
                {
                    if (!sources.Contains(uri))
                        sources.Add(uri);
                }
            }
        }

        var sha1 = artifact?.Sha1 ?? lib.Sha1;
        var size = artifact?.Size ?? lib.Size;
        var item = new DownloadItem(sources, path, sha1, size is > 0 ? size : null)
        {
            AlternativeSha1 = sha1 is null ? lib.Checksums : null,
            DisplayName = coord.ToString(),
        };
        return (path, item);
    }
}
