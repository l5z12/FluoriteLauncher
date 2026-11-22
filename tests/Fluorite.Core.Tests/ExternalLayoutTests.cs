using System.Text.Json.Nodes;
using Fluorite.Core.Instances;
using Fluorite.Core.Settings;

namespace Fluorite.Core.Tests;

public class IniFileTests
{
    [Fact]
    public void Prism_Sectioned_QuotedAndEscaped()
    {
        var ini = IniFile.Parse("[General]\nConfigVersion=1.2\nname=My Pack\nJvmArgs=\"-Dfoo=a,b -Dbar=\\\"x\\\"\"\nnotes=line1\\nline2\n\n[UI]\nmods_Page\\Columns=AAAA\n");
        Assert.True(ini.IsSectioned);
        Assert.Equal("My Pack", ini.Get("name"));
        Assert.Equal("-Dfoo=a,b -Dbar=\"x\"", ini.Get("JvmArgs"));
        Assert.Equal("line1\nline2", ini.Get("notes"));
        Assert.Equal("AAAA", ini.Get("UI/mods_Page\\Columns"));
    }

    [Fact]
    public void Set_KeepsOtherLinesAndInsertsIntoGeneral()
    {
        const string text = "[General]\nname=Old\nnotes=\n\n[UI]\nx=1";
        var ini = IniFile.Parse(text);
        ini.Set("name", "New, name");
        ini.Set("MaxMemAlloc", "4096");
        var round = IniFile.Parse(ini.ToString());
        Assert.Equal("New, name", round.Get("name"));
        Assert.Equal("4096", round.Get("MaxMemAlloc"));
        Assert.Equal("1", round.Get("UI/x"));
        var lines = ini.ToString().Split('\n');
        Assert.True(Array.IndexOf(lines, "MaxMemAlloc=4096") < Array.IndexOf(lines, "[UI]"));
        Assert.Contains("notes=", lines);
    }

    [Fact]
    public void OldMultiMCFormat_HasNoSections()
    {
        var ini = IniFile.Parse("InstanceType=OneSix\nname=Legacy\n");
        Assert.False(ini.IsSectioned);
        ini.Set("notes", "hi");
        Assert.Equal("hi", IniFile.Parse(ini.ToString()).Get("notes"));
    }
}

public class VersionProfileInspectorTests
{
    private static void Write(GameDirectories store, string id, string json)
    {
        Directory.CreateDirectory(store.VersionDir(id));
        File.WriteAllText(store.VersionJson(id), json);
    }

    [Theory]
    [InlineData("""{"id":"fabric-loader-0.16.9-1.21.4","inheritsFrom":"1.21.4","libraries":[{"name":"net.fabricmc:intermediary:1.21.4"},{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""", "1.21.4", ComponentUids.FabricLoader, "0.16.9")]
    [InlineData("""{"id":"1.20.1-forge-47.2.0","inheritsFrom":"1.20.1","libraries":[{"name":"net.minecraftforge:fmlloader:1.20.1-47.2.0"},{"name":"net.minecraftforge:forge:1.20.1-47.2.0:universal"}]}""", "1.20.1", ComponentUids.Forge, "47.2.0")]
    [InlineData("""{"id":"neoforge-21.1.77","inheritsFrom":"1.21.1","libraries":[{"name":"net.neoforged:neoforge:21.1.77:universal"}]}""", "1.21.1", ComponentUids.NeoForge, "21.1.77")]
    [InlineData("""{"id":"1.20.1-NeoForge","libraries":[{"name":"net.neoforged:forge:1.20.1-47.1.106"}],"patches":[{"id":"game","version":"1.20.1"}]}""", "1.20.1", ComponentUids.NeoForge, "47.1.106")]
    [InlineData("""{"id":"1.7.10-Forge10.13.4.1614-1.7.10","libraries":[{"name":"net.minecraftforge:forge:1.7.10-10.13.4.1614-1.7.10"}]}""", "1.7.10", ComponentUids.Forge, "10.13.4.1614")]
    [InlineData("""{"id":"quilt-loader-0.26.4-1.21","inheritsFrom":"1.21","libraries":[{"name":"org.quiltmc:quilt-loader:0.26.4"}]}""", "1.21", ComponentUids.QuiltLoader, "0.26.4")]
    [InlineData("""{"id":"1.12.2-LiteLoader1.12.2","inheritsFrom":"1.12.2","libraries":[{"name":"com.mumfrey:liteloader:1.12.2-SNAPSHOT"}]}""", "1.12.2", ComponentUids.LiteLoader, "1.12.2-SNAPSHOT")]
    public void DetectsLoader(string json, string mc, string uid, string version)
    {
        using var tmp = new TempDir();
        var store = new GameDirectories(tmp.Path);
        var id = JsonNode.Parse(json)!["id"]!.GetValue<string>();
        Write(store, id, json);
        var p = VersionProfileInspector.Inspect(store, id);
        Assert.Equal((mc, uid, version), (p.MinecraftVersion, p.LoaderUid, p.LoaderVersion));
        var components = VersionProfileInspector.ComponentsFor(p);
        Assert.Equal(id, ComponentResolver.LaunchVersionId(new InstanceManifest { Components = components }));
    }

    [Fact]
    public void VanillaCopyWithCustomName_LaunchesItsOwnJson()
    {
        using var tmp = new TempDir();
        var store = new GameDirectories(tmp.Path);
        Write(store, "1.21.4", """{"id":"1.21.4"}""");
        Write(store, "My 1.21.4", """{"id":"My 1.21.4","inheritsFrom":"1.21.4"}""");
        var components = VersionProfileInspector.ComponentsFor(VersionProfileInspector.Inspect(store, "My 1.21.4"));
        var m = new InstanceManifest { Components = components };
        Assert.Equal("1.21.4", m.MinecraftVersion);
        Assert.Null(ComponentResolver.PrimaryLoader(m));
        Assert.Equal("My 1.21.4", ComponentResolver.LaunchVersionId(m));
    }
}

public class ExternalLayoutTests
{
    private static ExternalLayoutContext Context(TempDir tmp, string rootId, bool writeBack = false) =>
        new(rootId, OtherLauncher.PrismLauncher, Path.Combine(tmp.Path, "fluorite", "external", rootId), new GameDirectories(Path.Combine(tmp.Path, "fluorite")), writeBack);

    private static string MakePrism(TempDir tmp)
    {
        var root = Path.Combine(tmp.Path, "prism");
        var inst = Path.Combine(root, "instances", "Pack A");
        Directory.CreateDirectory(Path.Combine(inst, ".minecraft", "mods"));
        File.WriteAllText(Path.Combine(root, "prismlauncher.cfg"), "[General]\nInstanceDir=instances\n");
        File.WriteAllText(Path.Combine(inst, "instance.cfg"),
            "[General]\nConfigVersion=1.2\nInstanceType=OneSix\nname=Pack A\nOverrideMemory=true\nMaxMemAlloc=6144\nOverrideJavaArgs=false\nJvmArgs=-Dignored=1\nlastLaunchTime=1700000000000\ntotalTimePlayed=120\n[UI]\nx=1\n");
        File.WriteAllText(Path.Combine(inst, "mmc-pack.json"), """
            {"components":[{"uid":"org.lwjgl3","version":"3.3.3","dependencyOnly":true},{"uid":"net.minecraft","version":"1.21.1","important":true},
             {"uid":"net.fabricmc.intermediary","version":"1.21.1","dependencyOnly":true},{"uid":"net.fabricmc.fabric-loader","version":"0.16.9"},
             {"uid":"custom.thing","cachedName":"Some Patch","version":"1"}],"formatVersion":1}
            """);
        File.WriteAllText(Path.Combine(root, "instances", "instgroups.json"), """{"formatVersion":"1","groups":{"Modded":{"hidden":false,"instances":["Pack A"]}}}""");
        return root;
    }

    [Fact]
    public async Task Prism_MapsComponentsSettingsGroupsAndStats()
    {
        using var tmp = new TempDir();
        var layout = new MultiMCLayout(MakePrism(tmp), Context(tmp, "prism"));
        var i = Assert.Single(await layout.GetInstancesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("prism:Pack A", i.Id);
        Assert.EndsWith(Path.Combine("Pack A", ".minecraft"), i.GameDirectory); // Only .minecraft exists.
        Assert.Equal("1.21.1", i.Manifest.MinecraftVersion);
        Assert.Equal(ComponentUids.FabricLoader, ComponentResolver.PrimaryLoader(i.Manifest)!.Uid);
        Assert.DoesNotContain(i.Manifest.Components, c => c.Uid is "org.lwjgl3" or "net.fabricmc.intermediary");
        Assert.Equal(6144, i.Manifest.Settings.MaxMemoryMb);
        Assert.Null(i.Manifest.Settings.JvmArguments); // Not overridden in Prism.
        Assert.Equal("Modded", i.Manifest.Group);
        Assert.Equal(120, i.Manifest.Stats.TotalPlaySeconds);
        Assert.Contains(i.Warnings, w => w.Contains("Some Patch", StringComparison.Ordinal));
        Assert.False(i.CanEditComponents);
        Assert.StartsWith(Path.Combine(tmp.Path, "fluorite", "external", "prism"), i.MetadataDirectory);
    }

    [Fact]
    public async Task Prism_Icons_CustomFolderSubfoldersSvgAndBuiltIns()
    {
        using var tmp = new TempDir();
        var root = MakePrism(tmp);
        File.WriteAllText(Path.Combine(root, "prismlauncher.cfg"), "[General]\nInstanceDir=instances\nIconsDir=my icons\n");
        Directory.CreateDirectory(Path.Combine(root, "my icons", "packs"));
        File.WriteAllText(Path.Combine(root, "my icons", "packs", "lighthearted.jpg"), "x");
        File.WriteAllText(Path.Combine(root, "my icons", "logo.png"), "x");
        File.WriteAllText(Path.Combine(root, "my icons", "logo.svg"), "<svg/>");
        Directory.CreateDirectory(Path.Combine(root, "icons"));
        File.WriteAllText(Path.Combine(root, "icons", "logo.png"), "x"); // Not the configured folder.
        var cfg = Path.Combine(root, "instances", "Pack A", "instance.cfg");
        var ct = TestContext.Current.CancellationToken;

        async Task<InstanceManifest> With(string key)
        {
            File.WriteAllText(cfg, $"[General]\nInstanceType=OneSix\nname=Pack A\niconKey={key}\n");
            return Assert.Single(await new MultiMCLayout(root, Context(tmp, "prism")).GetInstancesAsync(ct)).Manifest;
        }

        Assert.Equal(Path.Combine(root, "my icons", "packs", "lighthearted.jpg"), (await With("lighthearted")).Icon); // One folder down.
        Assert.Equal(Path.Combine(root, "my icons", "logo.svg"), (await With("logo")).Icon); // SVG preferred.
        var builtIn = await With("diamond");
        Assert.Equal((null, "diamond"), (builtIn.Icon, builtIn.IconKey)); // No file: kept by name.
        var missing = await With("gone");
        Assert.Equal((null, null), (missing.Icon, missing.IconKey));
        Assert.Equal((null, null), ((await With("default")).Icon, (await With("default")).IconKey));
    }

    [Fact]
    public async Task Prism_ReadOnly_ChangesGoToSidecar()
    {
        using var tmp = new TempDir();
        var root = MakePrism(tmp);
        var cfg = Path.Combine(root, "instances", "Pack A", "instance.cfg");
        var before = File.ReadAllText(cfg);
        var layout = new MultiMCLayout(root, Context(tmp, "prism"));
        var i = Assert.Single(await layout.GetInstancesAsync(TestContext.Current.CancellationToken));
        i.Manifest.Settings.JvmArguments = "-XX:+UseZGC";
        i.Manifest.Stats.TotalPlaySeconds += 60;
        i.Manifest.Stats.LastPlayed = DateTimeOffset.UtcNow;
        ComponentResolver.PrimaryLoader(i.Manifest)!.ProfileId = "fabric-loader-0.16.9-1.21.1";
        await layout.SaveAsync(i, TestContext.Current.CancellationToken);

        Assert.Equal(before, File.ReadAllText(cfg));
        var again = Assert.Single(await new MultiMCLayout(root, Context(tmp, "prism")).GetInstancesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("-XX:+UseZGC", again.Manifest.Settings.JvmArguments);
        Assert.Equal(180, again.Manifest.Stats.TotalPlaySeconds);
        Assert.Equal("fabric-loader-0.16.9-1.21.1", ComponentResolver.PrimaryLoader(again.Manifest)!.ProfileId);
        Assert.Equal(6144, again.Manifest.Settings.MaxMemoryMb); // Still Prism's own.
    }

    [Fact]
    public async Task Prism_WriteBack_UpdatesCfgAndPack()
    {
        using var tmp = new TempDir();
        var root = MakePrism(tmp);
        var layout = new MultiMCLayout(root, Context(tmp, "prism", writeBack: true));
        var i = Assert.Single(await layout.GetInstancesAsync(TestContext.Current.CancellationToken));
        Assert.True(i.CanEditComponents);
        i.Manifest.Name = "Renamed";
        i.Manifest.Settings.MaxMemoryMb = 8192;
        i.Manifest.Components.First(c => c.Uid == ComponentUids.Minecraft).Version = "1.21.4";
        await layout.SaveAsync(i, TestContext.Current.CancellationToken);

        var cfg = IniFile.Load(Path.Combine(root, "instances", "Pack A", "instance.cfg"));
        Assert.Equal("Renamed", cfg.Get("name"));
        Assert.Equal("8192", cfg.Get("MaxMemAlloc"));
        Assert.Equal("1", cfg.Get("UI/x")); // Other sections untouched.
        var pack = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "instances", "Pack A", "mmc-pack.json")))!["components"]!.AsArray();
        Assert.Equal("1.21.4", pack.Single(c => c!["uid"]!.GetValue<string>() == "net.minecraft")!["version"]!.GetValue<string>());
        Assert.Equal("1.21.4", pack.Single(c => c!["uid"]!.GetValue<string>() == "net.fabricmc.intermediary")!["version"]!.GetValue<string>());
        Assert.Contains(pack, c => c!["uid"]!.GetValue<string>() == "custom.thing"); // Unknown components kept.
    }

    [Fact]
    public async Task Official_ProfilesBecomeInstances()
    {
        using var tmp = new TempDir();
        var mc = Path.Combine(tmp.Path, ".minecraft");
        var store = new GameDirectories(mc);
        Directory.CreateDirectory(store.VersionDir("fabric-loader-0.16.9-1.21.4"));
        File.WriteAllText(store.VersionJson("fabric-loader-0.16.9-1.21.4"), """{"id":"fabric-loader-0.16.9-1.21.4","inheritsFrom":"1.21.4","libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9"}]}""");
        File.WriteAllText(Path.Combine(mc, "versions", "version_manifest_v2.json"), """{"latest":{"release":"26.3","snapshot":"26.4-snapshot-7"},"versions":[]}""");
        File.WriteAllText(Path.Combine(mc, "launcher_profiles.json"), """
            {"profiles":{
              "a":{"name":"Fabric","type":"custom","lastVersionId":"fabric-loader-0.16.9-1.21.4","gameDir":"profiles/fabric","javaArgs":"-Xmx3G","resolution":{"width":1280,"height":720},"lastUsed":"2026-01-02T03:04:05.000Z"},
              "b":{"name":"","type":"latest-snapshot","lastVersionId":"latest-snapshot"},
              "c":{"name":"Not installed","type":"custom","lastVersionId":"1.8.9"}}}
            """);
        var layout = new OfficialLayout(mc, Context(tmp, "official") with { Launcher = OtherLauncher.Official });
        var all = (await layout.GetInstancesAsync(TestContext.Current.CancellationToken)).ToDictionary(i => i.LocalId);
        Assert.Equal(Path.Combine(mc, "profiles", "fabric"), all["a"].GameDirectory);
        Assert.Equal("fabric-loader-0.16.9-1.21.4", all["a"].LaunchVersionId);
        Assert.Equal("-Xmx3G", all["a"].Manifest.Settings.JvmArguments);
        Assert.Equal(1280, all["a"].Manifest.Settings.WindowWidth);
        Assert.Equal(mc, all["a"].Store.Root);
        Assert.Equal("26.4-snapshot-7", all["b"].LaunchVersionId);
        Assert.Equal("Latest snapshot", all["b"].Name);
        Assert.Equal("1.8.9", all["c"].LaunchVersionId);
    }

    [Fact]
    public async Task Hmcl_IsolationFromCfgAndHeuristic()
    {
        using var tmp = new TempDir();
        var mc = Path.Combine(tmp.Path, ".minecraft");
        var store = new GameDirectories(mc);
        foreach (var id in new[] { "Iso", "Shared", "Guessed" })
        {
            Directory.CreateDirectory(store.VersionDir(id));
            File.WriteAllText(store.VersionJson(id), $$"""{"id":"{{id}}","inheritsFrom":"1.20.1","libraries":[{"name":"net.minecraftforge:forge:1.20.1-47.2.0"}]}""");
        }
        File.WriteAllText(Path.Combine(store.VersionDir("Iso"), "hmclversion.cfg"), """{"usesGlobal":false,"gameDirType":1,"maxMemory":5000,"autoMemory":false,"javaArgs":"-XX:+UseG1GC","width":1600,"height":900}""");
        File.WriteAllText(Path.Combine(store.VersionDir("Shared"), "hmclversion.cfg"), """{"usesGlobal":false,"gameDirType":0}""");
        Directory.CreateDirectory(Path.Combine(store.VersionDir("Guessed"), "saves"));
        var layout = new IsolatedLayout(mc, Context(tmp, "hmcl") with { Launcher = OtherLauncher.Hmcl });
        var all = (await layout.GetInstancesAsync(TestContext.Current.CancellationToken)).ToDictionary(i => i.LocalId);
        Assert.Equal(store.VersionDir("Iso"), all["Iso"].GameDirectory);
        Assert.Equal(5000, all["Iso"].Manifest.Settings.MaxMemoryMb);
        Assert.Equal(1600, all["Iso"].Manifest.Settings.WindowWidth);
        Assert.Equal(mc, all["Shared"].GameDirectory);
        Assert.Equal(store.VersionDir("Guessed"), all["Guessed"].GameDirectory);
        Assert.Equal(ComponentUids.Forge, ComponentResolver.PrimaryLoader(all["Iso"].Manifest)!.Uid);
        Assert.Equal("Iso", all["Iso"].LaunchVersionId);
        Assert.True(InstanceRegistry.IsIsolated(mc));
        // Instances sharing a game directory share the content index.
        Assert.NotEqual(all["Iso"].ContentDirectory, all["Shared"].ContentDirectory);
    }

    [Fact]
    public async Task Registry_DetectsKindsAndRoutesIds()
    {
        using var tmp = new TempDir();
        var prism = MakePrism(tmp);
        Assert.Equal((LayoutKind.MultiMC, OtherLauncher.PrismLauncher), Pick(InstanceRegistry.Detect(prism)));
        Assert.Equal((LayoutKind.MultiMC, OtherLauncher.PrismLauncher), Pick(InstanceRegistry.Detect(Path.Combine(prism, "instances"))));
        Assert.Equal(prism, InstanceRegistry.Detect(Path.Combine(prism, "instances"))!.Value.Path);

        var official = Path.Combine(tmp.Path, "off", ".minecraft");
        Directory.CreateDirectory(Path.Combine(official, "versions"));
        File.WriteAllText(Path.Combine(official, "launcher_profiles.json"), """{"profiles":{}}""");
        Assert.Equal((LayoutKind.Official, OtherLauncher.Official), Pick(InstanceRegistry.Detect(Path.Combine(tmp.Path, "off"))));
        Assert.Null(InstanceRegistry.Detect(Path.Combine(tmp.Path, "nothing-here")));

        var roots = new List<ExternalRoot> { new() { Id = "prism", Path = prism, Kind = LayoutKind.MultiMC, Launcher = OtherLauncher.PrismLauncher } };
        var registry = new InstanceRegistry(new FluoriteLayout(Path.Combine(tmp.Path, "fluorite")), () => roots);
        Assert.Equal("Pack A", (await registry.GetAsync("prism:Pack A", TestContext.Current.CancellationToken))!.Name);
        Assert.Single(await registry.GetInstancesAsync(TestContext.Current.CancellationToken));
        roots[0].Enabled = false;
        Assert.Empty(await registry.GetInstancesAsync(TestContext.Current.CancellationToken));
        Assert.Equal("prism-2", InstanceRegistry.ProposeId(OtherLauncher.PrismLauncher, ["prism"]));
    }

    private static (LayoutKind, OtherLauncher) Pick((LayoutKind Kind, OtherLauncher Launcher, string Path)? d) => (d!.Value.Kind, d.Value.Launcher);
}

public class InstanceIconTests
{
    private static InstanceRegistry Registry(TempDir tmp, string prismRoot, bool writeBack) =>
        new(new FluoriteLayout(Path.Combine(tmp.Path, "fluorite")),
            () => [new ExternalRoot { Id = "prism", Path = prismRoot, Kind = LayoutKind.MultiMC, Launcher = OtherLauncher.PrismLauncher, AllowWriteBack = writeBack }]);

    private static string MakePrism(TempDir tmp, string iconKey)
    {
        var root = Path.Combine(tmp.Path, "prism");
        var inst = Path.Combine(root, "instances", "Pack A");
        Directory.CreateDirectory(Path.Combine(inst, "minecraft"));
        File.WriteAllText(Path.Combine(root, "prismlauncher.cfg"), "[General]\nInstanceDir=instances\n");
        File.WriteAllText(Path.Combine(inst, "instance.cfg"), $"[General]\nInstanceType=OneSix\nname=Pack A\niconKey={iconKey}\n");
        File.WriteAllText(Path.Combine(inst, "mmc-pack.json"), """{"components":[{"uid":"net.minecraft","version":"1.21.1"}],"formatVersion":1}""");
        return root;
    }

    private static string Image(TempDir tmp, string name, string content)
    {
        var path = Path.Combine(tmp.Path, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<GameInstance> Prism(InstanceRegistry registry) =>
        Assert.Single(await registry.GetInstancesAsync(TestContext.Current.CancellationToken), i => i.IsExternal);

    [Fact]
    public async Task Prism_ReadOnly_IconStaysInFluoritesNotes()
    {
        using var tmp = new TempDir();
        var root = MakePrism(tmp, "diamond");
        var cfg = Path.Combine(root, "instances", "Pack A", "instance.cfg");
        var before = File.ReadAllText(cfg);
        var registry = Registry(tmp, root, writeBack: false);
        var ct = TestContext.Current.CancellationToken;

        await registry.SetIconAsync(await Prism(registry), Image(tmp, "mine.png", "a"), null, ct);
        var picked = (await Prism(registry)).Manifest;
        Assert.StartsWith(Path.Combine(tmp.Path, "fluorite", "external", "prism"), picked.Icon);
        Assert.Null(picked.IconKey);
        var copy = picked.Icon!;

        await registry.SetIconAsync(await Prism(registry), null, "grass", ct);
        var builtIn = (await Prism(registry)).Manifest;
        Assert.Equal((null, "grass"), (builtIn.Icon, builtIn.IconKey));
        Assert.False(File.Exists(copy)); // Replaced, and Fluorite's own: gone.

        await registry.SetIconAsync(await Prism(registry), null, null, ct);
        var none = (await Prism(registry)).Manifest;
        Assert.Equal((null, null), (none.Icon, none.IconKey)); // Over Prism's diamond.
        Assert.Equal(before, File.ReadAllText(cfg)); // Prism's files never touched.
    }

    [Fact]
    public async Task Prism_WriteBack_IconGoesToPrismsIconsFolder()
    {
        using var tmp = new TempDir();
        var root = MakePrism(tmp, "default");
        var cfg = Path.Combine(root, "instances", "Pack A", "instance.cfg");
        var registry = Registry(tmp, root, writeBack: true);
        var ct = TestContext.Current.CancellationToken;

        await registry.SetIconAsync(await Prism(registry), Image(tmp, "Mine.PNG", "a"), null, ct);
        var key = IniFile.Load(cfg).Get("iconKey");
        Assert.StartsWith("fluorite-", key);
        var icon = (await Prism(registry)).Manifest.Icon;
        Assert.Equal(Path.Combine(root, "icons", key + ".png"), icon); // Read back from Prism's own files.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(tmp.Path, "fluorite", "external"), "icon-*", SearchOption.AllDirectories)); // The staging copy is gone.

        await registry.SetIconAsync(await Prism(registry), null, "grass", ct);
        Assert.Equal("grass", IniFile.Load(cfg).Get("iconKey"));
        Assert.True(File.Exists(icon)); // Prism keeps its icons to pick again.
        await registry.SetIconAsync(await Prism(registry), null, null, ct);
        Assert.Equal("default", IniFile.Load(cfg).Get("iconKey"));
    }

    [Fact]
    public async Task Fluorite_IconIsCopiedIn_ReplacedOnesDeleted_DuplicatesGetTheirOwn()
    {
        using var tmp = new TempDir();
        var layout = new FluoriteLayout(Path.Combine(tmp.Path, "fluorite"));
        var registry = new InstanceRegistry(layout, () => []);
        var ct = TestContext.Current.CancellationToken;
        var instance = await layout.CreateAsync(new InstanceManifest { Name = "Mine", Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.21.1" }] }, ct);
        File.WriteAllText(Path.Combine(instance.Directory, "icon.png"), "pack");
        instance.Manifest.Icon = Path.Combine(instance.Directory, "icon.png"); // A modpack's icon.

        await registry.SetIconAsync(instance, Image(tmp, "a.png", "a"), null, ct);
        var first = instance.Manifest.Icon!;
        Assert.Equal(instance.Directory, Path.GetDirectoryName(first));
        Assert.False(File.Exists(Path.Combine(instance.Directory, "icon.png")));

        var copy = await layout.DuplicateAsync(instance, "Mine 2", ct);
        Assert.Equal(Path.Combine(copy.Directory, Path.GetFileName(first)), copy.Manifest.Icon);

        await registry.SetIconAsync(instance, Image(tmp, "b.svg", "<svg/>"), null, ct);
        Assert.EndsWith(".svg", instance.Manifest.Icon);
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(copy.Manifest.Icon)); // The duplicate's icon is its own.
        var reloaded = Assert.Single(await layout.GetInstancesAsync(ct), i => i.Id == instance.Id);
        Assert.Equal(instance.Manifest.Icon, reloaded.Manifest.Icon);
    }
}
