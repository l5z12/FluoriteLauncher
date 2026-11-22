using System.Net;
using System.Text;
using Fluorite.Core;
using Fluorite.Core.Instances;
using Fluorite.Core.Net;
using Fluorite.Modding.Loaders;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Tests;

internal sealed class StubHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request.RequestUri!));

    public static HttpResponseMessage Text(string body, string type = "application/json") =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, type) };

    public static HttpResponseMessage NotFound() => new(HttpStatusCode.NotFound);
}

public class LoaderVersionComparerTests
{
    [Theory]
    [InlineData("0.16.10", "0.16.9")]
    [InlineData("47.2.10", "47.2.9")]
    [InlineData("21.1.77", "21.1.77-beta")]
    [InlineData("0.20.0", "0.20.0-beta.9")]
    [InlineData("0.20.0-beta.10", "0.20.0-beta.9")]
    [InlineData("10.13.4.1614-1.7.10", "10.13.4.1566-1.7.10")]
    [InlineData("26.3.0.101", "26.3.0.99")]
    [InlineData("HD_U_I6", "HD_U_I5")]
    [InlineData("1.12.2", "1.5.2")]
    public void FirstIsNewer(string newer, string older)
    {
        Assert.True(LoaderVersionComparer.Instance.Compare(newer, older) > 0);
        Assert.True(LoaderVersionComparer.Instance.Compare(older, newer) < 0);
    }

    [Fact]
    public void EqualVersions() => Assert.Equal(0, LoaderVersionComparer.Instance.Compare("1.2.0", "1.2"));
}

public class NeoForgeTests
{
    [Theory]
    [InlineData("20.2.86", "1.20.2")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("21.1.265", "1.21.1")]
    [InlineData("21.1.77-beta", "1.21.1")]
    [InlineData("26.1.2.126", "26.1.2")]
    [InlineData("26.3.0.101", "26.3")]
    [InlineData("26.1.0.5-beta", "26.1")]
    public void MapsToMinecraft(string neoforge, string minecraft) =>
        Assert.Equal(minecraft, NeoForgeProvider.MinecraftVersionOf(neoforge));

    [Fact]
    public void Garbage_ReturnsNull() => Assert.Null(NeoForgeProvider.MinecraftVersionOf("abc"));
}

public class ForgeInstallerTests
{
    private static readonly GameDirectories Dirs = new(@"C:\mc");

    private static readonly Dictionary<string, string> Data = new()
    {
        ["SIDE"] = "client",
        ["MINECRAFT_JAR"] = @"C:\mc\versions\1.20.1\1.20.1.jar",
        ["MAPPINGS"] = @"C:\mc\libraries\x\mappings.txt",
    };

    [Fact]
    public void ResolvesMavenCoordinates() =>
        Assert.Equal(@"C:\mc\libraries\net\minecraftforge\forge\1.20.1-47.2.0\forge-1.20.1-47.2.0-client.jar",
            ForgeInstaller.ResolveArgument("[net.minecraftforge:forge:1.20.1-47.2.0:client]", Data, Dirs));

    [Fact]
    public void ResolvesDataKeys_WholeAndEmbedded()
    {
        Assert.Equal(@"C:\mc\versions\1.20.1\1.20.1.jar", ForgeInstaller.ResolveArgument("{MINECRAFT_JAR}", Data, Dirs));
        Assert.Equal(@"--mappings=C:\mc\libraries\x\mappings.txt", ForgeInstaller.ResolveArgument("--mappings={MAPPINGS}", Data, Dirs));
        Assert.Equal("{UNKNOWN}", ForgeInstaller.ResolveArgument("{UNKNOWN}", Data, Dirs));
    }

    [Fact]
    public void StripsLiteralQuotes() => Assert.Equal("abc123", ForgeInstaller.ResolveArgument("'abc123'", Data, Dirs));

    [Fact]
    public async Task ForgeVersions_UsePromotions_IncludingLegacySuffix()
    {
        const string metadata = """
        <metadata><versioning><versions>
          <version>1.7.10-10.13.4.1566-1.7.10</version>
          <version>1.7.10-10.13.4.1614-1.7.10</version>
          <version>1.20.1-47.2.0</version>
        </versions></versioning></metadata>
        """;
        const string promos = """{ "promos": { "1.7.10-recommended": "10.13.4.1614", "1.7.10-latest": "10.13.4.1614" } }""";
        using var http = new HttpClient(new StubHandler(uri => uri.AbsolutePath.EndsWith("maven-metadata.xml", StringComparison.Ordinal)
            ? StubHandler.Text(metadata, "text/xml")
            : StubHandler.Text(promos)));
        var versions = await new ForgeProvider(http, MirrorTable.Official).GetVersionsAsync("1.7.10", TestContext.Current.CancellationToken);

        Assert.Equal(["10.13.4.1614-1.7.10", "10.13.4.1566-1.7.10"], versions.Select(v => v.Version));
        Assert.True(versions[0].Recommended);
        Assert.True(versions[0].Latest);
        Assert.False(versions[1].Recommended);
    }

    [Fact]
    public async Task ForgeVersions_AncientMinecraft_HasNoInstallers()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not be called")));
        Assert.Empty(await new ForgeProvider(http, MirrorTable.Official).GetVersionsAsync("1.4.7", TestContext.Current.CancellationToken));
    }
}

public class FabricLikeTests
{
    [Fact]
    public async Task SortsAndPicksStable_QuiltWithoutStableFlag()
    {
        const string body = """
        [ { "loader": { "version": "0.20.0-beta.9" } },
          { "loader": { "version": "0.30.2" } },
          { "loader": { "version": "0.31.0-beta.6" } },
          { "loader": { "version": "0.29.1" } } ]
        """;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text(body)));
        var list = await FabricLikeProvider.Quilt(http, MirrorTable.Official).GetVersionsAsync("1.20.1", TestContext.Current.CancellationToken);

        Assert.Equal(["0.31.0-beta.6", "0.30.2", "0.29.1", "0.20.0-beta.9"], list.Select(v => v.Version));
        Assert.Equal("0.30.2", Assert.Single(list, v => v.Recommended).Version);
        Assert.True(list[0].Latest);
    }

    [Fact]
    public async Task FabricStableFlagIsHonoured()
    {
        const string body = """[ { "loader": { "version": "0.20.1", "stable": false } }, { "loader": { "version": "0.20.0", "stable": true } } ]""";
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text(body)));
        var list = await FabricLikeProvider.Fabric(http, MirrorTable.Official).GetVersionsAsync("26.3", TestContext.Current.CancellationToken);
        Assert.Equal("0.20.0", Assert.Single(list, v => v.Recommended).Version);
    }

    [Fact]
    public async Task UnsupportedMinecraft_ReturnsEmpty()
    {
        using var http = new HttpClient(new StubHandler(_ => StubHandler.NotFound()));
        Assert.Empty(await FabricLikeProvider.LegacyFabric(http, MirrorTable.Official).GetVersionsAsync("26.3", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Availability_UsesCachedGameList()
    {
        var requests = new List<string>();
        using var http = new HttpClient(new StubHandler(r =>
        {
            requests.Add(r.AbsolutePath);
            return StubHandler.Text("""[ { "version": "1.21.1", "stable": true }, { "version": "26.4-snapshot-7", "stable": false } ]""");
        }));
        ILoaderProvider fabric = FabricLikeProvider.Fabric(http, MirrorTable.Official);
        var ct = TestContext.Current.CancellationToken;

        Assert.True(await fabric.IsAvailableAsync("1.21.1", ct));
        Assert.True(await fabric.IsAvailableAsync("26.4-snapshot-7", ct));
        Assert.False(await fabric.IsAvailableAsync("1.13.2", ct));
        Assert.Equal(["/v2/versions/game"], requests);
    }

    [Fact]
    public async Task Availability_DefaultsToTheVersionList()
    {
        using var http = new HttpClient(new StubHandler(_ => throw new InvalidOperationException("should not be called")));
        ILoaderProvider forge = new ForgeProvider(http, MirrorTable.Official);
        Assert.False(await forge.IsAvailableAsync("1.4.7", TestContext.Current.CancellationToken));
    }
}

public class LiteLoaderTests
{
    [Fact]
    public async Task ParsesManifest_WithSharedSnapshotLibraries()
    {
        const string body = """
        { "versions": {
            "1.12.2": {
              "repo": { "stream": "SNAPSHOT", "url": "http://repo.mumfrey.com/" },
              "snapshots": {
                "libraries": [ { "name": "net.minecraft:launchwrapper:1.12" } ],
                "com.mumfrey:liteloader": {
                  "latest": { "version": "1.12.2-SNAPSHOT", "file": "liteloader-1.12.2-SNAPSHOT.jar", "tweakClass": "com.mumfrey.liteloader.launch.LiteLoaderTweaker" }
                }
              }
            },
            "1.7.10": {
              "artefacts": {
                "com.mumfrey:liteloader": {
                  "latest": { "version": "1.7.10_04" },
                  "abc": { "version": "1.7.10_04" },
                  "def": { "version": "1.7.10_03" }
                }
              }
            }
        } }
        """;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Text(body)));
        var provider = new LiteLoaderProvider(http, MirrorTable.Official);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("1.12.2-SNAPSHOT", Assert.Single(await provider.GetVersionsAsync("1.12.2", ct)).Version);
        Assert.Equal(["1.7.10_04", "1.7.10_03"], (await provider.GetVersionsAsync("1.7.10", ct)).Select(v => v.Version));
        Assert.Empty(await provider.GetVersionsAsync("1.20.1", ct));
    }
}

public class ComponentResolverTests
{
    private static InstanceManifest Manifest(params (string Uid, string Version)[] components) => new()
    {
        Name = "t",
        Components = components.Select(c => new ComponentRef { Uid = c.Uid, Version = c.Version }).ToList(),
    };

    [Fact]
    public void Vanilla_LaunchesMinecraftId() =>
        Assert.Equal("1.21.4", ComponentResolver.LaunchVersionId(Manifest((ComponentUids.Minecraft, "1.21.4"))));

    [Fact]
    public void Loader_UsesRecordedProfileId()
    {
        var m = Manifest((ComponentUids.Minecraft, "1.7.10"), (ComponentUids.Forge, "10.13.4.1614-1.7.10"));
        Assert.Equal("1.7.10-forge-10.13.4.1614-1.7.10", ComponentResolver.LaunchVersionId(m)); // conventional guess
        m.Components[1].ProfileId = "1.7.10-Forge10.13.4.1614-1.7.10";
        Assert.Equal("1.7.10-Forge10.13.4.1614-1.7.10", ComponentResolver.LaunchVersionId(m)); // installer's real id
    }

    [Fact]
    public void ForgeWithOptiFine_OptiFineIsAMod()
    {
        var m = Manifest((ComponentUids.Minecraft, "1.20.1"), (ComponentUids.OptiFine, "HD_U_I6"), (ComponentUids.Forge, "47.2.0"));
        Assert.Equal(ComponentUids.Forge, ComponentResolver.PrimaryLoader(m)!.Uid);
        Assert.Equal("HD_U_I6", ComponentResolver.OptiFineAsMod(m)!.Version);
    }

    [Fact]
    public void OptiFineAlone_IsPrimary()
    {
        var m = Manifest((ComponentUids.Minecraft, "1.20.1"), (ComponentUids.OptiFine, "HD_U_I6"));
        Assert.Equal(ComponentUids.OptiFine, ComponentResolver.PrimaryLoader(m)!.Uid);
        Assert.Null(ComponentResolver.OptiFineAsMod(m));
        Assert.Equal("1.20.1-OptiFine_HD_U_I6", ComponentResolver.LaunchVersionId(m));
    }
}
