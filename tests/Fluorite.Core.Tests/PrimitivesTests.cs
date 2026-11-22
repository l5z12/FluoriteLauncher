using Fluorite.Core.Java;
using Fluorite.Core.Launch;
using Fluorite.Core.Net;
using Fluorite.Core.Platform;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class MavenCoordinateTests
{
    [Theory]
    [InlineData("org.lwjgl:lwjgl:3.3.1", "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.1:natives-windows-arm64", "org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1-natives-windows-arm64.jar")]
    [InlineData("net.minecraftforge:forge:1.20.1-47.2.0:client", "net/minecraftforge/forge/1.20.1-47.2.0/forge-1.20.1-47.2.0-client.jar")]
    [InlineData("de.oceanlabs.mcp:mcp_config:1.20.1@zip", "de/oceanlabs/mcp/mcp_config/1.20.1/mcp_config-1.20.1.zip")]
    [InlineData("net.neoforged:neoform:1.21.1-20240808.144430:mappings@txt", "net/neoforged/neoform/1.21.1-20240808.144430/neoform-1.21.1-20240808.144430-mappings.txt")]
    public void RelativePath(string name, string expected) =>
        Assert.Equal(expected, MavenCoordinate.Parse(name).RelativePath);

    [Fact]
    public void DedupeKey_IgnoresVersion() =>
        Assert.Equal(MavenCoordinate.Parse("org.ow2.asm:asm:9.6").DedupeKey, MavenCoordinate.Parse("org.ow2.asm:asm:9.7").DedupeKey);

    [Theory]
    [InlineData("")]
    [InlineData("onlygroup")]
    [InlineData("g:a")]
    [InlineData("::1.0")]
    public void Invalid(string name) => Assert.False(MavenCoordinate.TryParse(name, out _));
}

public class CommandLineTests
{
    [Theory]
    [InlineData("simple", "simple")]
    [InlineData("has space", "\"has space\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData(@"C:\path with space\", "\"C:\\path with space\\\\\"")]
    [InlineData(@"C:\no\spaces\", @"C:\no\spaces\")]
    public void Quote(string input, string expected) => Assert.Equal(expected, CommandLine.Quote(input));

    [Fact]
    public void Split_RespectsQuotes() =>
        Assert.Equal(["-Xmx4G", "-Dfoo=a b", "-XX:+UseG1GC"], CommandLine.Split("-Xmx4G \"-Dfoo=a b\"   -XX:+UseG1GC"));

    [Fact]
    public void Split_Empty() => Assert.Empty(CommandLine.Split("   "));
}

public class LenientDateTests
{
    [Theory]
    [InlineData("2024-12-03T10:12:57+00:00")]
    [InlineData("2014-06-25T16:44:36+0000")]
    [InlineData("2013-04-25T15:45:00-0500")]
    [InlineData("2026-09-15T08:00:00Z")]
    public void Parses(string s) => Assert.NotNull(LenientDate.Parse(s));

    [Fact]
    public void ReturnsNullForGarbage() => Assert.Null(LenientDate.Parse("not a date"));
}

public class JavaTests
{
    [Theory]
    [InlineData("1.8.0_381", 8)]
    [InlineData("17.0.8", 17)]
    [InlineData("21", 21)]
    [InlineData("\"25.0.1\"", 25)]
    [InlineData("1.7.0_80", 7)]
    [InlineData("garbage", 0)]
    public void ParseMajor(string v, int expected) => Assert.Equal(expected, JavaInstallation.ParseMajor(v));

    private static JavaInstallation J(int major, CpuArch arch = CpuArch.X64, JavaSource src = JavaSource.Registry) =>
        new($@"C:\java{major}{arch}", $"{major}.0.0", major, arch, "Test", src);

    [Fact]
    public void LegacyRequirement_IsStrict()
    {
        var req = JavaRequirement.For(8, "jre-legacy", versionHasArm64Natives: false, CpuArch.X64);
        Assert.Null(JavaSelector.Select([J(17), J(21)], req));
        Assert.Equal(8, JavaSelector.Select([J(17), J(8)], req)!.Major);
    }

    [Fact]
    public void ModernRequirement_PrefersExactThenClosestNewer()
    {
        var req = JavaRequirement.For(17, "java-runtime-gamma", true, CpuArch.X64);
        Assert.Equal(17, JavaSelector.Select([J(25), J(17), J(21)], req)!.Major);
        Assert.Equal(21, JavaSelector.Select([J(25), J(21)], req)!.Major);
        Assert.Null(JavaSelector.Select([J(16), J(8)], req));
    }

    [Fact]
    public void Arm64Host_WithoutArm64Natives_PrefersX64Java()
    {
        var req = JavaRequirement.For(8, "jre-legacy", versionHasArm64Natives: false, CpuArch.Arm64);
        Assert.Equal(CpuArch.X64, req.PreferredArch);
        Assert.Null(JavaSelector.Select([J(8, CpuArch.Arm64)], req));
        Assert.Equal(CpuArch.X64, JavaSelector.Select([J(8, CpuArch.Arm64), J(8, CpuArch.X64)], req)!.Arch);
    }

    [Fact]
    public void Arm64Host_WithArm64Natives_PrefersNativeButAcceptsX64()
    {
        var req = JavaRequirement.For(21, "java-runtime-delta", versionHasArm64Natives: true, CpuArch.Arm64);
        Assert.Equal(CpuArch.Arm64, JavaSelector.Select([J(21, CpuArch.X64), J(21, CpuArch.Arm64)], req)!.Arch);
        Assert.Equal(CpuArch.X64, JavaSelector.Select([J(21, CpuArch.X64)], req)!.Arch);
    }

    [Fact]
    public void ManagedRuntimes_WinTies()
    {
        var req = JavaRequirement.For(21, null, false, CpuArch.X64);
        Assert.Equal(JavaSource.Fluorite, JavaSelector.Select([J(21), J(21, src: JavaSource.Fluorite) with { Home = "x" }], req)!.Source);
    }
}

public class MirrorTableTests
{
    [Fact]
    public void Official_PutsUpstreamFirst()
    {
        var c = new MirrorTable(DownloadSourceKind.Official).Candidates("https://libraries.minecraft.net/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar");
        Assert.Equal("libraries.minecraft.net", c[0].Host);
        Assert.Equal("https://bmclapi2.bangbang93.com/maven/org/lwjgl/lwjgl/3.3.1/lwjgl-3.3.1.jar", c[1].ToString());
    }

    [Fact]
    public void Bmclapi_PutsMirrorFirst()
    {
        var c = new MirrorTable(DownloadSourceKind.Bmclapi).Candidates("https://resources.download.minecraft.net/ab/abcdef");
        Assert.Equal("https://bmclapi2.bangbang93.com/assets/ab/abcdef", c[0].ToString());
        Assert.Equal("resources.download.minecraft.net", c[1].Host);
    }

    [Fact]
    public void UnknownHost_SingleCandidate() =>
        Assert.Single(new MirrorTable(DownloadSourceKind.Bmclapi).Candidates("https://github.com/owner/repo/releases/download/v1/x.jar"));

    [Theory]
    [InlineData("https://api.modrinth.com/v2/project/AANobbMI", "https://mod.mcimirror.top/modrinth/v2/project/AANobbMI")]
    [InlineData("https://cdn.modrinth.com/data/AANobbMI/versions/x/sodium.jar", "https://mod.mcimirror.top/data/AANobbMI/versions/x/sodium.jar")]
    [InlineData("https://api.curseforge.com/v1/mods/238222", "https://mod.mcimirror.top/curseforge/v1/mods/238222")]
    [InlineData("https://edge.forgecdn.net/files/9394/360/jei.jar", "https://mod.mcimirror.top/files/9394/360/jei.jar")]
    public void ContentPlatforms_MirrorThroughMcim(string url, string mirrored)
    {
        Assert.Equal([new Uri(mirrored), new Uri(url)], new MirrorTable(DownloadSourceKind.Bmclapi).Candidates(url));
        Assert.Equal([new Uri(url), new Uri(mirrored)], new MirrorTable(DownloadSourceKind.Official).Candidates(url));
    }

    [Fact]
    public void LegacyForgeMaven_IsCanonicalized() =>
        Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.7.10-10.13.4.1614/x.jar",
            MirrorTable.Canonicalize("http://files.minecraftforge.net/maven/net/minecraftforge/forge/1.7.10-10.13.4.1614/x.jar"));
}
