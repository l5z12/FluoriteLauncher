using System.Text;
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

public class AtomicFileTests
{
    [Fact]
    public void Write_ThatFails_LeavesTheFileAsItWas()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "settings.json");
            File.WriteAllText(path, "old");
            Assert.Throws<IOException>(() => AtomicFile.Write(path, tmp =>
            {
                File.WriteAllText(tmp, "ne"); // Cut short: the disk filled up.
                throw new IOException("There is not enough space on the disk.");
            }));
            Assert.Equal("old", File.ReadAllText(path));

            AtomicFile.WriteAllText(path, "new");
            Assert.Equal("new", File.ReadAllText(path));
            Assert.Equal(["settings.json"], Directory.EnumerateFiles(dir).Select(Path.GetFileName)); // No temp file left.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Write_WaitsForAReadOfTheFileToFinish()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Only Windows refuses to replace a file that's open.");
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "skin.png");
            File.WriteAllText(path, "old");
            var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); // Another load of the same skin.
            var done = Task.Run(async () =>
            {
                await Task.Delay(30, TestContext.Current.CancellationToken);
                await reader.DisposeAsync();
            }, TestContext.Current.CancellationToken);
            AtomicFile.WriteAllText(path, "new");
            await done;
            Assert.Equal("new", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("sodium.jar.1a2b3c4d.tmp", true)]
    [InlineData("fluorite.1a2b3c4d.tmp", true)]
    [InlineData("notes.tmp", false)]
    [InlineData("backup.2024.tmp", false)]
    [InlineData("sodium.jar", false)]
    public void IsTempFile(string name, bool temp) => Assert.Equal(temp, AtomicFile.IsTempFile(name));

    [Fact]
    public void WriteAllText_WritesTheEncodingAsFileDoes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var plain = Path.Combine(dir, "plain.txt");
            var marked = Path.Combine(dir, "marked.url");
            AtomicFile.WriteAllText(plain, "é");
            AtomicFile.WriteAllText(marked, "é", Encoding.UTF8);
            Assert.Equal([0xC3, 0xA9], File.ReadAllBytes(plain));
            Assert.Equal([0xEF, 0xBB, 0xBF, 0xC3, 0xA9], File.ReadAllBytes(marked));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TempPath_IsATempFile() => Assert.True(AtomicFile.IsTempFile(Path.GetFileName(AtomicFile.TempPath(@"C:\mods\sodium.jar"))));

    [Fact]
    public void Write_OfAFileWithTheLongestNameThereIs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, new string('m', 251) + ".jar"); // 255 characters: no room for a suffix.
            AtomicFile.WriteAllText(path, "jar");
            Assert.Equal("jar", File.ReadAllText(path));
            Assert.Single(Directory.EnumerateFiles(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Copy_OfAReadOnlyFile_ThatCantBePutInPlace_LeavesNoTempFile()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Skip("Only Windows refuses to replace a file that's open.");
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, "asset");
        try
        {
            File.WriteAllText(source, "a");
            File.SetAttributes(source, FileAttributes.ReadOnly);
            var target = Path.Combine(dir, "target");
            File.WriteAllText(target, "old");
            using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.True(Record.Exception(() => AtomicFile.Copy(source, target)) is IOException or UnauthorizedAccessException);
            Assert.Equal(["asset", "target"], Directory.EnumerateFiles(dir).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            File.SetAttributes(source, FileAttributes.Normal);
            Directory.Delete(dir, recursive: true);
        }
    }
}
