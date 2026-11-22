namespace Fluorite.Modding.Metadata;

/// <summary>Which loader a mod's metadata targets.</summary>
public enum ModLoaderKind
{
    Fabric,
    Quilt,

    /// <summary><c>META-INF/mods.toml</c> (Forge 1.13+, also early NeoForge).</summary>
    Forge,

    /// <summary><c>META-INF/neoforge.mods.toml</c>, or a mods.toml that depends on <c>neoforge</c>.</summary>
    NeoForge,

    /// <summary><c>mcmod.info</c> (Forge 1.12.2 and older).</summary>
    LegacyForge,
    LiteLoader,
}

public enum DependencyKind
{
    Required,
    Optional,

    /// <summary>Must not be present (Fabric <c>breaks</c>, Forge <c>incompatible</c>).</summary>
    Incompatible,

    /// <summary>Works but is known to cause issues (Fabric <c>conflicts</c>, NeoForge <c>discouraged</c>).</summary>
    Discouraged,
}

/// <param name="VersionRange">Raw predicate in the loader's syntax (Fabric semver predicate or Maven range); null = any.</param>
public sealed record ModDependency(string Id, DependencyKind Kind, string? VersionRange);

public sealed record ModInfo(
    string Id,
    string? Name,
    string? Version,
    ModLoaderKind Loader,
    IReadOnlyList<ModDependency> Dependencies,
    IReadOnlyList<string> Provides)
{
    public string? Description { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>Fabric/Quilt <c>environment</c>: <c>client</c>, <c>server</c> or <c>*</c>.</summary>
    public string? Environment { get; init; }

    /// <summary>Where to find the mod and its authors, in the order the mod lists them.</summary>
    public IReadOnlyList<ModLink> Links { get; init; } = [];

    /// <summary>"MIT", "LGPL-3.0-only, CC-BY-4.0"…</summary>
    public string? License { get; init; }
}

/// <summary>
/// One of a mod's own links: Fabric/Quilt <c>contact</c> entries, Forge's <c>displayURL</c> and <c>issueTrackerURL</c>,
/// mcmod.info's <c>url</c>.
/// </summary>
/// <param name="Kind">
/// The manifest's key in lower case: <see cref="Homepage"/>, <see cref="Sources"/>, <see cref="Issues"/>, <c>email</c>,
/// <c>irc</c>, <c>discord</c>… (Fabric and Quilt allow any key).
/// </param>
/// <param name="Value">As written: usually a URL; an address for <c>email</c>; <c>irc://…</c>.</param>
public sealed record ModLink(string Kind, string Value)
{
    public const string Homepage = "homepage";
    public const string Sources = "sources";
    public const string Issues = "issues";
}

/// <summary>What a content file declares about itself. Every field is best-effort.</summary>
public sealed class ContentMetadata
{
    public static ContentMetadata Empty { get; } = new();

    /// <summary>Top-level mods declared by the file (several for multi-loader jars or multi-mod tomls).</summary>
    public IReadOnlyList<ModInfo> Mods { get; init; } = [];

    /// <summary>Ids provided by nested (jar-in-jar) jars, recursively.</summary>
    public IReadOnlyList<ModInfo> Nested { get; init; } = [];

    public string? Name { get; init; }
    public string? Version { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string> Authors { get; init; } = [];

    /// <summary>Archive entry holding the icon (mod logo or <c>pack.png</c>).</summary>
    public string? IconEntry { get; init; }

    /// <summary>From <c>pack.mcmeta</c> (resource and data packs).</summary>
    public int? PackFormat { get; init; }

    /// <summary>The archive contains a <c>shaders/</c> folder.</summary>
    public bool IsShaderPack { get; init; }

    /// <summary>OptiFine itself (it has no mod metadata).</summary>
    public bool IsOptiFine { get; init; }

    public IEnumerable<ModLoaderKind> Loaders => Mods.Select(m => m.Loader).Distinct();

    /// <summary>Every id this file makes available: mod ids, <c>provides</c> aliases and nested jars.</summary>
    public IEnumerable<string> ProvidedIds =>
        Mods.Concat(Nested).SelectMany(m => m.Provides.Prepend(m.Id));
}
