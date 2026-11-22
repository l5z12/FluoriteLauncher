using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core.Assets;
using Fluorite.Core.Instances;
using Fluorite.Core.Java;
using Fluorite.Core.Settings;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Json;

/// <summary>
/// Source-generated serializer metadata. Lenient on input (comments, trailing commas, numbers as strings)
/// because version JSONs come from many hand-written and third-party sources.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(VersionManifest))]
[JsonSerializable(typeof(VersionJson))]
[JsonSerializable(typeof(List<Rule>))]
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(Dictionary<string, Dictionary<string, List<MojangRuntimeEntry>>>))]
[JsonSerializable(typeof(MojangRuntimeFileManifest))]
[JsonSerializable(typeof(InstanceManifest))]
[JsonSerializable(typeof(SnapshotManifest))]
[JsonSerializable(typeof(List<ComponentRef>))]
[JsonSerializable(typeof(LauncherSettings))]
[JsonSerializable(typeof(News.PatchNotesFeed))]
[JsonSerializable(typeof(News.PatchNoteContent))]
[JsonSerializable(typeof(News.NewsFeed))]
[JsonSerializable(typeof(Diagnostics.MclogsResponse))]
public sealed partial class FluoriteJsonContext : JsonSerializerContext;
