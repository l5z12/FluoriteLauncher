namespace Fluorite.Core.Versions;

/// <summary><c>group:artifact:version[:classifier][@extension]</c>.</summary>
public readonly record struct MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate Parse(string name)
    {
        if (!TryParse(name, out var c))
            throw new FormatException($"Invalid maven coordinate '{name}'.");
        return c;
    }

    public static bool TryParse(string? name, out MavenCoordinate coordinate)
    {
        coordinate = default;
        if (string.IsNullOrWhiteSpace(name))
            return false;

        var ext = "jar";
        var at = name.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            ext = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3 || parts.Length > 5)
            return false;

        string? classifier = null;
        string version;
        if (parts.Length == 5)
        {
            // group:artifact:extension:classifier:version (rare, Gradle-style)
            ext = parts[2];
            classifier = parts[3];
            version = parts[4];
        }
        else
        {
            version = parts[2];
            if (parts.Length == 4)
                classifier = parts[3];
        }

        if (parts[0].Length == 0 || parts[1].Length == 0 || version.Length == 0)
            return false;

        coordinate = new MavenCoordinate(parts[0], parts[1], version, string.IsNullOrEmpty(classifier) ? null : classifier, ext);
        return true;
    }

    public MavenCoordinate WithClassifier(string? classifier) => this with { Classifier = classifier };

    /// <summary>Relative path inside a maven repository, always with forward slashes.</summary>
    public string RelativePath
    {
        get
        {
            var file = Classifier is null
                ? $"{Artifact}-{Version}.{Extension}"
                : $"{Artifact}-{Version}-{Classifier}.{Extension}";
            return $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{file}";
        }
    }

    /// <summary>Key used to de-duplicate libraries across patches (version-independent).</summary>
    public string DedupeKey => Classifier is null ? $"{Group}:{Artifact}" : $"{Group}:{Artifact}:{Classifier}";

    public override string ToString() =>
        (Classifier is null ? $"{Group}:{Artifact}:{Version}" : $"{Group}:{Artifact}:{Version}:{Classifier}")
        + (Extension == "jar" ? "" : "@" + Extension);
}
