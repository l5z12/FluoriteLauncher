using Fluorite.Core.Nbt;

namespace Fluorite.Core.Servers;

/// <summary>Whether the game downloads a server's resource pack (the <c>acceptTextures</c> tag).</summary>
public enum ServerResourcePacks
{
    Prompt,
    Enabled,
    Disabled,
}

/// <summary>One entry of the multiplayer list.</summary>
public sealed class ServerEntry
{
    public ServerEntry(string name, string address)
    {
        Name = name;
        Address = address;
    }

    private ServerEntry(NbtCompound raw)
    {
        Raw = raw;
        Name = raw.GetString("name") ?? "";
        Address = raw.GetString("ip") ?? "";
        IconBase64 = raw.GetString("icon");
        Hidden = raw.GetBool("hidden") == true;
        ResourcePacks = raw.GetBool("acceptTextures") switch
        {
            true => ServerResourcePacks.Enabled,
            false => ServerResourcePacks.Disabled,
            null => ServerResourcePacks.Prompt,
        };
    }

    /// <summary>The tags as read, so ones Fluorite doesn't know survive a save.</summary>
    private NbtCompound Raw { get; } = new();

    public string Name { get; set; }

    /// <summary><c>host</c>, <c>host:port</c> or <c>[v6]:port</c>, as typed in game.</summary>
    public string Address { get; set; }

    /// <summary>Last server icon the game saved (base64 PNG).</summary>
    public string? IconBase64 { get; set; }

    public ServerResourcePacks ResourcePacks { get; set; } = ServerResourcePacks.Prompt;

    /// <summary>Kept by the game but not shown in its list (e.g. the last Quick Play server).</summary>
    public bool Hidden { get; set; }

    internal static ServerEntry FromNbt(NbtCompound tag) => new(tag);

    internal NbtCompound ToNbt()
    {
        Raw.Set("name", Name);
        Raw.Set("ip", Address);
        if (IconBase64 is { Length: > 0 } icon)
            Raw.Set("icon", icon);
        else
            Raw.Remove("icon");
        if (ResourcePacks == ServerResourcePacks.Prompt)
            Raw.Remove("acceptTextures");
        else
            Raw.Set("acceptTextures", ResourcePacks == ServerResourcePacks.Enabled);
        if (Hidden)
            Raw.Set("hidden", true);
        else
            Raw.Remove("hidden");
        return Raw;
    }
}

/// <summary>
/// The game's multiplayer list, <c>servers.dat</c> in the game directory (uncompressed NBT). Saving keeps unknown tags
/// and the previous file as <c>servers.dat_old</c>, like the game.
/// </summary>
public sealed class ServerList
{
    public const string FileName = "servers.dat";

    private NbtCompound _root = new();

    public List<ServerEntry> Servers { get; } = [];

    public static ServerList Load(string gameDirectory)
    {
        var list = new ServerList();
        var path = Path.Combine(gameDirectory, FileName);
        if (!File.Exists(path))
            return list;
        list._root = NbtFile.Read(path);
        foreach (var tag in list._root.GetList("servers")?.OfType<NbtCompound>() ?? [])
            list.Servers.Add(ServerEntry.FromNbt(tag));
        return list;
    }

    public void Save(string gameDirectory)
    {
        var servers = new NbtList(NbtTagType.Compound);
        // The game writes visible entries first, then hidden ones.
        foreach (var s in Servers.Where(s => !s.Hidden).Concat(Servers.Where(s => s.Hidden)))
            servers.Add(s.ToNbt());
        _root["servers"] = servers;
        Directory.CreateDirectory(gameDirectory);
        NbtFile.WriteSafely(Path.Combine(gameDirectory, FileName), _root, NbtCompression.None);
    }
}
