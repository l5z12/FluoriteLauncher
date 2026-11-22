using System.Text.Json.Serialization;

namespace Fluorite.Core.Versions;

/// <summary>piston-meta <c>version_manifest_v2.json</c>.</summary>
public sealed class VersionManifest
{
    public LatestVersions? Latest { get; set; }
    public List<VersionManifestEntry> Versions { get; set; } = [];
}

public sealed class LatestVersions
{
    public string? Release { get; set; }
    public string? Snapshot { get; set; }
}

public sealed class VersionManifestEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public string? Time { get; set; }
    public string? ReleaseTime { get; set; }
    public string? Sha1 { get; set; }
    public int? ComplianceLevel { get; set; }

    [JsonIgnore]
    public DateTimeOffset? ParsedReleaseTime => LenientDate.Parse(ReleaseTime);

    /// <summary>Used by screen readers/UI Automation for list items.</summary>
    public override string ToString() => Id;
}

/// <summary>Version channel as published by Mojang.</summary>
public static class VersionTypes
{
    public const string Release = "release";
    public const string Snapshot = "snapshot";
    public const string OldBeta = "old_beta";
    public const string OldAlpha = "old_alpha";
}
