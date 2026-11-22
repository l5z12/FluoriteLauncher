using System.Text.Json.Serialization;

namespace Fluorite.Modding.Platforms.Modrinth;

#pragma warning disable CA1056, CA1002, CA2227 // DTOs mirror the API.

public sealed class MrSearchResponse
{
    public List<MrSearchHit> Hits { get; set; } = [];
    public int Offset { get; set; }
    public int Limit { get; set; }
    public int TotalHits { get; set; }
}

public sealed class MrSearchHit
{
    public string ProjectId { get; set; } = "";
    public string? ProjectType { get; set; }
    public string Slug { get; set; } = "";
    public string? Author { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public List<string>? DisplayCategories { get; set; }
    public List<string>? Categories { get; set; }
    public long Downloads { get; set; }
    public long Follows { get; set; }
    public string? IconUrl { get; set; }
    public DateTimeOffset? DateModified { get; set; }
}

public sealed class MrProject
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string? ProjectType { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public string? Body { get; set; }
    public DateTimeOffset? Updated { get; set; }
    public long Downloads { get; set; }
    public long Followers { get; set; }
    public List<string>? Categories { get; set; }
    public string? IconUrl { get; set; }
    public string? IssuesUrl { get; set; }
    public string? SourceUrl { get; set; }
    public string? WikiUrl { get; set; }
    public List<MrGalleryImage>? Gallery { get; set; }
}

public sealed class MrGalleryImage
{
    public string Url { get; set; } = "";
    public bool Featured { get; set; }
}

public sealed class MrVersion
{
    public string Id { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string? Changelog { get; set; }
    public DateTimeOffset DatePublished { get; set; }
    public long Downloads { get; set; }
    public string? VersionType { get; set; }
    public List<MrFile> Files { get; set; } = [];
    public List<MrDependency>? Dependencies { get; set; }
    public List<string>? GameVersions { get; set; }
    public List<string>? Loaders { get; set; }
}

public sealed class MrFile
{
    public Dictionary<string, string>? Hashes { get; set; }
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public bool Primary { get; set; }
    public long Size { get; set; }
}

public sealed class MrDependency
{
    public string? VersionId { get; set; }
    public string? ProjectId { get; set; }
    public string? FileName { get; set; }
    public string? DependencyType { get; set; }
}

public sealed class MrCategory
{
    public string Name { get; set; } = "";
    public string? ProjectType { get; set; }
    public string? Header { get; set; }
}

public sealed class MrHashesRequest
{
    public List<string> Hashes { get; set; } = [];
    public string Algorithm { get; set; } = "sha1";
}

public sealed class MrUpdateRequest
{
    public List<string> Hashes { get; set; } = [];
    public string Algorithm { get; set; } = "sha1";
    public List<string>? Loaders { get; set; }
    public List<string>? GameVersions { get; set; }
}

#pragma warning restore CA1056, CA1002, CA2227

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(MrSearchResponse))]
[JsonSerializable(typeof(MrProject))]
[JsonSerializable(typeof(List<MrProject>))]
[JsonSerializable(typeof(MrVersion))]
[JsonSerializable(typeof(List<MrVersion>))]
[JsonSerializable(typeof(List<MrCategory>))]
[JsonSerializable(typeof(Dictionary<string, MrVersion>))]
[JsonSerializable(typeof(MrHashesRequest))]
[JsonSerializable(typeof(MrUpdateRequest))]
internal sealed partial class ModrinthJsonContext : JsonSerializerContext;
