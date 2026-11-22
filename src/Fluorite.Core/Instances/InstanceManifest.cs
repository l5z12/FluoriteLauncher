namespace Fluorite.Core.Instances;

/// <summary>Well-known component UIDs (Prism/MultiMC compatible so <c>mmc-pack.json</c> maps 1:1).</summary>
public static class ComponentUids
{
    public const string Minecraft = "net.minecraft";
    public const string Lwjgl2 = "org.lwjgl";
    public const string Lwjgl3 = "org.lwjgl3";
    public const string FabricLoader = "net.fabricmc.fabric-loader";
    public const string QuiltLoader = "org.quiltmc.quilt-loader";
    public const string Forge = "net.minecraftforge";
    public const string NeoForge = "net.neoforged";
    public const string LiteLoader = "com.mumfrey.liteloader";
    public const string OptiFine = "optifine";
    public const string LegacyFabric = "net.legacyfabric.fabric-loader";

    /// <summary>A raw version JSON already present in <c>versions/</c> (installed by another tool or launcher).</summary>
    public const string RawVersion = "fluorite.raw-version";
}

public sealed class ComponentRef
{
    public string Uid { get; set; } = "";
    public string Version { get; set; } = "";

    /// <summary>
    /// Id of the version JSON the loader installer produced in <c>versions/</c>. Installers (Forge especially) choose
    /// their own ids, so this records the truth after installation; null until installed.
    /// </summary>
    public string? ProfileId { get; set; }

    /// <summary>Pinned by the user; managed-pack updates won't change it.</summary>
    public bool Pinned { get; set; }
}

/// <summary>Per-instance overrides; null means "inherit from group/global".</summary>
public sealed class InstanceSettings
{
    public string? JavaPath { get; set; }
    public int? MaxMemoryMb { get; set; }
    public int? MinMemoryMb { get; set; }
    public string? JvmArguments { get; set; }
    public string? GameArguments { get; set; }
    public Dictionary<string, string>? Environment { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? Fullscreen { get; set; }
    public string? PreLaunchCommand { get; set; }
    public string? PostExitCommand { get; set; }
    public string? WrapperCommand { get; set; }
    public string? DefaultAccountId { get; set; }

    /// <summary>On an ARM64 PC; null means automatic (native where possible).</summary>
    public Libraries.Arm64Mode? Arm64Mode { get; set; }
}

public sealed class InstanceStats
{
    public DateTimeOffset? LastPlayed { get; set; }
    public long TotalPlaySeconds { get; set; }
    public long LastSessionSeconds { get; set; }
    public int LaunchCount { get; set; }
}

/// <summary><c>instance.json</c> of a Fluorite-native instance.</summary>
public sealed class InstanceManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "";
    public string? Group { get; set; }
    public string? Icon { get; set; }

    /// <summary>
    /// A Prism built-in icon's name (<see cref="PrismIcons.BuiltIn"/>, e.g. <c>grass</c>) for an instance with no icon
    /// file: kept from Prism instances and their copies so they can show it, and written back on export.
    /// </summary>
    public string? IconKey { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public List<ComponentRef> Components { get; set; } = [];
    public InstanceSettings Settings { get; set; } = new();
    public InstanceStats Stats { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string? MinecraftVersion => Components.FirstOrDefault(c => c.Uid == ComponentUids.Minecraft)?.Version;
}
