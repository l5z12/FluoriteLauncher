using System.Text.Json.Serialization;

namespace Fluorite.Core.Assets;

public sealed class AssetIndex
{
    public Dictionary<string, AssetObject> Objects { get; set; } = [];

    /// <summary>1.6–1.7.2 "legacy" index: objects must also be copied to <c>assets/virtual/&lt;id&gt;</c>.</summary>
    public bool Virtual { get; set; }

    /// <summary>Pre-1.6: objects must be copied to <c>&lt;gameDir&gt;/resources</c>.</summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}
