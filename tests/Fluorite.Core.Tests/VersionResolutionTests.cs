using System.Text.Json;
using Fluorite.Core.Json;
using Fluorite.Core.Launch;
using Fluorite.Core.Libraries;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class VersionResolutionTests
{
    private static VersionJson Parse(string json) =>
        JsonSerializer.Deserialize(json, FluoriteJsonContext.Default.VersionJson)!;

    private const string Vanilla = """
    {
      "id": "1.21.4", "type": "release", "mainClass": "net.minecraft.client.main.Main",
      "assetIndex": { "id": "19", "url": "https://piston-meta.mojang.com/v1/packages/x/19.json" },
      "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
      "downloads": { "client": { "sha1": "abc", "size": 10, "url": "https://piston-data.mojang.com/v1/objects/abc/client.jar" } },
      "arguments": {
        "game": ["--username", "${auth_player_name}", { "rules": [{ "action": "allow", "features": { "has_custom_resolution": true } }], "value": ["--width", "${resolution_width}", "--height", "${resolution_height}"] }],
        "jvm": [{ "rules": [{ "action": "allow", "os": { "name": "windows" } }], "value": "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump" }, "-cp", "${classpath}"]
      },
      "libraries": [
        { "name": "org.ow2.asm:asm:9.6", "downloads": { "artifact": { "path": "org/ow2/asm/asm/9.6/asm-9.6.jar", "sha1": "a", "size": 1, "url": "https://libraries.minecraft.net/org/ow2/asm/asm/9.6/asm-9.6.jar" } } },
        { "name": "ca.weblite:java-objc-bridge:1.1", "downloads": { "artifact": { "path": "ca/weblite/java-objc-bridge/1.1/java-objc-bridge-1.1.jar", "url": "https://libraries.minecraft.net/x.jar" } }, "rules": [{ "action": "allow", "os": { "name": "osx" } }] },
        { "name": "org.lwjgl:lwjgl:3.3.3:natives-windows-arm64", "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows-arm64.jar", "url": "https://libraries.minecraft.net/y.jar" } }, "rules": [{ "action": "allow", "os": { "name": "windows" } }] }
      ],
      "releaseTime": "2024-12-03T10:12:57+00:00"
    }
    """;

    private const string FabricProfile = """
    {
      "id": "fabric-loader-0.16.10-1.21.4", "inheritsFrom": "1.21.4", "type": "release",
      "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
      "arguments": { "game": [], "jvm": ["-DFabricMcEmu= net.minecraft.client.main.Main "] },
      "libraries": [
        { "name": "org.ow2.asm:asm:9.7.1", "url": "https://maven.fabricmc.net/", "sha1": "f0ed132a49244b042cd0e15702ab9f2ce3cc8436", "size": 126093 },
        { "name": "net.fabricmc:fabric-loader:0.16.10", "url": "https://maven.fabricmc.net/" }
      ]
    }
    """;

    [Fact]
    public void ArgumentEntries_ParseStringsObjectsAndArrays()
    {
        var v = Parse(Vanilla);
        Assert.Equal(3, v.Arguments!.Game!.Count);
        Assert.Equal(["--width", "${resolution_width}", "--height", "${resolution_height}"], v.Arguments.Game[2].Values);
        Assert.NotNull(v.Arguments.Game[2].Rules);
    }

    [Fact]
    public void Merge_FabricOverVanilla()
    {
        var merged = VersionMerger.Merge([Parse(Vanilla), Parse(FabricProfile)]);
        Assert.Equal("fabric-loader-0.16.10-1.21.4", merged.Id);
        Assert.Equal("1.21.4", merged.MinecraftVersion);
        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", merged.MainClass);
        Assert.Equal("1.21.4", merged.JarSourceId);
        Assert.Equal(21, merged.JavaVersion!.MajorVersion);
        Assert.Equal("19", merged.AssetIndexId);
        // Child libraries first.
        Assert.Equal("org.ow2.asm:asm:9.7.1", merged.Libraries[0].Name);
        // JVM args parent first, then child.
        Assert.Equal("-DFabricMcEmu= net.minecraft.client.main.Main ", merged.JvmArguments![^1].Values[0]);
    }

    [Fact]
    public void LibraryPlan_DedupesChildFirst_FiltersRules_DetectsArm64()
    {
        var merged = VersionMerger.Merge([Parse(Vanilla), Parse(FabricProfile)]);
        var dirs = new GameDirectories(@"C:\mc");
        var plan = LibraryPlanner.Plan(merged, dirs, RuleContext.ForCurrentOs(CpuArch.X64), MirrorTable.Official);

        var asm = plan.Classpath.Where(p => p.Contains(@"org\ow2\asm", StringComparison.Ordinal)).ToList();
        Assert.Single(asm);
        Assert.Contains("9.7.1", asm[0], StringComparison.Ordinal);
        Assert.DoesNotContain(plan.Classpath, p => p.Contains("objc", StringComparison.Ordinal));
        Assert.True(plan.HasArm64Natives);

        var loader = plan.Downloads.Single(d => d.DisplayName == "net.fabricmc:fabric-loader:0.16.10");
        Assert.Equal("https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.16.10/fabric-loader-0.16.10.jar", loader.Sources[0].ToString());
    }

    [Fact]
    public void LibraryPlan_WithoutDedupe_KeepsEveryVersion()
    {
        List<Library> libs =
        [
            new() { Name = "org.ow2.asm:asm-commons:9.1", Url = "https://maven.minecraftforge.net/" },
            new() { Name = "org.ow2.asm:asm-commons:9.5", Url = "https://maven.minecraftforge.net/" },
            new() { Name = "org.ow2.asm:asm-commons:9.5", Url = "https://maven.minecraftforge.net/" },
        ];
        var dirs = new GameDirectories(@"C:\mc");
        var ctx = RuleContext.ForCurrentOs(CpuArch.X64);
        Assert.Single(LibraryPlanner.Plan(libs, dirs, ctx, MirrorTable.Official).Downloads);
        Assert.Equal(2, LibraryPlanner.Plan(libs, dirs, ctx, MirrorTable.Official, deduplicate: false).Downloads.Count);
    }

    [Fact]
    public void LibraryPlan_LegacyNatives()
    {
        const string json = """
        {
          "id": "1.12.2", "mainClass": "net.minecraft.client.main.Main", "minecraftArguments": "--username ${auth_player_name}",
          "libraries": [
            { "name": "org.lwjgl.lwjgl:lwjgl-platform:2.9.4-nightly-20150209",
              "downloads": { "classifiers": {
                "natives-windows": { "path": "org/lwjgl/lwjgl/lwjgl-platform/2.9.4-nightly-20150209/lwjgl-platform-2.9.4-nightly-20150209-natives-windows.jar", "url": "https://libraries.minecraft.net/w.jar", "sha1": "b", "size": 2 },
                "natives-osx": { "path": "o.jar", "url": "https://libraries.minecraft.net/o.jar" } } },
              "natives": { "windows": "natives-windows", "osx": "natives-osx", "linux": "natives-linux" },
              "extract": { "exclude": ["META-INF/"] } },
            { "name": "tv.twitch:twitch-platform:6.5:natives-windows-${arch}", "natives": { "windows": "natives-windows-${arch}" } },
            { "name": "org.lwjgl:lwjgl:3.2.2",
              "downloads": { "artifact": { "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2.jar", "url": "https://libraries.minecraft.net/l.jar" },
                             "classifiers": { "natives-windows": { "path": "org/lwjgl/lwjgl/3.2.2/lwjgl-3.2.2-natives-windows.jar", "url": "https://libraries.minecraft.net/n.jar" } } },
              "natives": { "windows": "natives-windows" } }
          ]
        }
        """;
        var merged = VersionMerger.Merge([Parse(json)]);
        var plan = LibraryPlanner.Plan(merged, new GameDirectories(@"C:\mc"), RuleContext.ForCurrentOs(CpuArch.X64), MirrorTable.Official);

        // Natives-only lwjgl-platform is not on the classpath; lwjgl 3.2.2 (artifact + natives) is.
        Assert.DoesNotContain(plan.Classpath, p => p.Contains("lwjgl-platform", StringComparison.Ordinal));
        Assert.Contains(plan.Classpath, p => p.EndsWith("lwjgl-3.2.2.jar", StringComparison.Ordinal));
        Assert.Equal(3, plan.Natives.Count);
        Assert.Equal(["META-INF/"], plan.Natives[0].ExcludePrefixes);
        Assert.Contains(plan.Natives, n => n.Path.EndsWith("twitch-platform-6.5-natives-windows-64.jar", StringComparison.Ordinal));
    }

    [Fact]
    public void LibraryPlan_EmptyUrl_IsLocallyGenerated()
    {
        const string json = """
        { "id": "forge", "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
          "libraries": [ { "name": "net.minecraftforge:forge:1.20.1-47.2.0:client", "downloads": { "artifact": { "path": "net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-client.jar", "url": "", "sha1": "x" } } } ] }
        """;
        var plan = LibraryPlanner.Plan(VersionMerger.Merge([Parse(json)]), new GameDirectories(@"C:\mc"), RuleContext.ForCurrentOs(CpuArch.X64), MirrorTable.Official);
        Assert.Empty(plan.Downloads);
        Assert.Single(plan.LocallyGeneratedArtifacts);
    }

    private static LaunchOptions Options((int, int)? res = null, QuickPlay? qp = null) => new()
    {
        GameDirectory = @"C:\game",
        Session = new GameSession("Steve", "uuid", "token", "msa"),
        Resolution = res,
        QuickPlay = qp,
    };

    private static readonly Dictionary<string, string> Vars = new()
    {
        ["auth_player_name"] = "Steve",
        ["classpath"] = "a.jar;b.jar",
        ["resolution_width"] = "1280",
        ["resolution_height"] = "720",
        ["natives_directory"] = @"C:\natives",
        ["launcher_name"] = "FluoriteLauncher",
        ["launcher_version"] = "1.0",
    };

    [Fact]
    public void Arguments_Modern_FeatureGatedResolution()
    {
        var v = VersionMerger.Merge([Parse(Vanilla)]);
        var withRes = ArgumentBuilder.Build(v, RuleContext.ForCurrentOs(CpuArch.X64, new LaunchFeatures { HasCustomResolution = true }), Vars, ["-Xmx4096m"], null, Options((1280, 720)));
        Assert.Equal(["--username", "Steve", "--width", "1280", "--height", "720"], withRes.Game);
        Assert.Equal("-cp", withRes.Jvm[1]);
        Assert.Equal("a.jar;b.jar", withRes.Jvm[2]);
        Assert.Equal("-Xmx4096m", withRes.Jvm[3]);

        var without = ArgumentBuilder.Build(v, RuleContext.ForCurrentOs(CpuArch.X64), Vars, [], null, Options());
        Assert.Equal(["--username", "Steve"], without.Game);
    }

    [Fact]
    public void Arguments_Legacy_DefaultsAndExtras()
    {
        const string json = """{ "id": "1.7.10", "mainClass": "net.minecraft.client.main.Main", "minecraftArguments": "--username ${auth_player_name} --unknown ${not_a_var}" }""";
        var v = VersionMerger.Merge([Parse(json)]);
        var built = ArgumentBuilder.Build(v, RuleContext.ForCurrentOs(CpuArch.X64), Vars, [], "-Dlog4j.configurationFile=x.xml", Options((1280, 720), new QuickPlay(QuickPlayKind.Multiplayer, "mc.example.com:25570")));

        Assert.Contains("-Djava.library.path=C:\\natives", built.Jvm);
        Assert.Equal("-Dlog4j.configurationFile=x.xml", built.Jvm[^1]);
        Assert.Equal(["--username", "Steve", "--unknown", "${not_a_var}", "--width", "1280", "--height", "720", "--server", "mc.example.com", "--port", "25570"], built.Game);
    }

    [Theory]
    [InlineData(QuickPlayKind.Singleplayer, "Survival Island", "--quickPlaySingleplayer")]
    [InlineData(QuickPlayKind.Multiplayer, "mc.example.com:25570", "--quickPlayMultiplayer")]
    public void Arguments_QuickPlay_UsesTheVersionsRuleGatedArguments(QuickPlayKind kind, string target, string flag)
    {
        // Shaped like Mojang's 23w14a+ version JSONs.
        const string json = """
        { "id": "1.21.4", "mainClass": "net.minecraft.client.main.Main",
          "arguments": {
            "game": [ "--username", "${auth_player_name}",
              { "rules": [ { "action": "allow", "features": { "has_quick_plays_support": true } } ], "value": [ "--quickPlayPath", "${quickPlayPath}" ] },
              { "rules": [ { "action": "allow", "features": { "is_quick_play_singleplayer": true } } ], "value": [ "--quickPlaySingleplayer", "${quickPlaySingleplayer}" ] },
              { "rules": [ { "action": "allow", "features": { "is_quick_play_multiplayer": true } } ], "value": [ "--quickPlayMultiplayer", "${quickPlayMultiplayer}" ] } ],
            "jvm": [ "-cp", "${classpath}" ] } }
        """;
        var v = VersionMerger.Merge([Parse(json)]);
        var features = new LaunchFeatures
        {
            HasQuickPlaysSupport = true,
            IsQuickPlaySingleplayer = kind == QuickPlayKind.Singleplayer,
            IsQuickPlayMultiplayer = kind == QuickPlayKind.Multiplayer,
        };
        var vars = new Dictionary<string, string>(Vars)
        {
            ["quickPlayPath"] = @"C:\game\quickPlay\java\fluorite.json",
            ["quickPlaySingleplayer"] = kind == QuickPlayKind.Singleplayer ? target : "",
            ["quickPlayMultiplayer"] = kind == QuickPlayKind.Multiplayer ? target : "",
        };
        var built = ArgumentBuilder.Build(v, RuleContext.ForCurrentOs(CpuArch.X64, features), vars, [], null, Options(qp: new QuickPlay(kind, target)));

        Assert.Equal(["--username", "Steve", "--quickPlayPath", @"C:\game\quickPlay\java\fluorite.json", flag, target], built.Game);
        Assert.DoesNotContain("--server", built.Game); // No legacy fallback when the version has Quick Play.
    }

    [Theory]
    [InlineData("play.example.com", "play.example.com", "25565")]
    [InlineData("play.example.com:25570", "play.example.com", "25570")]
    [InlineData("[::1]:25566", "::1", "25566")]
    [InlineData("::1", "::1", "25565")]
    public void SplitHostPort(string target, string host, string port) =>
        Assert.Equal((host, port), ArgumentBuilder.SplitHostPort(target));
}
