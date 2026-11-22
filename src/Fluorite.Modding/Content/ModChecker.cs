using Fluorite.Core.Instances;
using Fluorite.Modding.Metadata;

namespace Fluorite.Modding.Content;

public enum ModIssueKind
{
    /// <summary>Mods are present but the instance has no mod loader.</summary>
    NoLoader,

    /// <summary>The mod is for a different loader and won't load.</summary>
    WrongLoader,

    /// <summary>The mod declares a Minecraft version range the instance doesn't match.</summary>
    WrongMinecraftVersion,

    /// <summary>The mod needs a different loader version.</summary>
    WrongLoaderVersion,

    /// <summary>Two files provide the same mod id.</summary>
    Duplicate,

    MissingDependency,

    /// <summary>A dependency is installed but outside the required version range.</summary>
    DependencyVersion,

    /// <summary>The mod declares itself incompatible with another installed mod.</summary>
    Incompatible,

    /// <summary>The mod discourages using it with another installed mod (works, with issues).</summary>
    Discouraged,

    /// <summary>OptiFine on Fabric/Quilt needs OptiFabric.</summary>
    OptiFineNeedsOptiFabric,

    /// <summary>A well-known incompatible pair (OptiFine with Sodium/Iris and their ports).</summary>
    KnownConflict,
}

public enum ModIssueSeverity
{
    Warning,
    Error,
}

/// <param name="File">File name of the mod the issue is about.</param>
/// <param name="Subject">Display name of that mod.</param>
public sealed record ModIssue(ModIssueKind Kind, ModIssueSeverity Severity, string File, string Subject)
{
    /// <summary>The other party: dependency id, conflicting mod or duplicate file.</summary>
    public string? Other { get; init; }

    /// <summary>Required version range or the version found.</summary>
    public string? Detail { get; init; }

    /// <summary>File name of the other party when it's a file (provider, conflicting mod, the other copy).</summary>
    public string? OtherFile { get; init; }

    /// <summary>The raw required range (dependency and loader issues), in the syntax of <see cref="Loader"/>.</summary>
    public string? Range { get; init; }

    /// <summary>The loader whose metadata <see cref="Range"/> comes from.</summary>
    public ModLoaderKind? Loader { get; init; }
}

/// <summary>
/// Pre-launch sanity checks over an instance's enabled mods, from their own metadata: wrong loader, Minecraft and
/// loader version constraints, duplicate ids, missing dependencies and declared incompatibilities.
/// Only reports what it's sure about; unparseable constraints are ignored.
/// </summary>
public static class ModChecker
{
    // Ids provided by the game/loader itself rather than by a mod jar.
    private static readonly HashSet<string> s_builtIns = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "forge", "neoforge", "fml", "javafml",
        "lowcodefml", "mcp", "minecraftforge", "liteloader", "mixinextras_neoforge",
    };

    private static readonly string[] s_optiFineConflicts = ["sodium", "iris", "embeddium", "rubidium", "oculus", "magnesium"];

    public static IReadOnlyList<ModIssue> Check(IReadOnlyList<ContentFile> mods, InstanceManifest manifest)
    {
        var loader = ComponentResolver.PrimaryLoader(manifest);
        return Check(mods, loader?.Uid, loader?.Version, manifest.MinecraftVersion);
    }

    public static IReadOnlyList<ModIssue> Check(IReadOnlyList<ContentFile> mods, string? loaderUid, string? loaderVersion, string? minecraftVersion)
    {
        var issues = new List<ModIssue>();
        var enabled = mods.Where(m => m.Enabled && !m.IsDirectory).ToList();
        if (enabled.Count == 0)
            return issues;

        if (loaderUid is null or ComponentUids.OptiFine)
        {
            var real = enabled.Where(m => m.Metadata.Mods.Count > 0).ToList();
            if (real.Count > 0)
                issues.Add(new ModIssue(ModIssueKind.NoLoader, ModIssueSeverity.Warning, real[0].FileName, real[0].DisplayName) { Detail = real.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            return issues;
        }

        // Mods (entries) that will actually load: those whose metadata matches the instance's loader.
        var active = new List<(ContentFile File, ModInfo Mod)>();
        foreach (var file in enabled)
        {
            if (file.Metadata.Mods.Count == 0)
                continue;
            var usable = file.Metadata.Mods.Where(m => Accepts(loaderUid, minecraftVersion, m)).ToList();
            if (usable.Count == 0)
            {
                issues.Add(new ModIssue(ModIssueKind.WrongLoader, ModIssueSeverity.Error, file.FileName, file.DisplayName)
                {
                    Detail = string.Join(", ", file.Metadata.Loaders.Distinct()),
                });
                continue;
            }
            // Multi-loader jars list one entry per loader; the instance's own loader wins.
            active.AddRange(usable.GroupBy(m => m.Id).Select(g => (file, g.First())));
        }

        // Everything the active files make available (plus nested jars and aliases). Like the loaders, the newest
        // copy of an id wins (many mods bundle old Fabric API modules).
        var provided = new Dictionary<string, (ContentFile File, string? Version)>(StringComparer.OrdinalIgnoreCase);
        void Provide(string id, ContentFile file, string? version)
        {
            if (!provided.TryGetValue(id, out var existing)
                || (LooseVersion.TryParse(version, out var v) && (!LooseVersion.TryParse(existing.Version, out var e) || v > e)))
                provided[id] = (file, version);
        }
        foreach (var (file, mod) in active)
        {
            Provide(mod.Id, file, mod.Version);
            foreach (var alias in mod.Provides)
                Provide(alias, file, mod.Version);
        }
        foreach (var file in active.Select(a => a.File).Distinct())
        {
            foreach (var nested in file.Metadata.Nested)
            {
                Provide(nested.Id, file, nested.Version);
                foreach (var alias in nested.Provides)
                    Provide(alias, file, nested.Version);
            }
        }

        // Duplicates: the same top-level id from two files.
        foreach (var group in active.GroupBy(a => a.Mod.Id, StringComparer.OrdinalIgnoreCase))
        {
            var files = group.Select(g => g.File).Distinct().ToList();
            for (var i = 1; i < files.Count; i++)
            {
                issues.Add(new ModIssue(ModIssueKind.Duplicate, ModIssueSeverity.Error, files[i].FileName, files[i].DisplayName)
                {
                    Other = files[0].FileName,
                    OtherFile = files[0].FileName,
                    Detail = group.Key,
                });
            }
        }

        foreach (var (file, mod) in active)
        {
            foreach (var dep in mod.Dependencies)
            {
                switch (dep.Kind)
                {
                    case DependencyKind.Required:
                        CheckRequired(file, mod, dep, loaderUid, loaderVersion, minecraftVersion, provided, issues);
                        break;
                    case DependencyKind.Incompatible or DependencyKind.Discouraged:
                        if (provided.TryGetValue(dep.Id, out var other) && other.File != file
                            && VersionPredicate.Matches(dep.VersionRange, other.Version, mod.Loader) != false)
                        {
                            issues.Add(new ModIssue(dep.Kind == DependencyKind.Incompatible ? ModIssueKind.Incompatible : ModIssueKind.Discouraged,
                                dep.Kind == DependencyKind.Incompatible ? ModIssueSeverity.Error : ModIssueSeverity.Warning, file.FileName, file.DisplayName)
                            {
                                Other = other.File.DisplayName,
                                OtherFile = other.File.FileName,
                                Range = dep.VersionRange,
                                Loader = mod.Loader,
                            });
                        }
                        break;
                }
            }
        }

        // OptiFine has no metadata, so these are hand-written.
        foreach (var optifine in enabled.Where(f => f.Metadata.IsOptiFine))
        {
            if (loaderUid is ComponentUids.FabricLoader or ComponentUids.QuiltLoader && !provided.ContainsKey("optifabric"))
                issues.Add(new ModIssue(ModIssueKind.OptiFineNeedsOptiFabric, ModIssueSeverity.Error, optifine.FileName, optifine.DisplayName));
            foreach (var id in s_optiFineConflicts)
            {
                if (provided.TryGetValue(id, out var conflict))
                    issues.Add(new ModIssue(ModIssueKind.KnownConflict, ModIssueSeverity.Error, optifine.FileName, optifine.DisplayName) { Other = conflict.File.DisplayName, OtherFile = conflict.File.FileName });
            }
        }

        return issues
            .DistinctBy(i => (i.Kind, i.File, i.Other))
            .OrderByDescending(i => i.Severity)
            .ThenBy(i => i.Subject, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void CheckRequired(ContentFile file, ModInfo mod, ModDependency dep, string loaderUid, string? loaderVersion, string? minecraftVersion,
        Dictionary<string, (ContentFile File, string? Version)> provided, List<ModIssue> issues)
    {
        var id = dep.Id;
        if (id.Equals("minecraft", StringComparison.OrdinalIgnoreCase))
        {
            if (VersionPredicate.Matches(dep.VersionRange, minecraftVersion, mod.Loader) == false)
                issues.Add(new ModIssue(ModIssueKind.WrongMinecraftVersion, ModIssueSeverity.Error, file.FileName, file.DisplayName) { Detail = dep.VersionRange });
            return;
        }

        // The instance's own loader: check its version where it's the real one (Quilt reports a compat fabricloader).
        var ownLoader = (id.ToLowerInvariant(), loaderUid) switch
        {
            ("fabricloader", ComponentUids.FabricLoader or ComponentUids.LegacyFabric) => true,
            ("quilt_loader", ComponentUids.QuiltLoader) => true,
            ("forge", ComponentUids.Forge) => true,
            ("neoforge", ComponentUids.NeoForge) => true,
            _ => false,
        };
        if (ownLoader)
        {
            if (VersionPredicate.Matches(dep.VersionRange, ForgeShortVersion(loaderVersion), mod.Loader) == false)
                issues.Add(new ModIssue(ModIssueKind.WrongLoaderVersion, ModIssueSeverity.Error, file.FileName, file.DisplayName) { Other = id, Detail = dep.VersionRange, Range = dep.VersionRange, Loader = mod.Loader });
            return;
        }
        if (s_builtIns.Contains(id))
            return;

        if (!provided.TryGetValue(id, out var provider))
        {
            issues.Add(new ModIssue(ModIssueKind.MissingDependency, ModIssueSeverity.Error, file.FileName, file.DisplayName) { Other = id, Detail = dep.VersionRange, Range = dep.VersionRange, Loader = mod.Loader });
            return;
        }
        if (provider.File != file && VersionPredicate.Matches(dep.VersionRange, provider.Version, mod.Loader) == false)
        {
            issues.Add(new ModIssue(ModIssueKind.DependencyVersion, ModIssueSeverity.Error, file.FileName, file.DisplayName)
            {
                Other = provider.File.DisplayName,
                OtherFile = provider.File.FileName,
                Detail = $"{dep.VersionRange} ≠ {provider.Version}",
                Range = dep.VersionRange,
                Loader = mod.Loader,
            });
        }
    }

    /// <summary>Forge's own version in mods.toml ranges is the short one ("47.2.0"), never "1.20.1-47.2.0".</summary>
    private static string? ForgeShortVersion(string? version) =>
        version is not null && version.Contains('-', StringComparison.Ordinal) && version.Split('-') is [var mc, var rest, ..] && mc.StartsWith("1.", StringComparison.Ordinal)
            ? rest
            : version;

    private static bool Accepts(string loaderUid, string? minecraftVersion, ModInfo mod) => (loaderUid, mod.Loader) switch
    {
        (ComponentUids.FabricLoader or ComponentUids.LegacyFabric, ModLoaderKind.Fabric) => true,
        (ComponentUids.QuiltLoader, ModLoaderKind.Quilt or ModLoaderKind.Fabric) => true,
        (ComponentUids.Forge, ModLoaderKind.Forge or ModLoaderKind.LegacyForge) => true,
        (ComponentUids.NeoForge, ModLoaderKind.NeoForge) => true,
        // NeoForge 1.20.1 is a Forge fork; early NeoForge used mods.toml, so neutral (no "forge" dependency) tomls load too.
        (ComponentUids.NeoForge, ModLoaderKind.Forge) => minecraftVersion == "1.20.1" || !mod.Dependencies.Any(d => d.Id == "forge" && d.Kind == DependencyKind.Required),
        (ComponentUids.LiteLoader, ModLoaderKind.LiteLoader) => true,
        _ => false,
    };
}
