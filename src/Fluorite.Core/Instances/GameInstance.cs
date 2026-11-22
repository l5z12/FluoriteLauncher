namespace Fluorite.Core.Instances;

public enum LayoutKind
{
    Fluorite,
    Official,
    Isolated,
    MultiMC,
}

/// <summary>
/// A launchable instance from any layout, normalised. Foreign layouts map their own formats onto this.
/// </summary>
public sealed class GameInstance
{
    /// <summary>
    /// Globally unique id: the folder name for Fluorite instances, <c>&lt;rootId&gt;:&lt;localId&gt;</c> for instances of
    /// another launcher's folder (see <see cref="RootId"/>, <see cref="LocalId"/>). Used in shortcuts and <c>fluorite://</c> links.
    /// </summary>
    public required string Id { get; init; }

    public required LayoutKind Layout { get; init; }

    /// <summary>The instance's own folder (holds metadata; equals <see cref="GameDirectory"/> for some layouts).</summary>
    public required string Directory { get; init; }

    /// <summary>Directory passed as <c>--gameDir</c>.</summary>
    public required string GameDirectory { get; init; }

    /// <summary>The shared store whose versions/libraries/assets this instance uses.</summary>
    public required GameDirectories Store { get; init; }

    public required InstanceManifest Manifest { get; init; }

    /// <summary>Version id to launch from <see cref="Store"/> (after components are installed).</summary>
    public required string LaunchVersionId { get; init; }

    /// <summary>
    /// Where Fluorite keeps its own data about the instance (content index, natives, modpack state). Inside the instance
    /// for Fluorite instances; a sidecar folder under Fluorite's data root for other launchers' instances, so their
    /// folders only ever change the way the game itself changes them.
    /// </summary>
    public required string MetadataDirectory { get; init; }

    public string NativesDirectory => Path.Combine(MetadataDirectory, "natives");

    /// <summary>
    /// Holds the content index (hash cache + provenance) of <see cref="GameDirectory"/>. Instances that share a game
    /// directory (official launcher profiles) share it.
    /// </summary>
    public string ContentDirectory
    {
        get => field ?? MetadataDirectory;
        init;
    }

    /// <summary>The external root this instance belongs to; null for Fluorite's own instances.</summary>
    public string? RootId { get; init; }

    /// <summary>Id within the layout (folder name, profile key, version id).</summary>
    public string LocalId { get; init; } = "";

    /// <summary>Which launcher's folder this came from (for display); null for Fluorite instances.</summary>
    public OtherLauncher? Launcher { get; init; }

    public string Name => Manifest.Name;

    /// <summary>Whether Fluorite may change the component stack (Minecraft/loader versions) of this instance.</summary>
    public bool CanEditComponents { get; init; } = true;

    /// <summary>Things Fluorite can't reproduce exactly (jar mods, custom patches…), shown before launching.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsExternal => RootId is not null;
}
