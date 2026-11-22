using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Fluorite.Core.Nbt;
using Fluorite.Core.Servers;
using Fluorite.Core.Worlds;

namespace Fluorite.Core.Tests;

public sealed class NbtTests
{
    [Fact]
    public void Reads_HandWrittenCompound()
    {
        // TAG_Compound "" { TAG_Int "n" = 7, TAG_String "s" = "hi", TAG_List "l" = [TAG_Byte 1, 2] }
        byte[] bytes =
        [
            0x0A, 0x00, 0x00,
            0x03, 0x00, 0x01, (byte)'n', 0x00, 0x00, 0x00, 0x07,
            0x08, 0x00, 0x01, (byte)'s', 0x00, 0x02, (byte)'h', (byte)'i',
            0x09, 0x00, 0x01, (byte)'l', 0x01, 0x00, 0x00, 0x00, 0x02, 0x01, 0x02,
            0x00,
        ];
        var root = NbtFile.Read(new MemoryStream(bytes), out var compression);
        Assert.Equal(NbtCompression.None, compression);
        Assert.Equal(7, root.GetInt("n"));
        Assert.Equal("hi", root.GetString("s"));
        var list = root.GetList("l")!;
        Assert.Equal(NbtTagType.Byte, list.ElementType);
        Assert.Equal([1L, 2L], list.Select(t => t.AsLong!.Value));
    }

    [Fact]
    public void RoundTrips_EveryTagType_Gzipped()
    {
        var root = new NbtCompound();
        root["b"] = new NbtByte(-3);
        root["sh"] = new NbtShort(-300);
        root["i"] = new NbtInt(int.MinValue);
        root["l"] = new NbtLong(long.MaxValue);
        root["f"] = new NbtFloat(1.5f);
        root["d"] = new NbtDouble(-2.25);
        root["ba"] = new NbtByteArray(new byte[] { 1, 2, 3 });
        root["s"] = new NbtString("text");
        root["ia"] = new NbtIntArray(new[] { 1, -1 });
        root["la"] = new NbtLongArray(new[] { 5L, -5L });
        var inner = new NbtCompound();
        inner.Set("x", 1);
        var list = new NbtList(NbtTagType.Compound) { inner };
        root["list"] = list;
        root["empty"] = new NbtList(NbtTagType.End);

        var ms = new MemoryStream();
        NbtFile.Write(ms, root, NbtCompression.GZip);
        ms.Position = 0;
        var back = NbtFile.Read(ms, out var compression);

        Assert.Equal(NbtCompression.GZip, compression);
        Assert.Equal(["b", "sh", "i", "l", "f", "d", "ba", "s", "ia", "la", "list", "empty"], back.Select(kv => kv.Key));
        Assert.Equal(-3, back.GetLong("b"));
        Assert.Equal(-300, back.GetLong("sh"));
        Assert.Equal(int.MinValue, back.GetInt("i"));
        Assert.Equal(long.MaxValue, back.GetLong("l"));
        Assert.Equal(1.5f, ((NbtFloat)back["f"]!).Value);
        Assert.Equal(-2.25, ((NbtDouble)back["d"]!).Value);
        Assert.Equal(new byte[] { 1, 2, 3 }, ((NbtByteArray)back["ba"]!).Value.ToArray());
        Assert.Equal([1, -1], ((NbtIntArray)back["ia"]!).Value.ToArray());
        Assert.Equal([5L, -5L], ((NbtLongArray)back["la"]!).Value.ToArray());
        Assert.Equal(1, ((NbtCompound)back.GetList("list")![0]).GetInt("x"));
        Assert.Equal(0, back.GetList("empty")!.Count);
    }

    [Fact]
    public void ModifiedUtf8_MatchesJava()
    {
        // Java's DataOutput.writeUTF: U+0000 as C0 80, supplementary characters as two 3-byte surrogates.
        Assert.Equal(new byte[] { 0xC0, 0x80 }, NbtFile.EncodeModifiedUtf8("\0"));
        Assert.Equal(new byte[] { 0xED, 0xA0, 0xBD, 0xED, 0xB8, 0x80 }, NbtFile.EncodeModifiedUtf8("😀"));
        foreach (var s in new[] { "plain", "\0nul", "é中文", "😀 emoji", "" })
            Assert.Equal(s, NbtFile.DecodeModifiedUtf8(NbtFile.EncodeModifiedUtf8(s)));
        // Standard UTF-8 4-byte sequences (other tools) still decode.
        Assert.Equal("😀", NbtFile.DecodeModifiedUtf8(Encoding.UTF8.GetBytes("😀")));
    }

    [Fact]
    public void Rejects_NonCompoundRoot_AndTruncatedData()
    {
        Assert.Throws<InvalidDataException>(() => NbtFile.Read(new MemoryStream([0x08, 0x00, 0x00, 0x00, 0x00]), out _));
        Assert.Throws<EndOfStreamException>(() => NbtFile.Read(new MemoryStream([0x0A, 0x00, 0x00, 0x03, 0x00, 0x01, (byte)'n', 0x00]), out _));
    }
}

public sealed class WorldTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "fluorite-worlds-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_game))
            Directory.Delete(_game, recursive: true);
    }

    private string MakeWorld(string folder, string levelName = "My World", long lastPlayed = 1_700_000_000_000)
    {
        var dir = Path.Combine(_game, "saves", folder);
        Directory.CreateDirectory(Path.Combine(dir, "region"));
        Directory.CreateDirectory(Path.Combine(dir, "datapacks"));
        var data = new NbtCompound();
        data.Set("LevelName", levelName);
        data.Set("LastPlayed", lastPlayed);
        data.Set("GameType", 1);
        data.Set("hardcore", true);
        data.Set("allowCommands", false);
        data.Set("DataVersion", 4189);
        var version = new NbtCompound();
        version.Set("Name", "1.21.4");
        version.Set("Id", 4189);
        version.Set("Snapshot", false);
        data["Version"] = version;
        var gen = new NbtCompound();
        gen.Set("seed", -42L);
        data["WorldGenSettings"] = gen;
        data.Set("CustomModTag", "kept");
        var root = new NbtCompound { };
        root["Data"] = data;
        NbtFile.Write(Path.Combine(dir, "level.dat"), root, NbtCompression.GZip);
        File.WriteAllBytes(Path.Combine(dir, "region", "r.0.0.mca"), Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray());
        File.WriteAllText(Path.Combine(dir, "session.lock"), "☃");
        return dir;
    }

    [Fact]
    public void Reads_LevelDat()
    {
        var dir = MakeWorld("World");
        var w = WorldInfo.Read(dir);
        Assert.Equal("World", w.FolderName);
        Assert.Equal("My World", w.Name);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_000), w.LastPlayed);
        Assert.Equal(GameMode.Creative, w.GameMode);
        Assert.True(w.Hardcore);
        Assert.False(w.CheatsEnabled);
        Assert.Equal("1.21.4", w.VersionName);
        Assert.Equal(4189, w.DataVersion);
        Assert.Equal(-42L, w.Seed);
        Assert.Null(w.Error);
    }

    [Fact]
    public void Lists_BrokenWorlds_WithAnError()
    {
        var dir = Path.Combine(_game, "saves", "Broken");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "level.dat"), "not nbt");
        MakeWorld("Good", lastPlayed: 1);
        var worlds = new WorldStore(_game).GetWorlds();
        var broken = Assert.Single(worlds, w => w.FolderName == "Broken");
        Assert.NotNull(broken.Error);
        Assert.Equal("Broken", broken.Name);
    }

    [Fact]
    public void Backup_MatchesMinecraftsLayout_AndRestores()
    {
        MakeWorld("World");
        var store = new WorldStore(_game);
        var world = store.Find("World")!;

        var backup = store.Backup(world, ct: Ct);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2}_World\.zip$", backup.FileName);
        Assert.Equal(Path.Combine(_game, "backups"), Path.GetDirectoryName(backup.Path));
        using (var zip = ZipFile.OpenRead(backup.Path))
        {
            Assert.All(zip.Entries, e => Assert.StartsWith("World/", e.FullName, StringComparison.Ordinal));
            Assert.DoesNotContain(zip.Entries, e => e.Name == "session.lock");
            Assert.Contains(zip.Entries, e => e.FullName == "World/datapacks/"); // Empty folders survive.
        }

        var listed = Assert.Single(store.GetBackups("World"));
        Assert.False(listed.IsAutomatic);
        Assert.Equal("World", listed.WorldFolder);

        var restored = store.Restore(listed, "World (restored)", Ct);
        Assert.Equal(
            File.ReadAllBytes(Path.Combine(world.Path, "region", "r.0.0.mca")),
            File.ReadAllBytes(Path.Combine(restored, "region", "r.0.0.mca")));
        Assert.Equal("My World", WorldInfo.Read(restored).Name);
        Assert.Throws<IOException>(() => store.Restore(listed, "World", Ct));
    }

    [Fact]
    public void PruneAutomaticBackups_KeepsManualOnes()
    {
        MakeWorld("World");
        var store = new WorldStore(_game);
        var world = store.Find("World")!;
        store.Backup(world, ct: Ct);
        for (var i = 0; i < 3; i++)
            store.Backup(world, automatic: true, ct: Ct);

        Assert.Equal(3, store.GetBackups("World").Count(b => b.IsAutomatic));
        Assert.Equal(2, store.PruneAutomaticBackups("World", keep: 1));
        var left = store.GetBackups("World");
        Assert.Equal(2, left.Count);
        Assert.Single(left, b => b.IsAutomatic);
        Assert.Single(left, b => !b.IsAutomatic);
    }

    [Fact]
    public void Import_FindsNestedWorld_AndPicksAFreeName()
    {
        var zipPath = Path.Combine(_game, "map.zip");
        Directory.CreateDirectory(_game);
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var src = MakeWorld("Source");
            zip.CreateEntryFromFile(Path.Combine(src, "level.dat"), "maps/Sky Island/level.dat");
            zip.CreateEntryFromFile(Path.Combine(src, "region", "r.0.0.mca"), "maps/Sky Island/region/r.0.0.mca");
            zip.CreateEntry("maps/readme.txt");
        }
        var store = new WorldStore(_game);
        Assert.Equal("Sky Island", store.Import(zipPath, Ct));
        Assert.Equal("Sky Island (1)", store.Import(zipPath, Ct));
        Assert.True(File.Exists(Path.Combine(_game, "saves", "Sky Island", "region", "r.0.0.mca")));
        Assert.False(File.Exists(Path.Combine(_game, "saves", "Sky Island", "readme.txt")));
    }

    [Fact]
    public void Import_RejectsEntriesOutsideTheWorld()
    {
        Directory.CreateDirectory(_game);
        var zipPath = Path.Combine(_game, "evil.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntry("World/level.dat");
            using var w = new StreamWriter(zip.CreateEntry("World/../../escaped.txt").Open());
            w.Write("x");
        }
        Assert.Throws<InvalidDataException>(() => new WorldStore(_game).Import(zipPath, Ct));
        Assert.False(File.Exists(Path.Combine(_game, "escaped.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_game, "saves")));
    }

    [Fact]
    public void Rename_And_Copy_UpdateLevelName_KeepingOtherTags()
    {
        MakeWorld("World");
        var store = new WorldStore(_game);
        var world = store.Find("World")!;
        WorldStore.Rename(world, "Renamed");
        Assert.Equal("Renamed", WorldInfo.Read(world.Path).Name);
        Assert.True(File.Exists(Path.Combine(world.Path, "level.dat_old")));
        Assert.Equal("kept", NbtFile.Read(Path.Combine(world.Path, "level.dat")).GetCompound("Data")!.GetString("CustomModTag"));

        var copy = store.Copy(store.Find("World")!, "Renamed copy", Ct);
        Assert.Equal("Renamed copy", copy);
        Assert.Equal("Renamed copy", store.Find(copy)!.Name);
        Assert.False(File.Exists(Path.Combine(_game, "saves", copy, "session.lock")));
    }

    [Theory]
    [InlineData("World", "World")]
    [InlineData("a/b:c", "a_b_c")]
    [InlineData("CON", "_CON_")]
    [InlineData("  trailing.  ", "trailing")]
    [InlineData("...", "World")]
    public void SafeFileName(string input, string expected) => Assert.Equal(expected, WorldStore.SafeFileName(input));

    [Fact]
    public void FindWorldRoot_PrefersTheShallowestLevelDat()
    {
        Assert.Equal("", WorldStore.FindWorldRoot(["level.dat", "region/r.0.0.mca"]));
        Assert.Equal("W/", WorldStore.FindWorldRoot(["W/level.dat", "W/DIM1/level.dat"]));
        Assert.Equal("a/b/", WorldStore.FindWorldRoot(["__MACOSX/level.dat", "a/b/level.dat"]));
        Assert.Null(WorldStore.FindWorldRoot(["readme.txt", "level.dat_old"]));
    }

    [Fact]
    public void Screenshots_NewestFirst()
    {
        var dir = ScreenshotLibrary.DirectoryOf(_game);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "old.png"), [1]);
        File.SetLastWriteTime(Path.Combine(dir, "old.png"), DateTime.Now.AddDays(-1));
        File.WriteAllBytes(Path.Combine(dir, "new.png"), [1]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "");
        Assert.Equal(["new.png", "old.png"], ScreenshotLibrary.List(_game).Select(s => s.FileName));
    }
}

public sealed class ServerListTests : IDisposable
{
    private readonly string _game = Path.Combine(Path.GetTempPath(), "fluorite-servers-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_game))
            Directory.Delete(_game, recursive: true);
    }

    [Fact]
    public void RoundTrip_KeepsUnknownTags_AndHiddenEntriesLast()
    {
        Directory.CreateDirectory(_game);
        var servers = new NbtList(NbtTagType.Compound);
        var hidden = new NbtCompound();
        hidden.Set("name", "Quick Play");
        hidden.Set("ip", "hidden.example");
        hidden.Set("hidden", true);
        servers.Add(hidden);
        var known = new NbtCompound();
        known.Set("name", "Hypixel");
        known.Set("ip", "mc.hypixel.net");
        known.Set("acceptTextures", false);
        known.Set("modTag", "keep me");
        servers.Add(known);
        var root = new NbtCompound();
        root["servers"] = servers;
        NbtFile.Write(Path.Combine(_game, "servers.dat"), root, NbtCompression.None);

        var list = ServerList.Load(_game);
        Assert.Equal(2, list.Servers.Count);
        var hyp = list.Servers.Single(s => s.Name == "Hypixel");
        Assert.Equal(ServerResourcePacks.Disabled, hyp.ResourcePacks);
        hyp.ResourcePacks = ServerResourcePacks.Prompt;
        list.Servers.Add(new ServerEntry("Local", "localhost:25566") { ResourcePacks = ServerResourcePacks.Enabled });
        list.Save(_game);

        Assert.True(File.Exists(Path.Combine(_game, "servers.dat_old")));
        var saved = NbtFile.Read(Path.Combine(_game, "servers.dat"), out var compression);
        Assert.Equal(NbtCompression.None, compression);
        var entries = saved.GetList("servers")!.Cast<NbtCompound>().ToList();
        Assert.Equal(["Hypixel", "Local", "Quick Play"], entries.Select(e => e.GetString("name")));
        Assert.Equal("keep me", entries[0].GetString("modTag"));
        Assert.False(entries[0].Contains("acceptTextures"));
        Assert.Equal(true, entries[1].GetBool("acceptTextures"));
        Assert.Equal(true, entries[2].GetBool("hidden"));
    }

    [Fact]
    public void Missing_File_IsAnEmptyList() => Assert.Empty(ServerList.Load(_game).Servers);
}

public sealed class ChatTextTests
{
    [Fact]
    public void Legacy_Codes()
    {
        var spans = ChatText.FromLegacy("§aHello §lWorld§r!");
        Assert.Equal(3, spans.Count);
        Assert.Equal(new ChatSpan("Hello ", "#55FF55"), spans[0]);
        Assert.Equal(new ChatSpan("World", "#55FF55", Bold: true), spans[1]);
        Assert.Equal(new ChatSpan("!"), spans[2]);
        Assert.Equal("Hello World!", ChatText.StripCodes("§aHello §lWorld§r!"));
    }

    [Fact]
    public void Bungee_HexColour() =>
        Assert.Equal(new ChatSpan("hex", "#12AB3F"), Assert.Single(ChatText.FromLegacy("§x§1§2§a§b§3§fhex")));

    [Fact]
    public void Json_Components_InheritStyles()
    {
        using var doc = JsonDocument.Parse("""
            {"text":"A","color":"gold","bold":true,"extra":[{"text":"B","bold":false},"§cC",{"text":"D","color":"#00ff00"}]}
            """);
        var spans = ChatText.FromJson(doc.RootElement);
        Assert.Equal(
            [new ChatSpan("A", "#FFAA00", Bold: true), new ChatSpan("B", "#FFAA00"), new ChatSpan("C", "#FF5555"), new ChatSpan("D", "#00FF00", Bold: true)],
            spans);
    }

    [Fact]
    public void Json_Arrays_AndPlainStrings()
    {
        using var doc = JsonDocument.Parse("""[{"text":"x","italic":true},"y",{"text":"z"}]""");
        // "y" and "z" inherit the first element's italic, so it's all one run.
        Assert.Equal([new ChatSpan("xyz", Italic: true)], ChatText.FromJson(doc.RootElement));
        using var str = JsonDocument.Parse("\"§6Gold\"");
        Assert.Equal(new ChatSpan("Gold", "#FFAA00"), Assert.Single(ChatText.FromJson(str.RootElement)));
    }
}

public sealed class ServerPingTests
{
    [Theory]
    [InlineData("play.example.net", "play.example.net", 25565, false)]
    [InlineData("play.example.net:25570", "play.example.net", 25570, true)]
    [InlineData("1.2.3.4:x", "1.2.3.4", 25565, false)]
    [InlineData("[::1]:25566", "::1", 25566, true)]
    [InlineData("[::1]", "::1", 25565, false)]
    [InlineData(" host ", "host", 25565, false)]
    public void ParseAddress(string input, string host, int port, bool explicitPort) =>
        Assert.Equal((host, port, explicitPort), ServerPinger.ParseAddress(input));

    [Fact]
    public void ParseStatus_ReadsTheUsualFields()
    {
        var status = ServerPinger.ParseStatus("""
            {"version":{"name":"Paper 1.21.4","protocol":769},
             "players":{"max":100,"online":5,"sample":[{"name":"§aSteve","id":"00000000-0000-0000-0000-000000000000"}]},
             "description":{"text":"Hello","extra":[{"text":" there","color":"red"}]},
             "favicon":"data:image/png;base64,iVBO\nRw0K"}
            """, TimeSpan.FromMilliseconds(12));
        Assert.Equal("Paper 1.21.4", status.VersionName);
        Assert.Equal(769, status.Protocol);
        Assert.Equal((5, 100), (status.OnlinePlayers, status.MaxPlayers));
        Assert.Equal(["Steve"], status.PlayerSample);
        Assert.Equal("Hello there", ChatText.ToPlainText(status.Motd));
        Assert.Equal("iVBORw0K", status.FaviconBase64);
    }

    [Fact]
    public void ParseLegacy_BothFormats()
    {
        var v16 = ServerPinger.ParseLegacy("§1\u000074\u00001.6.4\u0000§eOld server\u00003\u000020", TimeSpan.Zero);
        Assert.Equal(("1.6.4", 74, 3, 20), (v16.VersionName, v16.Protocol, v16.OnlinePlayers, v16.MaxPlayers));
        Assert.Equal("Old server", ChatText.ToPlainText(v16.Motd));
        Assert.True(v16.IsLegacy);

        var beta = ServerPinger.ParseLegacy("A Beta Server§1§8", TimeSpan.Zero);
        Assert.Equal(("A Beta Server", 1, 8), (ChatText.ToPlainText(beta.Motd), beta.OnlinePlayers, beta.MaxPlayers));
    }

    [Fact]
    public async Task Pings_AModernServer()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            var handshake = await ReadPacket(s);
            Assert.Equal(0x00, handshake[0]);
            Assert.Equal(1, handshake[^1]); // Next state: status.
            Assert.Equal([0x00], await ReadPacket(s));
            await WritePacket(s, [0x00, .. VarString("""{"version":{"name":"1.21.4","protocol":769},"players":{"max":20,"online":1},"description":"§bHi"}""")]);
            var ping = await ReadPacket(s);
            Assert.Equal(0x01, ping[0]);
            await WritePacket(s, ping); // Pong echoes the payload.
        }, TestContext.Current.CancellationToken);

        var status = await new ServerPinger().PingAsync($"127.0.0.1:{port}", TestContext.Current.CancellationToken);
        await server;
        Assert.Equal("1.21.4", status.VersionName);
        Assert.Equal((1, 20), (status.OnlinePlayers, status.MaxPlayers));
        Assert.Equal(new ChatSpan("Hi", "#55FFFF"), Assert.Single(status.Motd));
        Assert.False(status.IsLegacy);
    }

    [Fact]
    public async Task FallsBackToTheLegacyPing()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            // A 1.6 server doesn't understand the modern handshake and kicks.
            using (var first = await listener.AcceptTcpClientAsync())
            {
                var s = first.GetStream();
                await s.WriteAsync(new byte[] { 0xFF, 0x00, 0x00 });
            }
            using var second = await listener.AcceptTcpClientAsync();
            var s2 = second.GetStream();
            // FE 01 FA, "MC|PingHost", then protocol + host "127.0.0.1" + port: read it all so closing doesn't reset.
            var request = new byte[3 + 2 + 22 + 2 + 1 + 2 + 18 + 4];
            await s2.ReadExactlyAsync(request);
            Assert.Equal(new byte[] { 0xFE, 0x01, 0xFA }, request[..3]);
            var text = "§1\u000074\u00001.6.4\u0000Legacy\u00002\u000010";
            var body = Encoding.BigEndianUnicode.GetBytes(text);
            await s2.WriteAsync(new byte[] { 0xFF, (byte)(text.Length >> 8), (byte)text.Length }.Concat(body).ToArray());
            second.Client.Shutdown(SocketShutdown.Send);
            _ = await s2.ReadAsync(new byte[1]); // Wait for the client to hang up.
        }, TestContext.Current.CancellationToken);

        var status = await new ServerPinger().PingAsync($"127.0.0.1:{port}", TestContext.Current.CancellationToken);
        await server;
        Assert.True(status.IsLegacy);
        Assert.Equal("1.6.4", status.VersionName);
        Assert.Equal((2, 10), (status.OnlinePlayers, status.MaxPlayers));
    }

    [Fact]
    public async Task UsesTheSrvRecord_WhenNoPortIsGiven()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            var s = client.GetStream();
            await ReadPacket(s);
            await ReadPacket(s);
            await WritePacket(s, [0x00, .. VarString("""{"version":{"name":"srv","protocol":1},"players":{"max":1,"online":0}}""")]);
        }, TestContext.Current.CancellationToken);

        var status = await new ServerPinger(new FakeSrv("127.0.0.1", port)).PingAsync("play.example.test", TestContext.Current.CancellationToken);
        await server;
        Assert.Equal("srv", status.VersionName);
    }

    private sealed class FakeSrv(string host, int port) : ISrvResolver
    {
        public Task<(string Host, int Port)?> ResolveMinecraftAsync(string name, CancellationToken ct = default) =>
            Task.FromResult<(string Host, int Port)?>(name == "play.example.test" ? (host, port) : null);
    }

    private static async Task<byte[]> ReadPacket(Stream s)
    {
        var length = 0;
        for (var i = 0; ; i++)
        {
            var b = new byte[1];
            await s.ReadExactlyAsync(b);
            length |= (b[0] & 0x7F) << (7 * i);
            if ((b[0] & 0x80) == 0)
                break;
        }
        var data = new byte[length];
        await s.ReadExactlyAsync(data);
        return data;
    }

    private static async Task WritePacket(Stream s, byte[] payload) =>
        await s.WriteAsync(VarInt(payload.Length).Concat(payload).ToArray());

    private static byte[] VarInt(int value)
    {
        var bytes = new List<byte>();
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
                b |= 0x80;
            bytes.Add(b);
        }
        while (v != 0);
        return bytes.ToArray();
    }

    private static byte[] VarString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        return VarInt(bytes.Length).Concat(bytes).ToArray();
    }
}
