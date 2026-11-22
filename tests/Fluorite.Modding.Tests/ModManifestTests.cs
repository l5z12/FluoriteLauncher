using System.IO.Compression;
using System.Text.Json.Nodes;
using Fluorite.Modding.Content;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Platforms;

namespace Fluorite.Modding.Tests;

public class ModManifestTests
{
    [Fact]
    public void Fabric_WidensMinecraft_OnlyWhenNeeded()
    {
        var strict = Jar.FabricMod("a", depends: """{ "minecraft": "1.21.1", "fabricloader": ">=0.16" }""");
        Assert.Null(ModManifest.AllowVersion("fabric.mod.json", Jar.FabricMod("t", depends: """{ "minecraft": "~1.21.1" }"""), "minecraft", "1.21.4")); // ~ takes 1.21.x.
        var widened = ModManifest.AllowVersion("fabric.mod.json", strict, "minecraft", "1.21.4")!;
        var depends = JsonNode.Parse(widened)!["depends"]!;
        Assert.Equal(["1.21.1", "1.21.4"], depends["minecraft"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(">=0.16", (string?)depends["fabricloader"]);

        Assert.Null(ModManifest.AllowVersion("fabric.mod.json", widened, "minecraft", "1.21.4")); // Now accepted.
        Assert.Null(ModManifest.AllowVersion("fabric.mod.json", strict, "minecraft", "1.21.1")); // Already accepted.
        Assert.Null(ModManifest.AllowVersion("fabric.mod.json", strict, "quilt_loader", "0.29")); // No such dependency.
        Assert.Null(ModManifest.AllowVersion("fabric.mod.json", "{ not json", "minecraft", "1.21.4"));

        var anyOf = Jar.FabricMod("b", depends: """{ "minecraft": ["1.20.1", "1.20.2"] }""");
        Assert.Equal(["1.20.1", "1.20.2", "1.20.4"],
            JsonNode.Parse(ModManifest.AllowVersion("fabric.mod.json", anyOf, "minecraft", "1.20.4")!)!["depends"]!["minecraft"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void Quilt_WidensVersionsOfThatDependency()
    {
        var text = """{ "schema_version": 1, "quilt_loader": { "id": "q", "version": "1", "depends": ["quilt_loader", { "id": "minecraft", "versions": "=1.20.1" }] } }""";
        var widened = JsonNode.Parse(ModManifest.AllowVersion("quilt.mod.json", text, "minecraft", "1.20.4")!)!;
        Assert.Equal(["=1.20.1", "1.20.4"], widened["quilt_loader"]!["depends"]![1]!["versions"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void ModsToml_AddsARangeToThatDependencyOnly_AsText()
    {
        var text = """
            modLoader="javafml"
            loaderVersion="[47,)"
            [[mods]]
            modId="thing"
            # Keep this comment.
            [[dependencies.thing]]
                modId="forge"
                mandatory=true
                versionRange="[47,)"
            [[dependencies.thing]]
                versionRange = "[1.20.1,1.20.2)"
                modId = "minecraft"
                mandatory = true
            """;
        var widened = ModManifest.AllowVersion("META-INF/mods.toml", text, "minecraft", "1.20.4")!;
        Assert.Contains("""versionRange = "[1.20.1,1.20.2),[1.20.4]" """.TrimEnd(), widened, StringComparison.Ordinal);
        Assert.Contains("""versionRange="[47,)" """.TrimEnd(), widened, StringComparison.Ordinal); // Forge's untouched.
        Assert.Contains("# Keep this comment.", widened, StringComparison.Ordinal);
        Assert.Null(ModManifest.Validate("META-INF/mods.toml", widened));
        Assert.Null(ModManifest.AllowVersion("META-INF/mods.toml", widened, "minecraft", "1.20.4"));
        Assert.Null(ModManifest.AllowVersion("mcmod.info", """[{"modid":"x","mcversion":"1.12.2"}]""", "minecraft", "1.12"));
    }

    [Fact]
    public void Validate_SaysWhereJsonBreaks()
    {
        Assert.Null(ModManifest.Validate("fabric.mod.json", Jar.FabricMod("a")));
        Assert.StartsWith("Line 2,", ModManifest.Validate("fabric.mod.json", "{\n  \"id\": \"a\",,\n}"));
        Assert.NotNull(ModManifest.Validate("META-INF/neoforge.mods.toml", "[[mods]\nmodId=\"x\""));
    }

    [Fact]
    public async Task Edit_WritesTheJar_KeepsTheOriginalAndTheSource_AndRestores()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var jar = Jar.Write(Path.Combine(dir, "mods"), "a.jar",
                ("fabric.mod.json", Jar.FabricMod("a", depends: """{ "minecraft": "1.21.1" }""")), ("META-INF/SIGNER.SF", "x"), ("META-INF/SIGNER.RSA", "x"), ("a/A.class", "code"));
            using var manager = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            var location = new ContentLocation(ContentKind.Mod);
            var file = Assert.Single(await manager.ScanAsync(location, ct));
            var originalSha1 = file.Record.Sha1;
            await manager.UpdateAsync(i => { i.Files[file.Key].Source = new RemoteRef { Platform = ContentPlatform.Modrinth, ProjectId = "p", VersionId = "v" }; return true; }, ct);

            Assert.Equal(["fabric.mod.json"], ModManifest.Find(jar));
            Assert.True(ModManifest.IsSigned(jar));
            var text = ModManifest.Read(jar, "fabric.mod.json");
            var unsigned = await manager.EditManifestAsync(file, "fabric.mod.json", ModManifest.AllowVersion("fabric.mod.json", text, "minecraft", "1.21.4")!, ct);

            Assert.True(unsigned);
            Assert.False(ModManifest.IsSigned(jar));
            var edited = Assert.Single(await manager.ScanAsync(location, ct));
            Assert.Equal("p", edited.Record.Source?.ProjectId); // Still known: updates keep coming.
            Assert.Equal(originalSha1, edited.Record.OriginalSha1);
            Assert.NotEqual(originalSha1, edited.Record.Sha1);
            Assert.Contains(edited.Metadata.Mods[0].Dependencies, d => d.Id == "minecraft" && d.VersionRange == "1.21.1 || 1.21.4");
            using (var zip = ZipFile.OpenRead(jar))
                Assert.NotNull(zip.GetEntry("a/A.class")); // The rest of the jar kept.

            // Breaking the mod's id is refused, and leaves the jar as it was.
            await Assert.ThrowsAsync<InvalidDataException>(() => manager.EditManifestAsync(edited, "fabric.mod.json", "{}", ct));
            Assert.Equal(edited.Record.Sha1, (await Util.ContentHashes.ComputeAsync(jar, ct)).Sha1);

            Assert.NotNull(manager.OriginalOf(edited));
            await manager.RestoreOriginalAsync(edited, ct);
            var restored = Assert.Single(await manager.ScanAsync(location, ct));
            Assert.Equal(originalSha1, restored.Record.Sha1);
            Assert.Null(restored.Record.OriginalSha1);
            Assert.Equal("p", restored.Record.Source?.ProjectId);
            Assert.Empty(Directory.EnumerateFiles(manager.OriginalsDirectory));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
