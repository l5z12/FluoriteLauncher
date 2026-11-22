using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluorite.Modding.Json;

public sealed class FabricLoaderEntry
{
    public FabricLoaderInfo? Loader { get; set; }
}

public sealed class FabricLoaderInfo
{
    public string Version { get; set; } = "";
    public bool? Stable { get; set; }
    public string? Maven { get; set; }
}

/// <summary>Fabric/Quilt/Legacy Fabric <c>versions/game</c> entry.</summary>
public sealed class FabricGameEntry
{
    public string Version { get; set; } = "";
}

public sealed class ForgePromotions
{
    public Dictionary<string, string> Promos { get; set; } = [];
}

/// <summary>BMCLAPI <c>/optifine/{mc}</c> entry.</summary>
public sealed class OptiFineEntry
{
    public string Mcversion { get; set; } = "";
    public string Type { get; set; } = "";
    public string Patch { get; set; } = "";
    public string? Filename { get; set; }
    public string? Forge { get; set; }

    [JsonIgnore]
    public string FullVersion => $"{Type}_{Patch}";

    [JsonIgnore]
    public bool IsPreview => Patch.Contains("pre", StringComparison.OrdinalIgnoreCase) || (Filename?.StartsWith("preview", StringComparison.OrdinalIgnoreCase) ?? false);
}

/// <summary>LiteLoader <c>versions.json</c>.</summary>
public sealed class LiteLoaderManifest
{
    public Dictionary<string, LiteLoaderMinecraftEntry> Versions { get; set; } = [];
}

/// <summary>
/// <c>artefacts</c>/<c>snapshots</c> mix artifact maps (<c>"com.mumfrey:liteloader": {build…}</c>) with a shared
/// <c>libraries</c> array, so they're kept as raw JSON and read by <see cref="Loaders.LiteLoaderProvider"/>.
/// </summary>
public sealed class LiteLoaderMinecraftEntry
{
    public JsonElement? Artefacts { get; set; }
    public JsonElement? Snapshots { get; set; }
}

public sealed class LiteLoaderBuild
{
    public string? TweakClass { get; set; }
    public List<LiteLoaderLibrary>? Libraries { get; set; }
    public string? Stream { get; set; }
    public string? File { get; set; }
    public string? Version { get; set; }
    public string? Build { get; set; }
    public string? Md5 { get; set; }
}

public sealed class LiteLoaderLibrary
{
    public string Name { get; set; } = "";
    public string? Url { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<FabricLoaderEntry>))]
[JsonSerializable(typeof(List<FabricGameEntry>))]
[JsonSerializable(typeof(ForgePromotions))]
[JsonSerializable(typeof(List<OptiFineEntry>))]
[JsonSerializable(typeof(LiteLoaderManifest))]
[JsonSerializable(typeof(LiteLoaderBuild))]
[JsonSerializable(typeof(List<LiteLoaderLibrary>))]
internal sealed partial class ModdingJsonContext : JsonSerializerContext;
