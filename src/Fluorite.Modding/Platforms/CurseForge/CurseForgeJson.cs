using System.Text.Json.Serialization;

namespace Fluorite.Modding.Platforms.CurseForge;

#pragma warning disable CA1056, CA1002, CA2227 // DTOs mirror the API.

public sealed class CfResponse<T>
{
    public T? Data { get; set; }
    public CfPagination? Pagination { get; set; }
}

public sealed class CfPagination
{
    public int Index { get; set; }
    public int PageSize { get; set; }
    public int ResultCount { get; set; }
    public int TotalCount { get; set; }
}

public sealed class CfMod
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Slug { get; set; } = "";
    public CfLinks? Links { get; set; }
    public string? Summary { get; set; }
    public long DownloadCount { get; set; }
    public List<CfCategory>? Categories { get; set; }
    public int ClassId { get; set; }
    public List<CfAuthor>? Authors { get; set; }
    public CfLogo? Logo { get; set; }
    public List<CfLogo>? Screenshots { get; set; }
    public List<CfFileIndex>? LatestFilesIndexes { get; set; }
    public DateTimeOffset? DateModified { get; set; }
    public bool? AllowModDistribution { get; set; }
    public long ThumbsUpCount { get; set; }
}

public sealed class CfLinks
{
    public string? WebsiteUrl { get; set; }
    public string? WikiUrl { get; set; }
    public string? IssuesUrl { get; set; }
    public string? SourceUrl { get; set; }
}

public sealed class CfAuthor
{
    public string Name { get; set; } = "";
}

public sealed class CfLogo
{
    public string? ThumbnailUrl { get; set; }
    public string? Url { get; set; }
}

public sealed class CfCategory
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string? Slug { get; set; }
    public string? IconUrl { get; set; }
    public int? ClassId { get; set; }
    public long? ParentCategoryId { get; set; }
    public bool? IsClass { get; set; }
}

public sealed class CfFileIndex
{
    public string GameVersion { get; set; } = "";
    public long FileId { get; set; }
    public string? Filename { get; set; }
    public int ReleaseType { get; set; }
    public int? ModLoader { get; set; }
}

public sealed class CfFile
{
    public long Id { get; set; }
    public long ModId { get; set; }
    public string DisplayName { get; set; } = "";
    public string FileName { get; set; } = "";
    public int ReleaseType { get; set; }
    public List<CfHash>? Hashes { get; set; }
    public DateTimeOffset FileDate { get; set; }
    public long FileLength { get; set; }
    public long DownloadCount { get; set; }
    public string? DownloadUrl { get; set; }
    public List<string>? GameVersions { get; set; }
    public List<CfFileDependency>? Dependencies { get; set; }
    public long FileFingerprint { get; set; }
    public bool? IsServerPack { get; set; }
}

public sealed class CfHash
{
    public string Value { get; set; } = "";
    public int Algo { get; set; }
}

public sealed class CfFileDependency
{
    public long ModId { get; set; }
    public int RelationType { get; set; }
}

public sealed class CfFingerprintMatches
{
    public List<CfFingerprintMatch>? ExactMatches { get; set; }
}

public sealed class CfFingerprintMatch
{
    public long Id { get; set; }
    public CfFile? File { get; set; }
}

public sealed class CfFingerprintsRequest
{
    public List<uint> Fingerprints { get; set; } = [];
}

public sealed class CfModsRequest
{
    public List<long> ModIds { get; set; } = [];
    public bool FilterPcOnly { get; set; } = true;
}

public sealed class CfFilesRequest
{
    public List<long> FileIds { get; set; } = [];
}

#pragma warning restore CA1056, CA1002, CA2227

/// <summary>A file resolved by id (see <see cref="CurseForgeClient.GetFilesAsync"/>).</summary>
/// <param name="ClassId">6 mods, 12 resource packs, 6552 shader packs, 6945 data packs, 17 worlds.</param>
public sealed record CurseForgeFileInfo(
    long ProjectId, long FileId, string FileName, string? Url, string? Sha1, long Size, int ClassId, string? ProjectName,
    string DisplayName, string? ManualDownloadUrl)
{
    public string? IconUrl { get; init; }
    public string? WebUrl { get; init; }

    public string Folder => ClassId switch
    {
        12 => "resourcepacks",
        6552 => "shaderpacks",
        6945 => "datapacks",
        17 => "saves",
        _ => "mods",
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CfResponse<List<CfMod>>))]
[JsonSerializable(typeof(CfResponse<CfMod>))]
[JsonSerializable(typeof(CfResponse<List<CfFile>>))]
[JsonSerializable(typeof(CfResponse<CfFile>))]
[JsonSerializable(typeof(CfResponse<string>))]
[JsonSerializable(typeof(CfResponse<List<CfCategory>>))]
[JsonSerializable(typeof(CfResponse<CfFingerprintMatches>))]
[JsonSerializable(typeof(CfFingerprintsRequest))]
[JsonSerializable(typeof(CfModsRequest))]
[JsonSerializable(typeof(CfFilesRequest))]
internal sealed partial class CurseForgeJsonContext : JsonSerializerContext;
