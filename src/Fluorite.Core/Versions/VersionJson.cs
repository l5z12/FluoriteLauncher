using System.Globalization;
using System.Text.Json.Serialization;

namespace Fluorite.Core.Versions;

/// <summary>
/// A Minecraft version JSON (vanilla, or a loader profile that uses <see cref="InheritsFrom"/>).
/// Modelled loosely so that JSONs from Mojang, Forge, NeoForge, Fabric, Quilt, OptiFine, LiteLoader,
/// HMCL/PCL and hand-edited files all deserialize.
/// </summary>
public sealed class VersionJson
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? Type { get; set; }
    public string? MainClass { get; set; }

    /// <summary>Legacy (pre-1.13) space-separated game arguments.</summary>
    public string? MinecraftArguments { get; set; }

    /// <summary>Modern (1.13+) structured arguments.</summary>
    public VersionArguments? Arguments { get; set; }

    public AssetIndexReference? AssetIndex { get; set; }
    public string? Assets { get; set; }
    public int? ComplianceLevel { get; set; }
    public Dictionary<string, DownloadArtifact>? Downloads { get; set; }
    public JavaVersionInfo? JavaVersion { get; set; }
    public List<Library>? Libraries { get; set; }
    public Dictionary<string, LoggingConfig>? Logging { get; set; }
    public string? ReleaseTime { get; set; }
    public string? Time { get; set; }
    public int? MinimumLauncherVersion { get; set; }

    /// <summary>Some loader JSONs name the version whose jar should be used.</summary>
    public string? Jar { get; set; }

    [JsonIgnore]
    public DateTimeOffset? ParsedReleaseTime => LenientDate.Parse(ReleaseTime) ?? LenientDate.Parse(Time);
}

public sealed class VersionArguments
{
    public List<ArgumentEntry>? Game { get; set; }
    public List<ArgumentEntry>? Jvm { get; set; }
}

/// <summary>Either a plain string or <c>{ "rules": [...], "value": "x" | ["x","y"] }</c>.</summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public ArgumentEntry(IReadOnlyList<string> values, List<Rule>? rules = null)
    {
        Values = values;
        Rules = rules;
    }

    public IReadOnlyList<string> Values { get; }
    public List<Rule>? Rules { get; }

    public static implicit operator ArgumentEntry(string value) => new([value]);
}

public sealed class AssetIndexReference
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public long? TotalSize { get; set; }
    public string? Url { get; set; }
}

public sealed class DownloadArtifact
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public string? Url { get; set; }
}

public sealed class JavaVersionInfo
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingConfig
{
    public string? Argument { get; set; }
    public LoggingFile? File { get; set; }
    public string? Type { get; set; }
}

public sealed class LoggingFile
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public string? Url { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    public List<Rule>? Rules { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public ExtractRules? Extract { get; set; }

    /// <summary>Maven repository base (Fabric/Quilt/legacy Forge style libraries without <see cref="Downloads"/>).</summary>
    public string? Url { get; set; }

    public string? Sha1 { get; set; }
    public long? Size { get; set; }

    /// <summary>Legacy Forge: list of acceptable SHA-1s.</summary>
    public List<string>? Checksums { get; set; }

    public bool? Clientreq { get; set; }
    public bool? Serverreq { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadArtifact? Artifact { get; set; }
    public Dictionary<string, DownloadArtifact>? Classifiers { get; set; }
}

public sealed class ExtractRules
{
    public List<string>? Exclude { get; set; }
}

internal static class LenientDate
{
    private static readonly string[] s_formats =
    [
        "yyyy-MM-dd'T'HH:mm:ssK",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:sszz00",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzz00",
    ];

    public static DateTimeOffset? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return null;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d))
            return d;
        // "+0000" style offsets without a colon.
        if (s.Length > 5 && (s[^5] == '+' || s[^5] == '-') && char.IsDigit(s[^1]))
        {
            var fixedUp = s[..^2] + ":" + s[^2..];
            if (DateTimeOffset.TryParse(fixedUp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d))
                return d;
        }
        return DateTimeOffset.TryParseExact(s, s_formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out d) ? d : null;
    }
}
