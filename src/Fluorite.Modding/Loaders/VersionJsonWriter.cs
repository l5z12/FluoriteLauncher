using System.Text.Json;
using System.Text.Json.Nodes;
using Fluorite.Core;
using Fluorite.Core.Json;
using Fluorite.Core.Versions;

namespace Fluorite.Modding.Loaders;

/// <summary>Writes loader profile JSONs into <c>versions/&lt;id&gt;/&lt;id&gt;.json</c> (official-launcher compatible).</summary>
internal static class VersionJsonWriter
{
    /// <summary>Writes upstream JSON verbatim (keeps fields we don't model), optionally patching top-level properties.</summary>
    public static async Task<string> WriteRawAsync(GameDirectories dirs, string json, Action<JsonObject>? patch, CancellationToken ct)
    {
        var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject()
            ?? throw new InvalidDataException("Loader profile is not a JSON object.");
        patch?.Invoke(node);
        var id = node["id"]?.GetValue<string>() ?? throw new InvalidDataException("Loader profile has no id.");
        var path = dirs.VersionJson(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), ct).ConfigureAwait(false);
        return id;
    }

    public static async Task<string> WriteAsync(GameDirectories dirs, VersionJson version, CancellationToken ct)
    {
        var path = dirs.VersionJson(version.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(version, FluoriteJsonContext.Default.VersionJson), ct).ConfigureAwait(false);
        return version.Id;
    }
}
