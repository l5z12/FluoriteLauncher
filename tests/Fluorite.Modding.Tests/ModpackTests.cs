using System.IO.Compression;
using System.Text;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Content;
using Fluorite.Modding.Modpacks;
using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Tests;

internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fluorite-pack-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name, byte[] bytes)
    {
        var p = System.IO.Path.Combine(Path, name);
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class PackPathTests
{
    [Theory]
    [InlineData("mods/a.jar", "mods/a.jar")]
    [InlineData("./config\\x.toml", "config/x.toml")]
    [InlineData("config//nested/x.json", "config/nested/x.json")]
    [InlineData("../evil.jar", null)]
    [InlineData("mods/../../evil.jar", null)]
    [InlineData("/etc/passwd", null)]
    [InlineData("C:/Windows/x.dll", null)]
    [InlineData("config/CON.txt", null)]
    [InlineData("mods/trailingdot.", null)]
    [InlineData("", null)]
    public void Sanitize(string input, string? expected) => Assert.Equal(expected, PackPaths.Sanitize(input));

    [Fact]
    public void Resolve_RejectsEscapes()
    {
        Assert.Throws<InvalidDataException>(() => PackPaths.Resolve(@"C:\game", "../x"));
        Assert.Equal(@"C:\game\mods\a.jar", PackPaths.Resolve(@"C:\game", "mods/a.jar"));
    }
}

public class ModpackReaderTests
{
    private const string MrIndex = """
        {"formatVersion":1,"game":"minecraft","versionId":"1.2.3","name":"Test Pack","summary":"S",
         "files":[
           {"path":"mods/a.jar","hashes":{"sha1":"aa","sha512":"bb"},"downloads":["https://cdn.modrinth.com/a.jar"],"fileSize":10},
           {"path":"mods/server-only.jar","hashes":{"sha1":"cc"},"env":{"client":"unsupported","server":"required"},"downloads":["https://x/s.jar"]},
           {"path":"mods/optional.jar","hashes":{"sha1":"dd"},"env":{"client":"optional","server":"optional"},"downloads":["https://x/o.jar"]},
           {"path":"../escape.jar","hashes":{"sha1":"ee"},"downloads":["https://x/e.jar"]}],
         "dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.9"}}
        """;

    [Fact]
    public void Mrpack()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("p.mrpack", Jar.Build(
            ("modrinth.index.json", MrIndex),
            ("overrides/config/a.toml", "base"),
            ("overrides/options.txt", "x"),
            ("client-overrides/config/a.toml", "client wins"),
            ("overrides/../../evil.txt", "no")));
        var m = ModpackReader.Read(path);
        Assert.Equal(PackFormat.Modrinth, m.Format);
        Assert.Equal(("Test Pack", "1.2.3", "1.21.1", ComponentUids.FabricLoader, "0.16.9"), (m.Name, m.Version, m.MinecraftVersion, m.LoaderUid, m.LoaderVersion));
        Assert.Equal(3, m.Files.Count);
        Assert.Equal(PackSide.Unsupported, m.Files.Single(f => f.Path == "mods/server-only.jar").Client);
        Assert.Equal(PackSide.Optional, m.Files.Single(f => f.Path == "mods/optional.jar").Client);
        Assert.Contains(m.Warnings, w => w.Contains("escape", StringComparison.Ordinal));
        var config = Assert.Single(m.Overrides, o => o.Path == "config/a.toml");
        Assert.Equal("client-overrides/config/a.toml", config.Entry);
        Assert.DoesNotContain(m.Overrides, o => o.Path.Contains("evil", StringComparison.Ordinal));

        var entries = ModpackInstaller.Entries(m, includeOptional: false);
        Assert.False(entries.ContainsKey("mods/server-only.jar"));
        Assert.False(entries.ContainsKey("mods/optional.jar"));
        Assert.True(entries.ContainsKey("mods/a.jar"));
        Assert.Equal("sha1", entries["config/a.toml"].Algorithm);
    }

    [Theory]
    [InlineData("forge-47.2.0", "1.20.1", ComponentUids.Forge, "47.2.0")]
    [InlineData("neoforge-21.1.80", "1.21.1", ComponentUids.NeoForge, "21.1.80")]
    [InlineData("neoforge-1.20.1-47.1.106", "1.20.1", ComponentUids.NeoForge, "47.1.106")]
    [InlineData("fabric-0.16.9", "1.21.1", ComponentUids.FabricLoader, "0.16.9")]
    [InlineData("quilt-0.26.4", "1.21.1", ComponentUids.QuiltLoader, "0.26.4")]
    public void CurseForgeLoaderIds(string id, string mc, string uid, string version) =>
        Assert.Equal((uid, version), ModpackReader.ParseCurseForgeLoader(id, mc)!);

    [Fact]
    public void CurseForgeZip()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("cf.zip", Jar.Build(
            ("manifest.json", """
                {"minecraft":{"version":"1.20.1","modLoaders":[{"id":"forge-47.2.0","primary":true}],"recommendedRam":6144},
                 "manifestType":"minecraftModpack","manifestVersion":1,"name":"CF Pack","version":"2.0","author":"me",
                 "files":[{"projectID":238222,"fileID":4712866,"required":true},{"projectID":1,"fileID":2,"required":false}],"overrides":"custom"}
                """),
            ("custom/config/x.cfg", "y")));
        var m = ModpackReader.Read(path);
        Assert.Equal(PackFormat.CurseForge, m.Format);
        Assert.Equal((ComponentUids.Forge, "47.2.0"), (m.LoaderUid, m.LoaderVersion));
        Assert.Equal(6144, m.Settings.MaxMemoryMb);
        Assert.Equal([(238222L, 4712866L, true), (1L, 2L, false)], m.UnresolvedCurseForge);
        Assert.Equal("config/x.cfg", Assert.Single(m.Overrides).Path);
    }

    [Fact]
    public void PrismZip_WithTopFolder()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("prism.zip", Jar.Build(
            ("My Pack/instance.cfg", "[General]\nInstanceType=OneSix\nname=Prism Export\nOverrideMemory=true\nMaxMemAlloc=4096\n"),
            ("My Pack/mmc-pack.json", """{"components":[{"uid":"net.minecraft","version":"1.21.4"},{"uid":"net.fabricmc.intermediary","version":"1.21.4"},{"uid":"net.fabricmc.fabric-loader","version":"0.16.9"}],"formatVersion":1}"""),
            ("My Pack/.minecraft/mods/a.jar", "jar"),
            ("My Pack/.minecraft/options.txt", "o")));
        var m = ModpackReader.Read(path);
        Assert.Equal(PackFormat.MultiMC, m.Format);
        Assert.Equal(("Prism Export", "1.21.4", ComponentUids.FabricLoader), (m.Name, m.MinecraftVersion, m.LoaderUid));
        Assert.Equal(4096, m.Settings.MaxMemoryMb);
        Assert.Equal(["mods/a.jar", "options.txt"], m.Overrides.Select(o => o.Path).Order());
    }

    [Fact]
    public void TechnicZip_FromVersionJson()
    {
        using var tmp = new TempFolder();
        var path = tmp.File("technic.zip", Jar.Build(
            ("bin/version.json", """{"id":"1.12.2-forge","inheritsFrom":"1.12.2","libraries":[{"name":"net.minecraftforge:forge:1.12.2-14.23.5.2860"}]}"""),
            ("mods/x.jar", "x"),
            ("config/y.cfg", "y")));
        var m = ModpackReader.Read(path);
        Assert.Equal((PackFormat.Technic, "1.12.2", ComponentUids.Forge, "14.23.5.2860"), (m.Format, m.MinecraftVersion, m.LoaderUid, m.LoaderVersion));
        Assert.Equal(2, m.Overrides.Count);
    }

    [Fact]
    public void Unknown_Throws()
    {
        using var tmp = new TempFolder();
        Assert.Throws<InvalidDataException>(() => ModpackReader.Read(tmp.File("x.zip", Jar.Build(("readme.txt", "hi")))));
    }

    /// <summary>A pack zip dropped on an instance's worlds or resource packs installs; their own zips are added.</summary>
    [Fact]
    public void IsModpack_TellsPacksFromOtherZips()
    {
        using var tmp = new TempFolder();
        Assert.True(ModpackReader.IsModpack(tmp.File("mr.zip", Jar.Build(("modrinth.index.json", MrIndex)))));
        Assert.True(ModpackReader.IsModpack(tmp.File("cf.zip", Jar.Build(("manifest.json", """{"manifestType":"minecraftModpack"}""")))));
        Assert.True(ModpackReader.IsModpack(tmp.File("prism.zip", Jar.Build(("My Pack/instance.cfg", "[General]\n")))));
        Assert.True(ModpackReader.IsModpack(tmp.File("technic.zip", Jar.Build(("bin/version.json", "{}")))));
        Assert.False(ModpackReader.IsModpack(tmp.File("world.zip", Jar.Build(("My World/level.dat", "x")))));
        Assert.False(ModpackReader.IsModpack(tmp.File("resources.zip", Jar.Build(("pack.mcmeta", "{}"), ("assets/x.png", "x")))));
        Assert.False(ModpackReader.IsModpack(tmp.File("other.zip", Jar.Build(("manifest.json", """{"manifestType":"somethingElse"}""")))));
        Assert.False(ModpackReader.IsModpack(tmp.File("broken.zip", "not a zip"u8.ToArray())));
    }

    [Theory]
    [InlineData("modrinth:fabulously-optimized", "modrinth", "fabulously-optimized", null)]
    [InlineData("curseforge:396246@5000000", "curseforge", "396246", "5000000")]
    [InlineData("technic:tekkit", "technic", "tekkit", null)]
    [InlineData("C:\\packs\\x.zip", null, "C:\\packs\\x.zip", null)]
    public void ShortForms(string input, string? scheme, string id, string? version) =>
        Assert.Equal((scheme, id, version), ModpackService.SplitShortForm(input));

    [Theory]
    [InlineData("https://www.technicpack.net/modpack/tekkit-legends.736902", "tekkit-legends")]
    [InlineData("http://api.technicpack.net/modpack/attack-of-the-bteam?build=x", "attack-of-the-bteam")]
    [InlineData("hexxit", "hexxit")]
    public void TechnicSlugs(string input, string slug) => Assert.Equal(slug, TechnicClient.SlugFrom(input));
}

public class ModpackInstallTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ModpackInstaller Installer() => new(new DownloadEngine(new HttpClient()), new MirrorTable(DownloadSourceKind.Official));

    private static byte[] FabricMod(string id, string version) => Jar.Build(("fabric.mod.json", $$"""{"schemaVersion":1,"id":"{{id}}","version":"{{version}}"}"""));

    private static PackManifest Pack(TempFolder tmp, string name, params (string Path, object Content)[] files)
    {
        var archive = tmp.File(name, Jar.Build([("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"VER","name":"P","files":[],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.9"}}""".Replace("VER", name, StringComparison.Ordinal)),
            .. files.Select(f => ("overrides/" + f.Path, f.Content))]));
        return ModpackReader.Read(archive);
    }

    [Fact]
    public async Task Update_KeepsUserChanges_RemovesUntouched_AndKeepsDisabledMods()
    {
        using var tmp = new TempFolder();
        var layout = new FluoriteLayout(System.IO.Path.Combine(tmp.Path, "root"));
        var v1 = Pack(tmp, "v1.mrpack",
            ("config/a.cfg", "a1"), ("config/b.cfg", "b1"), ("config/c.cfg", "c1"),
            ("mods/foo-1.0.jar", FabricMod("foo", "1.0")), ("mods/bar-1.0.jar", FabricMod("bar", "1.0")));
        var instance = await ModpackInstaller.CreateInstanceAsync(layout, v1, ct: Ct);
        var installer = Installer();
        await installer.InstallAsync(v1, instance, null, ct: Ct);
        var game = instance.GameDirectory;
        Assert.Equal("a1", File.ReadAllText(System.IO.Path.Combine(game, "config", "a.cfg")));
        Assert.Equal(5, PackState.Load(instance.MetadataDirectory)!.Files.Count);

        // The user edits b, disables foo and adds their own file.
        File.WriteAllText(System.IO.Path.Combine(game, "config", "b.cfg"), "mine");
        File.Move(System.IO.Path.Combine(game, "mods", "foo-1.0.jar"), System.IO.Path.Combine(game, "mods", "foo-1.0.jar.disabled"));
        File.WriteAllText(System.IO.Path.Combine(game, "config", "user.cfg"), "u");

        var v2 = Pack(tmp, "v2.mrpack",
            ("config/a.cfg", "a2"), ("config/b.cfg", "b2"), ("config/d.cfg", "d2"), ("config/user.cfg", "pack's"),
            ("mods/foo-2.0.jar", FabricMod("foo", "2.0")), ("mods/bar-1.0.jar", FabricMod("bar", "1.0")));
        var plan = await ModpackInstaller.PlanUpdateAsync(instance, v2, ct: Ct);
        var kinds = plan.Changes.ToDictionary(c => c.Path, c => c.Kind);
        Assert.Equal(PackChangeKind.Replace, kinds["config/a.cfg"]);
        Assert.Equal(PackChangeKind.Modified, kinds["config/b.cfg"]);
        Assert.Equal(PackChangeKind.Remove, kinds["config/c.cfg"]);
        Assert.Equal(PackChangeKind.Add, kinds["config/d.cfg"]);
        Assert.Equal(PackChangeKind.Conflict, kinds["config/user.cfg"]);
        Assert.Equal(PackChangeKind.Remove, kinds["mods/foo-1.0.jar"]);
        Assert.Equal(PackChangeKind.Add, kinds["mods/foo-2.0.jar"]);
        Assert.False(kinds.ContainsKey("mods/bar-1.0.jar"));
        Assert.Equal(1, plan.Unchanged);

        var saved = 0;
        await installer.ApplyUpdateAsync(plan, instance, null, _ => { saved++; return Task.CompletedTask; }, ct: Ct);
        Assert.Equal(1, saved);
        Assert.Equal("a2", File.ReadAllText(System.IO.Path.Combine(game, "config", "a.cfg")));
        Assert.Equal("mine", File.ReadAllText(System.IO.Path.Combine(game, "config", "b.cfg")));
        Assert.False(File.Exists(System.IO.Path.Combine(game, "config", "c.cfg")));
        Assert.Equal("d2", File.ReadAllText(System.IO.Path.Combine(game, "config", "d.cfg")));
        Assert.Equal("u", File.ReadAllText(System.IO.Path.Combine(game, "config", "user.cfg")));
        Assert.False(File.Exists(System.IO.Path.Combine(game, "mods", "foo-1.0.jar.disabled")));
        Assert.True(File.Exists(System.IO.Path.Combine(game, "mods", "foo-2.0.jar.disabled")));
        Assert.Equal("v2.mrpack", PackState.Load(instance.MetadataDirectory)!.Version);

        // A second update with nothing new keeps the user's b.cfg flagged as modified, not overwritten.
        var again = await ModpackInstaller.PlanUpdateAsync(instance, v2, ct: Ct);
        Assert.All(again.Changes, c => Assert.False(c.Apply));
    }

    [Fact]
    public async Task Export_RoundTrips_WithoutPlatforms()
    {
        using var tmp = new TempFolder();
        var layout = new FluoriteLayout(System.IO.Path.Combine(tmp.Path, "root"));
        var source = await layout.CreateAsync(new InstanceManifest
        {
            Name = "Exported",
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.21.1" }, new ComponentRef { Uid = ComponentUids.NeoForge, Version = "21.1.80" }],
            Settings = new InstanceSettings { MaxMemoryMb = 5000 },
        }, Ct);
        var game = source.GameDirectory;
        Directory.CreateDirectory(System.IO.Path.Combine(game, "mods"));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "config", "deep"));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "logs"));
        File.WriteAllBytes(System.IO.Path.Combine(game, "mods", "m.jar"), FabricMod("m", "1"));
        File.WriteAllText(System.IO.Path.Combine(game, "mods", "off.jar.disabled"), "x");
        File.WriteAllText(System.IO.Path.Combine(game, "config", "deep", "c.json"), "{}");
        File.WriteAllText(System.IO.Path.Combine(game, "logs", "latest.log"), "log");
        File.WriteAllText(System.IO.Path.Combine(game, "options.txt"), "personal");

        var selection = ModpackExporter.DefaultSelection(game);
        Assert.DoesNotContain(selection, c => c.Name == "logs");
        Assert.False(selection.Single(c => c.Name == "options.txt").IncludedByDefault);
        var include = selection.Where(c => c.IncludedByDefault).Select(c => c.Name).ToList();

        using var content = new ContentService([], new DownloadEngine(new HttpClient()), new MirrorTable(DownloadSourceKind.Official));
        var exporter = new ModpackExporter(content);
        foreach (var format in new[] { PackFormat.Modrinth, PackFormat.CurseForge, PackFormat.MultiMC })
        {
            var output = System.IO.Path.Combine(tmp.Path, $"out-{format}.zip");
            await exporter.ExportAsync(source, new PackExportOptions { Format = format, Name = "Round Trip", Include = include }, output, Ct);
            var back = ModpackReader.Read(output);
            Assert.Equal(("1.21.1", ComponentUids.NeoForge, "21.1.80"), (back.MinecraftVersion, back.LoaderUid, back.LoaderVersion));
            var expected = format == PackFormat.MultiMC ? new[] { "config/deep/c.json", "mods/m.jar", "mods/off.jar.disabled" } : ["config/deep/c.json", "mods/m.jar"];
            Assert.Equal(expected, back.Overrides.Select(o => o.Path).Order());
            if (format == PackFormat.MultiMC)
                Assert.Equal(5000, back.Settings.MaxMemoryMb);
        }

        var wiz = System.IO.Path.Combine(tmp.Path, "packwiz");
        await exporter.ExportAsync(source, new PackExportOptions { Format = PackFormat.Packwiz, Name = "Wiz", Include = include }, wiz, Ct);
        var read = await PackwizReader.ReadAsync(PackwizReader.NormalizeLocation(wiz), new HttpClient(), null, Ct);
        Assert.Equal(("1.21.1", ComponentUids.NeoForge), (read.MinecraftVersion, read.LoaderUid));
        Assert.Equal(["config/deep/c.json", "mods/m.jar"], read.Files.Select(f => f.Path).Order());
        Assert.All(read.Files, f => Assert.Equal("sha256", f.OtherHash!.Value.Algorithm));

        // Installing the local packwiz folder copies its own files and checks their sha256.
        var target = await ModpackInstaller.CreateInstanceAsync(layout, read, ct: Ct);
        await Installer().InstallAsync(read, target, null, ct: Ct);
        Assert.Equal("{}", File.ReadAllText(System.IO.Path.Combine(target.GameDirectory, "config", "deep", "c.json")));
    }

    [Fact]
    public async Task Export_TakesPathsInsideFolders_EachFileOnce_AndNothingOutside()
    {
        using var tmp = new TempFolder();
        var layout = new FluoriteLayout(System.IO.Path.Combine(tmp.Path, "root"));
        var source = await layout.CreateAsync(new InstanceManifest
        {
            Name = "Picked",
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.21.1" }, new ComponentRef { Uid = ComponentUids.LiteLoader, Version = "1.12.2" }],
        }, Ct);
        var game = source.GameDirectory;
        Directory.CreateDirectory(System.IO.Path.Combine(game, "saves", "Keep", "region"));
        Directory.CreateDirectory(System.IO.Path.Combine(game, "saves", "Skip"));
        File.WriteAllText(System.IO.Path.Combine(game, "saves", "Keep", "level.dat"), "k");
        File.WriteAllText(System.IO.Path.Combine(game, "saves", "Keep", "region", "r.0.0.mca"), "r");
        File.WriteAllText(System.IO.Path.Combine(game, "saves", "Skip", "level.dat"), "s");

        using var content = new ContentService([], new DownloadEngine(new HttpClient()), new MirrorTable(DownloadSourceKind.Official));
        var exporter = new ModpackExporter(content);
        var output = System.IO.Path.Combine(tmp.Path, "picked.mrpack");
        // A world on its own, and a file inside it listed again.
        var result = await exporter.ExportAsync(source, new PackExportOptions
        {
            Format = PackFormat.Modrinth, Name = "Picked", Summary = "", Include = ["saves/Keep", "saves/Keep/level.dat"],
        }, output, Ct);
        Assert.Equal(["saves/Keep/level.dat", "saves/Keep/region/r.0.0.mca"], ModpackReader.Read(output).Overrides.Select(o => o.Path).Order());
        Assert.Equal(2, result.Bundled);
        Assert.Single(result.Warnings); // LiteLoader: Modrinth packs can't say so.
        using (var zip = ZipFile.OpenRead(output))
        {
            using var reader = new StreamReader(zip.GetEntry("modrinth.index.json")!.Open());
            Assert.DoesNotContain("summary", reader.ReadToEnd(), StringComparison.Ordinal); // Empty: left out, not null.
        }

        await Assert.ThrowsAsync<InvalidDataException>(() => exporter.ExportAsync(source, new PackExportOptions
        {
            Format = PackFormat.MultiMC, Name = "Escape", Include = ["../.."],
        }, System.IO.Path.Combine(tmp.Path, "escape.zip"), Ct));
    }

    [Theory]
    [InlineData(PackFormat.Modrinth, ComponentUids.LiteLoader, true)]
    [InlineData(PackFormat.Modrinth, ComponentUids.NeoForge, false)]
    [InlineData(PackFormat.CurseForge, ComponentUids.LegacyFabric, true)]
    [InlineData(PackFormat.Packwiz, ComponentUids.LiteLoader, false)]
    [InlineData(PackFormat.MultiMC, ComponentUids.LiteLoader, false)]
    public void UnsupportedLoader_SaysWhatAFormatCantHold(PackFormat format, string loader, bool unsupported)
    {
        var manifest = new InstanceManifest
        {
            Name = "L",
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.12.2" }, new ComponentRef { Uid = loader, Version = "1" }],
        };
        Assert.Equal(unsupported ? loader : null, ModpackExporter.UnsupportedLoader(manifest, format));
        Assert.Null(ModpackExporter.UnsupportedLoader(new InstanceManifest { Name = "Vanilla" }, format));
    }

    [Fact]
    public void ExportState_InnermostChoiceWins()
    {
        var state = new PackExportState { Included = ["mods", "saves/Keep"], Excluded = ["mods/big.jar", "saves"] };
        Assert.True(state.WasIncluded("mods/small.jar"));
        Assert.False(state.WasIncluded("mods/big.jar"));
        Assert.True(state.WasIncluded("saves/Keep/level.dat"));
        Assert.False(state.WasIncluded("saves/Other"));
        Assert.Null(state.WasIncluded("config"));
        Assert.Null(state.WasIncluded("modsextra")); // Not inside "mods".
        Assert.True(state.HasChoicesInside("saves"));
        Assert.False(state.HasChoicesInside("saves/Keep"));
    }

    [Fact]
    public async Task ExportState_RoundTrips()
    {
        using var tmp = new TempFolder();
        var at = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        await new PackExportState { Format = PackFormat.Packwiz, Name = "N", Version = "2.0", Author = "A", ExportedAt = at, Included = ["mods"], Excluded = ["saves"] }.SaveAsync(tmp.Path, Ct);
        var back = PackExportState.Load(tmp.Path)!;
        Assert.Equal((PackFormat.Packwiz, "N", "2.0", "A", at), (back.Format, back.Name, back.Version, back.Author, back.ExportedAt));
        Assert.Equal(["mods"], back.Included);
        Assert.Equal(["saves"], back.Excluded);
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, PackExportState.FileName), "{ not json");
        Assert.Null(PackExportState.Load(tmp.Path));
    }

    [Fact]
    public async Task Packwiz_Metafiles()
    {
        using var tmp = new TempFolder();
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, "pack.toml"), "name = \"W\"\nversion = \"3\"\n[index]\nfile = \"index.toml\"\nhash-format = \"sha256\"\nhash = \"x\"\n[versions]\nminecraft = \"1.20.1\"\nforge = \"47.2.0\"\n");
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, "index.toml"), "hash-format = \"sha256\"\n[[files]]\nfile = \"mods/sodium.pw.toml\"\nhash = \"h\"\nmetafile = true\n[[files]]\nfile = \"mods/server.pw.toml\"\nhash = \"h\"\nmetafile = true\n[[files]]\nfile = \"../../escape.txt\"\nhash = \"h\"\n");
        Directory.CreateDirectory(System.IO.Path.Combine(tmp.Path, "mods"));
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, "mods", "sodium.pw.toml"), "name = \"Sodium\"\nfilename = \"sodium.jar\"\nside = \"client\"\n[download]\nurl = \"https://cdn.modrinth.com/s.jar\"\nhash-format = \"sha1\"\nhash = \"ABC\"\n[update.modrinth]\nmod-id = \"AANobbMI\"\nversion = \"v1\"\n[option]\noptional = true\n");
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, "mods", "server.pw.toml"), "name = \"S\"\nfilename = \"s.jar\"\nside = \"server\"\n[download]\nurl = \"https://x/s.jar\"\nhash-format = \"sha512\"\nhash = \"d\"\n");
        var m = await PackwizReader.ReadAsync(PackwizReader.NormalizeLocation(tmp.Path), new HttpClient(), null, Ct);
        Assert.Equal((ComponentUids.Forge, "47.2.0", "3"), (m.LoaderUid, m.LoaderVersion, m.Version));
        var sodium = m.Files.Single(f => f.Path == "mods/sodium.jar");
        Assert.Equal(("ABC", PackSide.Optional, ContentPlatform.Modrinth, "AANobbMI"), (sodium.Sha1, sodium.Client, sodium.Source!.Platform, sodium.Source.ProjectId));
        Assert.Equal(PackSide.Unsupported, m.Files.Single(f => f.Path == "mods/s.jar").Client);
        Assert.Equal(2, m.Files.Count);
    }
}

public class SidecarTests
{
    [Fact]
    public void DiffAndOverlay_OnlyStoreFluoriteChanges()
    {
        var native = new InstanceManifest { Name = "N", Settings = new InstanceSettings { MaxMemoryMb = 4096 }, Stats = new InstanceStats { TotalPlaySeconds = 100 } };
        var effective = new InstanceManifest { Name = "N", Settings = new InstanceSettings { MaxMemoryMb = 4096, JvmArguments = "-X" }, Stats = new InstanceStats { TotalPlaySeconds = 160, LaunchCount = 1, LastPlayed = DateTimeOffset.UnixEpoch.AddYears(60) } };
        var sidecar = InstanceSidecar.Diff(effective, native, new InstanceSidecar());
        Assert.Null(sidecar.Name);
        Assert.Null(sidecar.Settings.MaxMemoryMb);
        Assert.Equal("-X", sidecar.Settings.JvmArguments);
        Assert.Equal(60, sidecar.Stats.TotalPlaySeconds);

        var reread = new InstanceManifest { Name = "N", Settings = new InstanceSettings { MaxMemoryMb = 8192 }, Stats = new InstanceStats { TotalPlaySeconds = 500 } };
        sidecar.ApplyTo(reread);
        Assert.Equal(8192, reread.Settings.MaxMemoryMb); // The other launcher's later change still shows.
        Assert.Equal("-X", reread.Settings.JvmArguments);
        Assert.Equal(560, reread.Stats.TotalPlaySeconds);
    }

    [Fact]
    public void FolderName_IsSafeAndStable()
    {
        var a = InstanceSidecar.FolderName("My: Pack/1");
        Assert.Equal(a, InstanceSidecar.FolderName("My: Pack/1"));
        Assert.NotEqual(a, InstanceSidecar.FolderName("My: Pack/2"));
        Assert.DoesNotContain(':', a);
        Assert.DoesNotContain('/', a);
    }
}
