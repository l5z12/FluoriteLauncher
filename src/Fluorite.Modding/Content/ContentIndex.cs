using System.Text.Json;
using System.Text.Json.Serialization;
using Fluorite.Core;
using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Content;

/// <summary>A file's identity on a content platform.</summary>
public sealed class RemoteRef
{
    public ContentPlatform Platform { get; set; }
    public string ProjectId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string? VersionNumber { get; set; }
    public DateTimeOffset? Published { get; set; }

    /// <summary>Release channel of the installed version; updates never move to a less stable one.</summary>
    public ReleaseChannel Channel { get; set; }
}

/// <summary>What Fluorite knows about one content file (keyed by path without <c>.disabled</c>).</summary>
public sealed class ContentRecord
{
    public long Size { get; set; }
    public DateTime ModifiedUtc { get; set; }
    public string? Sha1 { get; set; }
    public string? Sha512 { get; set; }
    public uint? Murmur2 { get; set; }

    /// <summary>Where the file came from (installer) or what a hash lookup matched; drives updates.</summary>
    public RemoteRef? Source { get; set; }

    public string? Title { get; set; }
    public string? IconUrl { get; set; }
    public string? ProjectUrl { get; set; }

    /// <summary>Don't offer updates.</summary>
    public bool Pinned { get; set; }

    /// <summary>Installed automatically as a dependency of another project.</summary>
    public bool IsDependency { get; set; }

    /// <summary>Last time a hash lookup was attempted (unidentified files are retried after a day).</summary>
    public DateTimeOffset? IdentifiedAt { get; set; }

    /// <summary>
    /// Its manifest was edited in Fluorite: the jar's hash before that. The original is kept under this name in
    /// <see cref="ContentManager.OriginalsDirectory"/>, to put back.
    /// </summary>
    public string? OriginalSha1 { get; set; }
}

/// <summary><c>&lt;instance&gt;/.fluorite/content.json</c>: hash cache + provenance for every content file.</summary>
public sealed class ContentIndex
{
    public int FormatVersion { get; set; } = 1;

#pragma warning disable CA2227 // Serialised.
    public Dictionary<string, ContentRecord> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Mod issues the user dismissed (<see cref="ModIssue.Key"/>s), by instance id: instances sharing a game directory
    /// (official launcher profiles) share this index, but not their dismissals. Kept while their mods are away
    /// (disabled, say), so they stay dismissed when they come back.
    /// </summary>
    public Dictionary<string, HashSet<string>>? DismissedIssues { get; set; }
#pragma warning restore CA2227

    public static async Task<ContentIndex> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new ContentIndex();
        try
        {
            await using var fs = File.OpenRead(path);
            var index = await JsonSerializer.DeserializeAsync(fs, ContentIndexJsonContext.Default.ContentIndex, ct).ConfigureAwait(false) ?? new ContentIndex();
            index.Files = new Dictionary<string, ContentRecord>(index.Files, StringComparer.OrdinalIgnoreCase);
            if (index.DismissedIssues is { } dismissed)
                index.DismissedIssues = new Dictionary<string, HashSet<string>>(dismissed, StringComparer.OrdinalIgnoreCase);
            return index;
        }
        catch (JsonException)
        {
            return new ContentIndex(); // Only a cache + provenance; rebuilt by hashing.
        }
    }

    public async Task SaveAsync(string path, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFile.WriteAsync(path, async tmp =>
        {
            await using var fs = File.Create(tmp);
            await JsonSerializer.SerializeAsync(fs, this, ContentIndexJsonContext.Default.ContentIndex, ct).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ContentIndex))]
internal sealed partial class ContentIndexJsonContext : JsonSerializerContext;
