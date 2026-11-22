namespace Fluorite.Modding.Content;

/// <summary>Kinds of installable content. Each maps to a folder in the game directory.</summary>
public enum ContentKind
{
    Mod,
    ResourcePack,
    ShaderPack,
    DataPack,
    Modpack,
}

/// <summary>Where a piece of content lives inside an instance's game directory.</summary>
/// <param name="World">For data packs: the world folder name under <c>saves/</c>.</param>
public sealed record ContentLocation(ContentKind Kind, string? World = null)
{
    public const string DisabledSuffix = ".disabled";

    /// <summary>Folder relative to the game directory, with forward slashes.</summary>
    public string RelativeFolder => Kind switch
    {
        ContentKind.Mod => "mods",
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.ShaderPack => "shaderpacks",
        ContentKind.DataPack => World is { Length: > 0 } w ? $"saves/{w}/datapacks" : throw new InvalidOperationException("Data packs need a world."),
        _ => throw new NotSupportedException($"{Kind} is not stored in an instance folder."),
    };

    public string FullPath(string gameDirectory) => Path.Combine(gameDirectory, RelativeFolder.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>File extensions recognised in this folder (folders are accepted for packs too).</summary>
    public IReadOnlyList<string> Extensions => Kind switch
    {
        ContentKind.Mod => [".jar", ".zip", ".litemod"],
        _ => [".zip"],
    };

    public bool AcceptsDirectories => Kind is ContentKind.ResourcePack or ContentKind.ShaderPack or ContentKind.DataPack;

    public bool Accepts(string fileName)
    {
        var name = StripDisabled(fileName);
        return Extensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));
    }

    public static string StripDisabled(string name) =>
        name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^DisabledSuffix.Length] : name;

    public static bool IsDisabled(string name) => name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
}
