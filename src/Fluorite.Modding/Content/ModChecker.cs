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

    /// <summary>Mod id of the subject (its file name and display name change with versions and lookups; this doesn't).</summary>
    public string? ModId { get; init; }

    /// <summary>Mod id of the other party (dependency, conflicting mod), where there's one.</summary>
    public string? OtherId { get; init; }

    /// <summary>
    /// The instance's side of the problem, where the mod's side alone doesn't say it: its Minecraft version, its
    /// loader (and that loader's version), the version of the dependency found.
    /// </summary>
    public string? Found { get; init; }

    /// <summary>
    /// What a dismissal remembers: the same problem between the same mods, whatever their files are called. Any
    /// change to either side (what the mod asks for, what the instance has) makes it a new one, which shows again.
    /// Starts with the kind and the subject's mod id (see <see cref="ModChecker.ResolvedDismissals"/>).
    /// </summary>
    public string Key => Kind switch
    {
        ModIssueKind.NoLoader => Join(Kind.ToString()), // Its detail is how many mods there are.
        // These two files: another copy later is another problem.
        ModIssueKind.Duplicate => Join(Kind.ToString(), ModId, string.Join(",", new[] { File, OtherFile }.Order(StringComparer.OrdinalIgnoreCase))),
        _ => Join(Kind.ToString(), ModId ?? File, OtherId ?? Other, Detail, Range, Found),
    };

    private static string Join(params string?[] parts) => string.Join("|", parts.Select(p => p?.ToLowerInvariant()));
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
                    ModId = file.Metadata.Mods[0].Id,
                    Found = loaderUid,
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
            // By file name: each copy is paired with the first, which mustn't change when a title is looked up (scan
            // order is by display name), or the pairs (and their dismissals' keys) would.
            var files = group.Select(g => g.File).Distinct().OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase).ToList();
            for (var i = 1; i < files.Count; i++)
            {
                issues.Add(new ModIssue(ModIssueKind.Duplicate, ModIssueSeverity.Error, files[i].FileName, files[i].DisplayName)
                {
                    Other = files[0].FileName,
                    OtherFile = files[0].FileName,
                    Detail = group.Key,
                    ModId = group.Key,
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
                                ModId = mod.Id,
                                OtherId = dep.Id,
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
                issues.Add(new ModIssue(ModIssueKind.OptiFineNeedsOptiFabric, ModIssueSeverity.Error, optifine.FileName, optifine.DisplayName) { ModId = "optifine" });
            foreach (var id in s_optiFineConflicts)
            {
                if (provided.TryGetValue(id, out var conflict))
                {
                    issues.Add(new ModIssue(ModIssueKind.KnownConflict, ModIssueSeverity.Error, optifine.FileName, optifine.DisplayName)
                    {
                        Other = conflict.File.DisplayName,
                        OtherFile = conflict.File.FileName,
                        ModId = "optifine",
                        OtherId = id,
                    });
                }
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
                issues.Add(new ModIssue(ModIssueKind.WrongMinecraftVersion, ModIssueSeverity.Error, file.FileName, file.DisplayName) { Detail = dep.VersionRange, ModId = mod.Id, Found = minecraftVersion });
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
            {
                issues.Add(new ModIssue(ModIssueKind.WrongLoaderVersion, ModIssueSeverity.Error, file.FileName, file.DisplayName)
                {
                    Other = id,
                    Detail = dep.VersionRange,
                    Range = dep.VersionRange,
                    Loader = mod.Loader,
                    ModId = mod.Id,
                    OtherId = id,
                    Found = $"{loaderUid} {loaderVersion}",
                });
            }
            return;
        }
        if (s_builtIns.Contains(id))
            return;

        if (!provided.TryGetValue(id, out var provider))
        {
            issues.Add(new ModIssue(ModIssueKind.MissingDependency, ModIssueSeverity.Error, file.FileName, file.DisplayName)
            {
                Other = id,
                Detail = dep.VersionRange,
                Range = dep.VersionRange,
                Loader = mod.Loader,
                ModId = mod.Id,
                OtherId = id,
            });
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
                ModId = mod.Id,
                OtherId = id,
                Found = provider.Version,
            });
        }
    }

    /// <summary>
    /// Dismissed issues (<see cref="ModIssue.Key"/>s) whose problem is gone while both its sides are here and enabled:
    /// it was fixed, so if it comes back it's asked about again. Those waiting for a side that's away (either mod
    /// disabled or removed for now, a copy of a duplicate) stay dismissed.
    /// </summary>
    /// <param name="issues">What <see cref="Check(IReadOnlyList{ContentFile}, InstanceManifest)"/> found in <paramref name="mods"/>.</param>
    public static IReadOnlyList<string> ResolvedDismissals(IReadOnlySet<string> dismissed, IReadOnlyList<ModIssue> issues, IReadOnlyList<ContentFile> mods)
    {
        // Without a loader nothing else is checked, so nothing else is known to be fixed.
        if (dismissed.Count == 0 || issues.Any(i => i.Kind == ModIssueKind.NoLoader))
            return [];
        var current = issues.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var here = new EnabledMods(mods);
        return dismissed.Where(k => !current.Contains(k) && here.AwayParty(k) is null).ToList();
    }

    /// <summary>The subject's mod id in a <see cref="ModIssue.Key"/>; none for <see cref="ModIssueKind.NoLoader"/>.</summary>
    public static string? KeyModId(string key) => key.Split('|') is [_, var id, ..] ? id : null;

    /// <summary>The mod ids a file makes available (with aliases and nested mods), and "optifine" for OptiFine.</summary>
    public static IEnumerable<string> IdsOf(ContentFile file) =>
        file.Metadata.IsOptiFine ? file.Metadata.ProvidedIds.Append("optifine") : file.Metadata.ProvidedIds;

    /// <summary>Forge's own version in mods.toml ranges is the short one ("47.2.0"), never "1.20.1-47.2.0".</summary>
    private static string? ForgeShortVersion(string? version) =>
        version is not null && version.Contains('-', StringComparison.Ordinal) && version.Split('-') is [var mc, var rest, ..] && mc.StartsWith("1.", StringComparison.Ordinal)
            ? rest
            : version;

    /// <summary>The loader <paramref name="loaderUid"/> loads this mod entry.</summary>
    internal static bool Accepts(string loaderUid, string? minecraftVersion, ModInfo mod) => (loaderUid, mod.Loader) switch
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

/// <summary>
/// What an instance has enabled, by mod id (aliases and nested mods too) and by file: where the sides of a dismissed
/// issue's problem are looked for.
/// </summary>
public sealed class EnabledMods
{
    private readonly HashSet<string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Some file has mod metadata: without a loader, it would raise the no-loader warning.</summary>
    private readonly bool _anyMod;

    public EnabledMods(IEnumerable<ContentFile> mods)
    {
        foreach (var file in mods.Where(m => m.Enabled && !m.IsDirectory))
        {
            _files.Add(file.FileName);
            _ids.UnionWith(ModChecker.IdsOf(file));
            _anyMod |= file.Metadata.Mods.Count > 0;
        }
    }

    /// <summary>
    /// The side of a dismissed issue's problem (<see cref="ModIssue.Key"/>) that's away: the mod id of the mod with
    /// the issue, or of the other one where the problem is between two (a conflict, the dependency version found),
    /// or for a duplicate the file name of a copy. Null when they're all here: a problem not found now was fixed.
    /// A no-loader warning waits for "", any mod, while there's none.
    /// </summary>
    public string? AwayParty(string key)
    {
        var parts = key.Split('|');
        if (parts.Length < 2)
            return _anyMod ? null : "";
        if (!_ids.Contains(parts[1]))
            return parts[1];
        if (parts.Length < 3 || !Enum.TryParse<ModIssueKind>(parts[0], ignoreCase: true, out var kind))
            return null;
        return kind switch
        {
            ModIssueKind.Duplicate => parts[2].Split(',').FirstOrDefault(f => !_files.Contains(f)),
            // Not a missing dependency's: its being away is the problem, and its coming is the fix.
            ModIssueKind.Incompatible or ModIssueKind.Discouraged or ModIssueKind.KnownConflict or ModIssueKind.DependencyVersion
                when !_ids.Contains(parts[2]) => parts[2],
            _ => null,
        };
    }
}
