using System.Text.Json;
using System.Text.Json.Nodes;

namespace Fluorite.Core.Instances;

/// <summary>What an installed version JSON turns out to be.</summary>
/// <param name="MinecraftVersion">The vanilla version it builds on (null when it can't be told).</param>
/// <param name="LoaderUid">Primary mod loader component uid, if any.</param>
public sealed record InspectedProfile(string Id, string? MinecraftVersion, string? LoaderUid, string? LoaderVersion);

/// <summary>
/// Infers the component stack of a version JSON installed by another launcher (official launcher, HMCL, PCL, loader
/// installers, Technic packs) from its <c>inheritsFrom</c> chain, library coordinates and FML arguments, so the
/// instance shows the right loader and content filters, and its existing profile launches as-is.
/// </summary>
public static class VersionProfileInspector
{
    public static InspectedProfile Inspect(GameDirectories store, string id)
    {
        var chain = new List<JsonObject>();
        var current = id;
        for (var depth = 0; depth < 8 && current is not null; depth++)
        {
            var json = TryLoad(store.VersionJson(current));
            if (json is null)
            {
                // Parent not installed (vanilla JSON fetched on first launch): its id is the Minecraft version.
                return Build(id, chain, missingParent: current);
            }
            chain.Add(json);
            current = Str(json, "inheritsFrom");
        }
        return Build(id, chain, missingParent: null);
    }

    /// <summary>Same inference for a JSON document that isn't in a store (e.g. Technic's <c>bin/version.json</c>).</summary>
    public static InspectedProfile Inspect(JsonObject json, string? fallbackMinecraft = null)
    {
        var id = Str(json, "id") ?? "";
        var result = Build(id, [json], missingParent: Str(json, "inheritsFrom"));
        return result.MinecraftVersion is null && fallbackMinecraft is not null ? result with { MinecraftVersion = fallbackMinecraft } : result;
    }

    private static InspectedProfile Build(string id, List<JsonObject> chain, string? missingParent)
    {
        var libraries = chain.SelectMany(j => j["libraries"] as JsonArray ?? [])
            .Select(l => l is JsonObject o ? Str(o, "name") : null)
            .OfType<string>()
            .ToList();
        var gameArgs = chain.SelectMany(GameArguments).ToList();

        var (loaderUid, loaderVersion) = DetectLoader(libraries, gameArgs);
        var mc = missingParent
            ?? (chain.Count > 0 ? HmclGameVersion(chain[^1]) ?? Str(chain[^1], "clientVersion") : null)
            ?? Arg(gameArgs, "--fml.mcVersion")
            ?? MinecraftFromLibraries(libraries)
            ?? (chain.Count > 0 && Str(chain[^1], "inheritsFrom") is null ? Str(chain[^1], "id") : null);
        return new InspectedProfile(id, mc, loaderUid, loaderVersion);
    }

    private static (string? Uid, string? Version) DetectLoader(List<string> libraries, List<string> gameArgs)
    {
        string? Lib(string prefix) => libraries.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } hit
            ? hit[prefix.Length..].Split(':')[0]
            : null;

        if (Lib("net.neoforged:neoforge:") is { } neo)
            return (ComponentUids.NeoForge, neo);
        if (Lib("net.neoforged:forge:") is { } neoLegacy)
            return (ComponentUids.NeoForge, StripMinecraftPrefix(neoLegacy));
        if (libraries.Any(l => l.StartsWith("net.neoforged.fancymodloader:", StringComparison.OrdinalIgnoreCase)))
        {
            if (Arg(gameArgs, "--fml.neoForgeVersion") is { } v)
                return (ComponentUids.NeoForge, v);
            if (Arg(gameArgs, "--fml.forgeVersion") is { } fv)
                return (ComponentUids.NeoForge, fv);
        }
        if ((Lib("net.minecraftforge:forge:") ?? Lib("net.minecraftforge:fmlloader:")) is { } forge)
            return (ComponentUids.Forge, StripMinecraftPrefix(forge));
        if (Lib("net.minecraftforge:minecraftforge:") is { } oldForge)
            return (ComponentUids.Forge, oldForge);
        if (Arg(gameArgs, "--fml.forgeVersion") is { } forgeArg)
            return (ComponentUids.Forge, forgeArg);
        if (Lib("net.fabricmc:fabric-loader:") is { } fabric)
            return (libraries.Any(l => l.StartsWith("net.legacyfabric", StringComparison.OrdinalIgnoreCase)) ? ComponentUids.LegacyFabric : ComponentUids.FabricLoader, fabric);
        if (Lib("org.quiltmc:quilt-loader:") is { } quilt)
            return (ComponentUids.QuiltLoader, quilt);
        if (Lib("com.mumfrey:liteloader:") is { } lite)
            return (ComponentUids.LiteLoader, lite);
        if (Lib("optifine:OptiFine:") is { } of)
            return (ComponentUids.OptiFine, of.Contains('_', StringComparison.Ordinal) ? of[(of.IndexOf('_', StringComparison.Ordinal) + 1)..] : of);
        return (null, null);
    }

    /// <summary><c>1.20.1-47.2.0</c> → <c>47.2.0</c>; <c>1.7.10-10.13.4.1614-1.7.10</c> → <c>10.13.4.1614</c>.</summary>
    public static string StripMinecraftPrefix(string version)
    {
        var parts = version.Split('-');
        return parts.Length >= 2 && parts[0].Length > 0 && char.IsAsciiDigit(parts[0][0]) && parts[0].Contains('.', StringComparison.Ordinal)
            && parts[1].Length > 0 && char.IsAsciiDigit(parts[1][0])
            ? parts[1]
            : version;
    }

    private static string? MinecraftFromLibraries(List<string> libraries)
    {
        foreach (var prefix in new[] { "net.fabricmc:intermediary:", "org.quiltmc:hashed:", "net.legacyfabric.v2:intermediary:", "net.legacyfabric:intermediary:" })
        {
            if (libraries.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } hit)
                return hit[prefix.Length..].Split(':')[0];
        }
        foreach (var prefix in new[] { "net.minecraftforge:forge:", "net.minecraftforge:fmlloader:", "net.neoforged:forge:" })
        {
            if (libraries.FirstOrDefault(l => l.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } hit && hit[prefix.Length..].Split('-') is [var mc, _, ..])
                return mc;
        }
        return null;
    }

    /// <summary>HMCL records merged versions as <c>"patches": [{"id": "game", "version": "1.20.1"}, …]</c>.</summary>
    private static string? HmclGameVersion(JsonObject json) =>
        (json["patches"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(p => Str(p, "id") == "game") is { } game ? Str(game, "version") : null;

    private static IEnumerable<string> GameArguments(JsonObject json)
    {
        if (json["arguments"]?["game"] is JsonArray args)
        {
            foreach (var a in args)
            {
                if (a is JsonValue v && v.TryGetValue<string>(out var s))
                    yield return s;
            }
        }
        if (Str(json, "minecraftArguments") is { } legacy)
        {
            foreach (var s in legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                yield return s;
        }
    }

    private static string? Arg(List<string> args, string name)
    {
        var i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static JsonObject? TryLoad(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonNode.Parse(File.ReadAllBytes(path), documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;

    /// <summary>Components for an existing profile: the loader is "installed" with <see cref="ComponentRef.ProfileId"/> = <paramref name="profile"/>.Id.</summary>
    public static List<ComponentRef> ComponentsFor(InspectedProfile profile)
    {
        var list = new List<ComponentRef>();
        if (profile.MinecraftVersion is { } mc)
            list.Add(new ComponentRef { Uid = ComponentUids.Minecraft, Version = mc });
        if (profile.LoaderUid is { } uid && profile.LoaderVersion is { } lv)
            list.Add(new ComponentRef { Uid = uid, Version = lv, ProfileId = profile.Id });
        else if (profile.MinecraftVersion != profile.Id)
            list.Add(new ComponentRef { Uid = ComponentUids.RawVersion, Version = profile.Id });
        return list;
    }
}
