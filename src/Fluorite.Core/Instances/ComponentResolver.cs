namespace Fluorite.Core.Instances;

/// <summary>
/// Maps a component stack to the version id to launch. Vanilla launches the Minecraft id; with a mod loader the
/// launched id is the loader's profile JSON in <c>versions/</c> (which inherits from vanilla).
/// </summary>
public static class ComponentResolver
{
    /// <summary>Loaders that provide the launched profile, in precedence order. OptiFine is primary only when alone.</summary>
    public static readonly string[] PrimaryLoaders =
    [
        ComponentUids.Forge, ComponentUids.NeoForge, ComponentUids.FabricLoader, ComponentUids.QuiltLoader,
        ComponentUids.LegacyFabric, ComponentUids.LiteLoader, ComponentUids.OptiFine,
    ];

    public static ComponentRef? PrimaryLoader(InstanceManifest manifest)
    {
        foreach (var uid in PrimaryLoaders)
        {
            if (manifest.Components.FirstOrDefault(c => c.Uid == uid) is { } c)
                return c;
        }
        return null;
    }

    /// <summary>OptiFine installed as a Forge mod (when Forge is the primary loader).</summary>
    public static ComponentRef? OptiFineAsMod(InstanceManifest manifest) =>
        PrimaryLoader(manifest)?.Uid is ComponentUids.Forge
            ? manifest.Components.FirstOrDefault(c => c.Uid == ComponentUids.OptiFine)
            : null;

    public static string LaunchVersionId(InstanceManifest manifest)
    {
        var raw = manifest.Components.FirstOrDefault(c => c.Uid == ComponentUids.RawVersion);
        if (raw is not null)
            return raw.Version;

        var mc = manifest.MinecraftVersion
            ?? throw new InvalidDataException($"Instance '{manifest.Name}' has no Minecraft component.");

        var loader = PrimaryLoader(manifest);
        return loader is null ? mc : loader.ProfileId ?? LoaderProfileId(loader.Uid, mc, loader.Version);
    }

    /// <summary>Conventional id a loader profile gets in <c>versions/</c> (installers may override it; see <see cref="ComponentRef.ProfileId"/>).</summary>
    public static string LoaderProfileId(string uid, string minecraft, string loaderVersion) => uid switch
    {
        ComponentUids.FabricLoader => $"fabric-loader-{loaderVersion}-{minecraft}",
        ComponentUids.QuiltLoader => $"quilt-loader-{loaderVersion}-{minecraft}",
        ComponentUids.LegacyFabric => $"fabric-loader-{loaderVersion}-{minecraft}",
        ComponentUids.Forge => $"{minecraft}-forge-{loaderVersion}",
        ComponentUids.NeoForge => $"neoforge-{loaderVersion}",
        ComponentUids.LiteLoader => $"{minecraft}-LiteLoader{minecraft}",
        ComponentUids.OptiFine => $"{minecraft}-OptiFine_{loaderVersion}",
        _ => throw new NotSupportedException($"Unknown component '{uid}'."),
    };
}
