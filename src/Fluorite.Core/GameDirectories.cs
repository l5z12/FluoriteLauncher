namespace Fluorite.Core;

/// <summary>
/// An official-launcher-compatible shared store (<c>versions/</c>, <c>libraries/</c>, <c>assets/</c>, <c>runtime/</c>).
/// Fluorite's own data root uses the same layout so official/HMCL/PCL launchers can reuse it, and the same type
/// points at a foreign <c>.minecraft</c> when launching another launcher's versions in place.
/// </summary>
public sealed class GameDirectories(string root)
{
    public string Root { get; } = Path.GetFullPath(root);
    public string Versions => Path.Combine(Root, "versions");
    public string Libraries => Path.Combine(Root, "libraries");
    public string Assets => Path.Combine(Root, "assets");
    public string AssetIndexes => Path.Combine(Assets, "indexes");
    public string AssetObjects => Path.Combine(Assets, "objects");
    public string LogConfigs => Path.Combine(Assets, "log_configs");
    public string Runtime => Path.Combine(Root, "runtime");

    public string VersionDir(string id) => Path.Combine(Versions, id);
    public string VersionJson(string id) => Path.Combine(Versions, id, id + ".json");
    public string VersionJar(string id) => Path.Combine(Versions, id, id + ".jar");

    public string LibraryPath(string relativeMavenPath) =>
        Path.Combine(Libraries, relativeMavenPath.Replace('/', Path.DirectorySeparatorChar));

    public string AssetObject(string hash) => Path.Combine(AssetObjects, hash[..2], hash);
}
