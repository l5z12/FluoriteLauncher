using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fluorite.Modding.Modpacks;

/// <summary>
/// <c>&lt;metadata&gt;/export.json</c>: what an instance was last exported as, so the next export starts from there
/// (pack authors export the same pack again and again). Choices are kept as the paths the user left whole: a folder
/// ticked or unticked as a whole is listed itself, a folder with some of it ticked by what's inside it.
/// </summary>
public sealed class PackExportState
{
    public int FormatVersion { get; set; } = 1;
    public PackFormat Format { get; set; }
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Author { get; set; }
    public string? Summary { get; set; }

    /// <summary>
    /// When the last export finished, and as what. The choices above are kept as soon as Export is pressed (a cancelled
    /// save keeps them too), so they may be newer than what was last exported.
    /// </summary>
    public DateTimeOffset ExportedAt { get; set; }

    public PackFormat? ExportedFormat { get; set; }
    public string? ExportedVersion { get; set; }

    /// <summary>Copy every file in (see <see cref="PackExportOptions.LocalOnly"/>); null: as Settings says.</summary>
    public bool? LocalOnly { get; set; }

    public bool KeepDisabled { get; set; }

#pragma warning disable CA2227 // Serialised.
    /// <summary>Relative paths (<c>/</c>) ticked, each with everything inside it.</summary>
    public List<string> Included { get; set; } = [];

    /// <summary>Relative paths (<c>/</c>) left out, each with everything inside it.</summary>
    public List<string> Excluded { get; set; } = [];
#pragma warning restore CA2227

    public const string FileName = "export.json";

    public static PackExportState? Load(string metadataDirectory)
    {
        var path = Path.Combine(metadataDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllBytes(path), PackExportStateJsonContext.Default.PackExportState) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task SaveAsync(string metadataDirectory, CancellationToken ct = default)
    {
        Directory.CreateDirectory(metadataDirectory);
        var path = Path.Combine(metadataDirectory, FileName);
        var tmp = path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, this, PackExportStateJsonContext.Default.PackExportState, ct).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Whether <paramref name="path"/> was ticked last time: true or false when it or a folder it's in was left whole,
    /// null when nothing says (a new file, or one in a folder that was only partly ticked: that folder's own default).
    /// The innermost mention wins.
    /// </summary>
    public bool? WasIncluded(string path)
    {
        var best = -1;
        bool? result = null;
        foreach (var (list, value) in new[] { (Included, true), (Excluded, false) })
        {
            foreach (var p in list)
            {
                if (p.Length > best && Covers(p, path))
                {
                    best = p.Length;
                    result = value;
                }
            }
        }
        return result;
    }

    /// <summary>Whether something inside <paramref name="folder"/> (not the folder itself) was chosen on its own.</summary>
    public bool HasChoicesInside(string folder) =>
        Included.Concat(Excluded).Any(p => p.Length > folder.Length && Covers(folder, p));

    /// <summary><paramref name="path"/> is <paramref name="ancestor"/> or inside it.</summary>
    private static bool Covers(string ancestor, string path) =>
        path.Equals(ancestor, StringComparison.OrdinalIgnoreCase)
        || (path.Length > ancestor.Length && path[ancestor.Length] == '/' && path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase));
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PackExportState))]
internal sealed partial class PackExportStateJsonContext : JsonSerializerContext;
