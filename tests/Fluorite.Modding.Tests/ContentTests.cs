using System.IO.Compression;
using System.Text;
using Fluorite.Core.Instances;
using Fluorite.Modding.Content;
using Fluorite.Modding.Loaders;
using Fluorite.Modding.Metadata;
using Fluorite.Modding.Platforms;
using Fluorite.Modding.Util;

namespace Fluorite.Modding.Tests;

internal static class Jar
{
    /// <summary>Builds a zip with the given text entries (and nested binary entries).</summary>
    public static byte[] Build(params (string Name, object Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var bytes = content switch
                {
                    string text => Encoding.UTF8.GetBytes(text),
                    byte[] b => b,
                    _ => throw new ArgumentException("content"),
                };
                s.Write(bytes);
            }
        }
        return ms.ToArray();
    }

    public static ContentMetadata Read(params (string Name, object Content)[] entries)
    {
        using var ms = new MemoryStream(Build(entries));
        return ContentMetadataReader.Read(ms);
    }

    public static string Write(string dir, string fileName, params (string Name, object Content)[] entries)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllBytes(path, Build(entries));
        return path;
    }

    public static string FabricMod(string id, string version = "1.0.0", string depends = "{}", string extra = "") =>
        $$"""{ "schemaVersion": 1, "id": "{{id}}", "version": "{{version}}", "name": "{{id}} mod", "depends": {{depends}} {{extra}} }""";
}

public class MetadataTests
{
    [Fact]
    public void Fabric_FullMetadata()
    {
        var meta = Jar.Read(
            ("fabric.mod.json", """
                {
                  "schemaVersion": 1, "id": "sodium", "version": "0.5.3+mc1.20.1", "name": "Sodium",
                  "description": "Fast",
                  "authors": ["JellySquid", { "name": "IMS" }],
                  "icon": { "16": "s16.png", "128": "assets/sodium/icon.png" },
                  "environment": "client",
                  "depends": { "minecraft": ["1.20", "1.20.1"], "fabricloader": ">=0.12.0" },
                  "breaks": { "optifabric": "*" },
                  "provides": ["rubidium-compat"],
                  "jars": [ { "file": "META-INF/jars/inner.jar" } ],
                }
                """),
            ("assets/sodium/icon.png", "png"),
            ("META-INF/jars/inner.jar", Jar.Build(("fabric.mod.json", Jar.FabricMod("fabric-api-base", "0.4.31")))));

        var mod = Assert.Single(meta.Mods);
        Assert.Equal("sodium", mod.Id);
        Assert.Equal(ModLoaderKind.Fabric, mod.Loader);
        Assert.Equal(["JellySquid", "IMS"], mod.Authors);
        Assert.Equal("client", mod.Environment);
        Assert.Equal("assets/sodium/icon.png", meta.IconEntry);
        Assert.Contains(mod.Dependencies, d => d.Id == "minecraft" && d.VersionRange == "1.20 || 1.20.1");
        Assert.Contains(mod.Dependencies, d => d.Id == "optifabric" && d.Kind == DependencyKind.Incompatible && d.VersionRange is null);
        Assert.Contains("rubidium-compat", meta.ProvidedIds);
        Assert.Contains("fabric-api-base", meta.ProvidedIds);
    }

    [Fact]
    public void Links_AndLicense_FromEachLoader()
    {
        var fabric = Assert.Single(Jar.Read(("fabric.mod.json", """
            {
              "schemaVersion": 1, "id": "f", "version": "1",
              "contact": { "homepage": "https://modrinth.com/mod/f", "sources": "https://modrinth.com/mod/f", "issues": "https://github.com/me/f/issues", "Discord": "https://discord.gg/abc", "irc": " " },
              "license": ["MIT", "CC-BY-4.0"]
            }
            """)).Mods);
        // The repeated page once, keys in lower case, blanks dropped.
        Assert.Equal([new ModLink("homepage", "https://modrinth.com/mod/f"), new ModLink("issues", "https://github.com/me/f/issues"), new ModLink("discord", "https://discord.gg/abc")], fabric.Links);
        Assert.Equal("MIT, CC-BY-4.0", fabric.License);

        var quilt = Assert.Single(Jar.Read(("quilt.mod.json", """
            { "quilt_loader": { "id": "q", "version": "1", "metadata": { "contact": { "email": "me@example.com" }, "license": { "id": "Apache-2.0", "name": "Apache License 2.0" } } } }
            """)).Mods);
        Assert.Equal([new ModLink("email", "me@example.com")], quilt.Links);
        Assert.Equal("Apache-2.0", quilt.License);

        var forge = Assert.Single(Jar.Read(("META-INF/mods.toml", """
            modLoader="javafml"
            license="All rights reserved"
            issueTrackerURL="https://github.com/me/m/issues"
            [[mods]]
            modId="m"
            version="1"
            displayURL="${mod_url}"
            """)).Mods);
        Assert.Equal([new ModLink("issues", "https://github.com/me/m/issues")], forge.Links); // The unfilled placeholder is left out.
        Assert.Equal("All rights reserved", forge.License);
    }

    [Fact]
    public void Fabric_RawNewlinesInStrings_StillParse()
    {
        var meta = Jar.Read(("fabric.mod.json", "{ \"id\": \"x\", \"version\": \"1\", \"description\": \"line one\nline two\" }"));
        Assert.Equal("x", Assert.Single(meta.Mods).Id);
    }

    [Fact]
    public void Quilt_DependsObjectsAndGroups()
    {
        var meta = Jar.Read(("quilt.mod.json", """
            {
              "schema_version": 1,
              "quilt_loader": {
                "group": "org.example", "id": "qmod", "version": "2.0.0",
                "metadata": { "name": "Q Mod", "contributors": { "Alice": "Owner" }, "icon": "icon.png" },
                "depends": [ "quilt_loader", { "id": "org.quiltmc:qsl", "versions": ">=6.0" }, { "id": "emi", "optional": true } ],
                "breaks": [ "badmod" ],
                "provides": [ { "id": "qmod_api" } ]
              },
              "minecraft": { "environment": "*" }
            }
            """), ("icon.png", "png"));
        var mod = Assert.Single(meta.Mods);
        Assert.Equal(ModLoaderKind.Quilt, mod.Loader);
        Assert.Equal("Q Mod", mod.Name);
        Assert.Contains(mod.Dependencies, d => d.Id == "qsl" && d.Kind == DependencyKind.Required && d.VersionRange == ">=6.0");
        Assert.Contains(mod.Dependencies, d => d.Id == "emi" && d.Kind == DependencyKind.Optional);
        Assert.Contains(mod.Dependencies, d => d.Id == "badmod" && d.Kind == DependencyKind.Incompatible);
        Assert.Contains("qmod_api", meta.ProvidedIds);
        Assert.Equal("icon.png", meta.IconEntry);
    }

    [Fact]
    public void ModsToml_ForgeWithJarVersionAndJarJar()
    {
        var meta = Jar.Read(
            ("META-INF/mods.toml", """
                modLoader="javafml"
                loaderVersion="[47,)"
                license="MIT"
                logoFile="logo.png"
                [[mods]]
                modId="jei"
                version="${file.jarVersion}"
                displayName="Just Enough Items"
                authors="mezz"
                description='''
                Item and recipe viewer
                '''
                [[dependencies.jei]]
                    modId="forge"
                    mandatory=true
                    versionRange="[47.0,)"
                    ordering="NONE"
                    side="BOTH"
                [[dependencies.jei]]
                    modId="minecraft"
                    mandatory=true
                    versionRange="[1.20.1, 1.20.2)"
                    side="BOTH"
                [[dependencies.jei]]
                    modId="serveronly"
                    mandatory=true
                    side="SERVER"
                """),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\r\nImplementation-Version: 15.2.0.27\r\n"),
            ("logo.png", "png"),
            ("META-INF/jarjar/metadata.json", """{ "jars": [ { "identifier": { "group": "mezz", "artifact": "cfg" }, "path": "META-INF/jarjar/cfg.jar" } ] }"""),
            ("META-INF/jarjar/cfg.jar", Jar.Build(("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"mezz_config\"\nversion=\"1.0\"\n"))));

        var mod = Assert.Single(meta.Mods);
        Assert.Equal(("jei", "15.2.0.27", ModLoaderKind.Forge), (mod.Id, mod.Version, mod.Loader));
        Assert.Equal("Item and recipe viewer", mod.Description);
        Assert.Equal(["mezz"], mod.Authors);
        Assert.Equal("logo.png", meta.IconEntry);
        Assert.Equal(2, mod.Dependencies.Count); // SERVER-only dependency skipped
        Assert.Contains("mezz_config", meta.ProvidedIds);
    }

    [Fact]
    public void ModsToml_NeoForgeTypes()
    {
        var meta = Jar.Read(("META-INF/neoforge.mods.toml", """
            modLoader="javafml"
            [[mods]]
            modId="create"
            version="6.0"
            [[dependencies.create]]
            modId="neoforge"
            type="required"
            versionRange="[21.1,)"
            [[dependencies.create]]
            modId="optifine"
            type="incompatible"
            [[dependencies.create]]
            modId="jei"
            type="optional"
            """));
        var mod = Assert.Single(meta.Mods);
        Assert.Equal(ModLoaderKind.NeoForge, mod.Loader);
        Assert.Contains(mod.Dependencies, d => d.Id == "optifine" && d.Kind == DependencyKind.Incompatible);
        Assert.Contains(mod.Dependencies, d => d.Id == "jei" && d.Kind == DependencyKind.Optional);
    }

    [Fact]
    public void ModsToml_EarlyNeoForgeDetectedByDependency()
    {
        var meta = Jar.Read(("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"m\"\nversion=\"1\"\n[[dependencies.m]]\nmodId=\"neoforge\"\nmandatory=true\nversionRange=\"[20.4,)\"\n"));
        Assert.Equal(ModLoaderKind.NeoForge, Assert.Single(meta.Mods).Loader);
    }

    [Fact]
    public void MultiLoaderJar_ListsBoth()
    {
        var meta = Jar.Read(
            ("fabric.mod.json", Jar.FabricMod("architectury")),
            ("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"architectury\"\nversion=\"9\"\n"));
        Assert.Equal([ModLoaderKind.Fabric, ModLoaderKind.Forge], meta.Loaders.Order());
    }

    [Fact]
    public void McModInfo_ListAndV2Forms()
    {
        var v1 = Jar.Read(("mcmod.info", """[{ "modid": "jei", "name": "JEI", "version": "4.16", "mcversion": "1.12.2", "authorList": ["mezz"], "requiredMods": ["Forge@[14.23,)"] }]"""));
        var m = Assert.Single(v1.Mods);
        Assert.Equal(ModLoaderKind.LegacyForge, m.Loader);
        Assert.DoesNotContain(m.Dependencies, d => d.Id == "minecraft"); // bare mcversion is informational
        Assert.Contains(m.Dependencies, d => d.Id == "Forge" && d.VersionRange == "[14.23,)");

        var v2 = Jar.Read(("mcmod.info", """{ "modListVersion": 2, "modList": [ { "modid": "a", "mcversion": "[1.7.10]" } ] }"""));
        Assert.Contains(Assert.Single(v2.Mods).Dependencies, d => d.Id == "minecraft" && d.VersionRange == "[1.7.10]");
    }

    [Fact]
    public void LiteMod() =>
        Assert.Equal(ModLoaderKind.LiteLoader, Assert.Single(Jar.Read(("litemod.json", """{ "name": "VoxelMap", "version": "1.9", "mcversion": "1.12.2", "author": "MamiyaOtaru" }""")).Mods).Loader);

    [Fact]
    public void PackMcmeta_TextComponentDescription()
    {
        var meta = Jar.Read(("pack.mcmeta", """{ "pack": { "pack_format": 15, "description": [ { "text": "§aFresh " }, { "text": "Animations", "extra": [ "!" ] } ] } }"""), ("pack.png", "png"));
        Assert.Equal(15, meta.PackFormat);
        Assert.Equal("Fresh Animations!", meta.Description);
        Assert.Equal("pack.png", meta.IconEntry);
        Assert.Empty(meta.Mods);
    }

    [Fact]
    public void ShaderPackAndOptiFineDetection()
    {
        Assert.True(Jar.Read(("shaders/composite.fsh", "void main(){}")).IsShaderPack);
        var of = Jar.Read(("optifine/Patcher.class", "x"), ("net/optifine/Config.class", "x"));
        Assert.True(of.IsOptiFine);
        Assert.Equal("OptiFine", of.Name);
    }

    [Fact]
    public void Garbage_IsEmptyNotThrow()
    {
        using var ms = new MemoryStream([1, 2, 3]);
        Assert.Same(ContentMetadata.Empty, ContentMetadataReader.Read(ms));
        Assert.Empty(Jar.Read(("fabric.mod.json", "{ not json")).Mods);
    }
}

public class VersionPredicateTests
{
    [Theory]
    [InlineData(">=1.20", "1.20.1", true)]
    [InlineData(">=1.20-", "1.20.1", true)]
    [InlineData("1.20.x", "1.20.4", true)]
    [InlineData("1.20.x", "1.21", false)]
    [InlineData("~1.20", "1.20.6", true)]
    [InlineData("~1.20", "1.21", false)]
    [InlineData("^0.15", "0.16.10", true)]
    [InlineData(">=1.19.4 <1.20", "1.20.1", false)]
    [InlineData("1.19.4 || 1.20.1", "1.20.1", true)]
    [InlineData("=1.20.1", "1.20.1", true)]
    [InlineData("1.20", "1.20.1", false)]
    [InlineData("*", "anything", true)]
    [InlineData(">=0.92.0", "0.92.2+1.20.1", true)]
    [InlineData(">=0.5.0", "0.5.0-beta.1", false)]
    [InlineData(">=26.1", "26.3", true)]
    public void Fabric(string predicate, string version, bool expected) => Assert.Equal(expected, VersionPredicate.MatchesFabric(predicate, version));

    [Theory]
    [InlineData(">=1.20", "24w14a")]
    [InlineData("1.20.x", "b1.7.3")]
    public void Fabric_UnknownIsNull(string predicate, string version) => Assert.Null(VersionPredicate.MatchesFabric(predicate, version));

    [Theory]
    [InlineData("[1.20.1,1.20.2)", "1.20.1", true)]
    [InlineData("[1.20.1, 1.20.2)", "1.20.2", false)]
    [InlineData("[1.20,)", "26.3", true)]
    [InlineData("(,1.20]", "1.20", true)]
    [InlineData("(,1.20)", "1.20", false)]
    [InlineData("[1.12.2]", "1.12.2", true)]
    [InlineData("[1.7.10],[1.12,1.13)", "1.12.2", true)]
    [InlineData("1.20.1", "1.12.2", true)] // soft requirement
    [InlineData("[47,)", "47.2.0", true)]
    public void Maven(string range, string version, bool expected) => Assert.Equal(expected, VersionPredicate.MatchesMaven(range, version));
}

public class HashTests
{
    [Fact]
    public void Murmur2_IgnoresWhitespace()
    {
        var a = CurseForgeFingerprint.Compute("hello world\r\n\tagain"u8);
        var b = CurseForgeFingerprint.Compute("helloworldagain"u8);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Murmur2_KnownVectors()
    {
        // Regression vectors; the implementation was verified against CurseForge's own fileFingerprint
        // (jei-1.20.1-forge-15.74.0.234.jar → 1871968345).
        Assert.Equal(CurseForgeFingerprint.Hash([], 1), CurseForgeFingerprint.Compute([]));
        Assert.Equal(CurseForgeFingerprint.Hash("abc"u8, 1), CurseForgeFingerprint.Compute("a b\tc"u8));
        Assert.NotEqual(CurseForgeFingerprint.Compute("abcd"u8), CurseForgeFingerprint.Compute("abce"u8));
    }

    [Fact]
    public async Task ContentHashes_MatchesStreamedFingerprint()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var data = new byte[200_000];
            new Random(42).NextBytes(data);
            var path = Path.Combine(dir, "f.bin");
            await File.WriteAllBytesAsync(path, data, TestContext.Current.CancellationToken);
            var h = await ContentHashes.ComputeAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(CurseForgeFingerprint.Compute(data), h.Murmur2);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(data)), h.Sha1);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}

public class ModCheckerTests
{
    private static ContentFile Mod(string file, ContentMetadata meta, bool enabled = true) => new()
    {
        Path = enabled ? $"C:\\mods\\{file}" : $"C:\\mods\\{file}.disabled",
        Key = $"mods/{file}",
        Location = new ContentLocation(ContentKind.Mod),
        Metadata = meta,
        Record = new ContentRecord(),
    };

    private static ContentFile Fabric(string id, string depends = "{}", string extra = "", string version = "1.0.0") =>
        Mod($"{id}.jar", Jar.Read(("fabric.mod.json", Jar.FabricMod(id, version, depends, extra))));

    private static IReadOnlyList<ModIssue> CheckFabric(params ContentFile[] mods) =>
        ModChecker.Check(mods, ComponentUids.FabricLoader, "0.16.10", "1.20.1");

    [Fact]
    public void Clean() =>
        Assert.Empty(CheckFabric(Fabric("fabric-api", extra: ",\"provides\":[\"fabric\"]"), Fabric("sodium", """{ "minecraft": "1.20.x", "fabricloader": ">=0.12", "fabric": "*" }""")));

    [Fact]
    public void MissingDependency()
    {
        var issue = Assert.Single(CheckFabric(Fabric("modmenu", """{ "fabric-api": "*" }""")));
        Assert.Equal((ModIssueKind.MissingDependency, "fabric-api"), (issue.Kind, issue.Other));
    }

    [Fact]
    public void DismissalKeyFollowsTheProblemNotTheFile()
    {
        static string KeyOf(ContentFile mod) => Assert.Single(CheckFabric(mod)).Key;
        var before = KeyOf(Fabric("modmenu", """{ "fabric-api": ">=0.90" }"""));
        // Updated (new file name and version), same problem: still dismissed.
        var updated = Fabric("modmenu", """{ "fabric-api": ">=0.90" }""", version: "2.0.0");
        Assert.Equal(before, KeyOf(Mod("modmenu-2.0.0.jar", updated.Metadata)));
        // Needs something else now: a new problem.
        Assert.NotEqual(before, KeyOf(Fabric("modmenu", """{ "fabric-api": ">=0.100" }""")));
    }

    [Fact]
    public void DismissalKeyFollowsTheInstance()
    {
        var mod = Fabric("sodium", """{ "minecraft": "1.20.1" }""");
        string KeyOn(string minecraft) => Assert.Single(ModChecker.Check([mod], ComponentUids.FabricLoader, "0.16.10", minecraft)).Key;
        // Dismissed on 1.20.4, asked again once the instance moves to 1.21.4.
        Assert.NotEqual(KeyOn("1.20.4"), KeyOn("1.21.4"));
    }

    [Fact]
    public void FixedDismissalsAreForgottenButNotThoseOfDisabledMods()
    {
        var needsApi = Fabric("modmenu", """{ "fabric-api": "*" }""");
        var key = Assert.Single(CheckFabric(needsApi)).Key;
        var dismissed = new HashSet<string> { key };
        var fixedMods = new[] { needsApi, Fabric("fabric-api") };
        Assert.Equal(key, Assert.Single(ModChecker.ResolvedDismissals(dismissed, CheckFabric(fixedMods), fixedMods)));
        var disabled = new[] { Mod("modmenu.jar", needsApi.Metadata, enabled: false) };
        Assert.Empty(ModChecker.ResolvedDismissals(dismissed, CheckFabric(disabled), disabled));
    }

    [Fact]
    public async Task DismissedIssuesAreKeptPerInstance()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var manager = new ContentManager(dir, Path.Combine(dir, ".fluorite")))
            {
                await manager.SetIssuesDismissedAsync("forge", ["a", "b"], dismissed: true, ct);
                await manager.SetIssuesDismissedAsync("forge", ["a"], dismissed: false, ct);
            }
            using var reopened = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            Assert.Equal("b", Assert.Single(await reopened.GetDismissedIssuesAsync("forge", ct)));
            // Another profile sharing the game directory (and so the index) has its own.
            Assert.Empty(await reopened.GetDismissedIssuesAsync("neoforge", ct));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DependencyInsideJarInJarCounts()
    {
        var host = Mod("host.jar", Jar.Read(
            ("fabric.mod.json", Jar.FabricMod("host", extra: ",\"jars\":[{\"file\":\"META-INF/jars/cloth.jar\"}]")),
            ("META-INF/jars/cloth.jar", Jar.Build(("fabric.mod.json", Jar.FabricMod("cloth-config"))))));
        Assert.Empty(CheckFabric(host, Fabric("user", """{ "cloth-config": "*" }""")));
    }

    [Fact]
    public void DisabledModsAreIgnoredAndDontSatisfy()
    {
        var api = Mod("fabric-api.jar", Jar.Read(("fabric.mod.json", Jar.FabricMod("fabric-api"))), enabled: false);
        Assert.Equal(ModIssueKind.MissingDependency, Assert.Single(CheckFabric(api, Fabric("modmenu", """{ "fabric-api": "*" }"""))).Kind);
    }

    [Fact]
    public void WrongMinecraftAndLoaderVersion()
    {
        var issues = CheckFabric(Fabric("newmod", """{ "minecraft": ">=1.21", "fabricloader": ">=0.17" }"""));
        Assert.Contains(issues, i => i.Kind == ModIssueKind.WrongMinecraftVersion);
        Assert.Contains(issues, i => i.Kind == ModIssueKind.WrongLoaderVersion);
    }

    [Fact]
    public void WrongLoader()
    {
        var forgeMod = Mod("jei.jar", Jar.Read(("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"jei\"\nversion=\"1\"\n")));
        Assert.Equal(ModIssueKind.WrongLoader, Assert.Single(CheckFabric(forgeMod)).Kind);
        // …but NeoForge 1.20.1 loads Forge mods.
        Assert.Empty(ModChecker.Check([forgeMod], ComponentUids.NeoForge, "47.1.106", "1.20.1"));
        // Quilt loads Fabric mods.
        Assert.Empty(ModChecker.Check([Fabric("sodium")], ComponentUids.QuiltLoader, "0.26", "1.20.1"));
    }

    [Fact]
    public void Duplicates()
    {
        var a = Mod("sodium-0.5.jar", Jar.Read(("fabric.mod.json", Jar.FabricMod("sodium", "0.5"))));
        var b = Mod("sodium-0.6.jar", Jar.Read(("fabric.mod.json", Jar.FabricMod("sodium", "0.6"))));
        var issue = Assert.Single(CheckFabric(a, b));
        Assert.Equal(ModIssueKind.Duplicate, issue.Kind);
    }

    [Fact]
    public void Breaks()
    {
        var issues = CheckFabric(Fabric("sodium", extra: ",\"breaks\":{\"optifabric\":\"*\"}"), Fabric("optifabric"));
        Assert.Equal(ModIssueKind.Incompatible, Assert.Single(issues).Kind);
    }

    [Fact]
    public void DependencyVersionMismatch()
    {
        var issues = CheckFabric(Fabric("fabric-api", version: "0.80.0"), Fabric("mod", """{ "fabric-api": ">=0.90.0" }"""));
        Assert.Equal(ModIssueKind.DependencyVersion, Assert.Single(issues).Kind);
    }

    [Fact]
    public void NewestProviderWins_OldBundledCopyDoesNotWarn()
    {
        // A mod bundling an old Fabric API module must not hide the newer one from Fabric API itself.
        var bundler = Mod("coroutil.jar", Jar.Read(
            ("fabric.mod.json", Jar.FabricMod("coroutil", extra: ",\"jars\":[{\"file\":\"META-INF/jars/renderer.jar\"}]")),
            ("META-INF/jars/renderer.jar", Jar.Build(("fabric.mod.json", Jar.FabricMod("fabric-renderer-api-v1", "3.0.1"))))));
        var api = Mod("fabric-api.jar", Jar.Read(
            ("fabric.mod.json", Jar.FabricMod("fabric-api", "0.92.2", extra: ",\"jars\":[{\"file\":\"META-INF/jars/renderer.jar\"}]")),
            ("META-INF/jars/renderer.jar", Jar.Build(("fabric.mod.json", Jar.FabricMod("fabric-renderer-api-v1", "3.2.1"))))));
        Assert.Empty(CheckFabric(bundler, api, Fabric("indium", """{ "fabric-renderer-api-v1": ">=3.2.0" }""")));
    }

    [Fact]
    public void OptiFineOnFabric()
    {
        var of = Mod("OptiFine.jar", Jar.Read(("optifine/Patcher.class", "x")));
        var issues = CheckFabric(of, Fabric("sodium"));
        Assert.Contains(issues, i => i.Kind == ModIssueKind.OptiFineNeedsOptiFabric);
        Assert.Contains(issues, i => i.Kind == ModIssueKind.KnownConflict);
    }

    [Fact]
    public void VanillaWithMods() =>
        Assert.Equal(ModIssueKind.NoLoader, Assert.Single(ModChecker.Check([Fabric("sodium")], null, null, "1.20.1")).Kind);
}

public class ContentServiceTests
{
    private static RemoteVersion V(string project, string id, ReleaseChannel channel, int day, string mc = "1.20.1", string loader = "fabric", params RemoteDependency[] deps) =>
        new(ContentPlatform.Modrinth, project, id, id, id)
        {
            Channel = channel,
            Published = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
            GameVersions = [mc],
            Loaders = [loader],
            Files = [new RemoteFile($"{project}-{id}.jar", 3) { Url = $"https://cdn.example/{project}/{id}.jar", Primary = true }],
            Dependencies = deps,
        };

    [Fact]
    public void PickBest_PrefersNewestReleaseForTheTarget()
    {
        var target = new ContentTarget(ContentKind.Mod, "1.20.1", ["fabric"]);
        var best = ContentService.PickBest(
        [
            V("p", "beta-new", ReleaseChannel.Beta, 20),
            V("p", "rel-old", ReleaseChannel.Release, 5),
            V("p", "rel-wrong-mc", ReleaseChannel.Release, 25, mc: "1.21"),
            V("p", "rel-forge", ReleaseChannel.Release, 25, loader: "forge"),
        ], target);
        Assert.Equal("rel-old", best?.Id);
    }

    [Fact]
    public void PickBest_QuiltPrefersQuiltBuildOnTheSameDay()
    {
        var target = new ContentTarget(ContentKind.Mod, "1.20.1", ["quilt", "fabric"]);
        var fabric = V("p", "fabric", ReleaseChannel.Release, 10, loader: "fabric") with { Published = new DateTimeOffset(2026, 1, 10, 12, 0, 1, TimeSpan.Zero) };
        var quilt = V("p", "quilt", ReleaseChannel.Release, 10, loader: "quilt");
        Assert.Equal("quilt", ContentService.PickBest([fabric, quilt], target)?.Id);
    }

    [Theory]
    [InlineData("architectury-api", "architectury")]
    [InlineData("cloth-config", "cloth-config")]
    [InlineData("fabric-language-kotlin", "fabric_language_kotlin")]
    [InlineData("ftb-library-fabric", "ftb_library")]
    public void SlugCandidates_CoverCommonModIds(string slug, string modId) =>
        Assert.Contains(modId, ContentService.SlugCandidates(slug));

    [Fact]
    public void Targets()
    {
        var m = new InstanceManifest { Components = [new() { Uid = ComponentUids.Minecraft, Version = "1.20.1" }, new() { Uid = ComponentUids.NeoForge, Version = "47.1.106" }] };
        Assert.Equal(["neoforge", "forge"], ContentService.TargetFor(m, ContentKind.Mod).Loaders);
        Assert.Equal(["datapack"], ContentService.TargetFor(m, ContentKind.DataPack).Loaders);
        Assert.Empty(ContentService.TargetFor(m, ContentKind.ResourcePack).Loaders);
    }

    [Fact]
    public async Task Plan_ResolvesRequiredDependenciesOnce_AndSkipsInstalled()
    {
        var ct = TestContext.Current.CancellationToken;
        var platform = new FakePlatform();
        platform.Add(V("app", "app-1", ReleaseChannel.Release, 1, deps: [new(RemoteDependencyKind.Required, "lib", null), new(RemoteDependencyKind.Required, "api", null), new(RemoteDependencyKind.Optional, "extra", null)]));
        platform.Add(V("lib", "lib-1", ReleaseChannel.Release, 1, deps: [new(RemoteDependencyKind.Required, "api", null)]));
        platform.Add(V("api", "api-1", ReleaseChannel.Release, 1));
        platform.Add(V("extra", "extra-1", ReleaseChannel.Release, 1));

        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = new ContentService([platform], new Core.Net.DownloadEngine(new HttpClient()), Core.Net.MirrorTable.Official);
            using var manager = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            var target = new ContentTarget(ContentKind.Mod, "1.20.1", ["fabric"]);
            var location = new ContentLocation(ContentKind.Mod);

            var plan = await service.PlanAsync(manager, target, platform.Versions["app"][0], location, ct: ct);
            Assert.Equal(["app", "lib", "api"], plan.Files.Select(f => f.Version.ProjectId));
            Assert.Equal(["lib", "api"], plan.Dependencies.Select(f => f.Version.ProjectId));
            Assert.Equal("extra", Assert.Single(plan.Optional).Id);

            // "api" already installed from this platform → not planned again.
            Jar.Write(Path.Combine(dir, "mods"), "api-api-1.jar", ("fabric.mod.json", Jar.FabricMod("api")));
            await manager.UpdateAsync(index =>
            {
                index.Files["mods/api-api-1.jar"] = new ContentRecord { Source = new RemoteRef { Platform = ContentPlatform.Modrinth, ProjectId = "api", VersionId = "api-1" } };
                return false;
            }, ct);
            plan = await service.PlanAsync(manager, target, platform.Versions["app"][0], location, ct: ct);
            Assert.Equal(["app", "lib"], plan.Files.Select(f => f.Version.ProjectId));

            // A mod id present on disk (added by hand) matching the dependency's slug also counts.
            plan = await service.PlanAsync(manager, target, platform.Versions["app"][0], location, installedIds: new HashSet<string> { "lib" }, ct: ct);
            Assert.Equal(["app"], plan.Files.Select(f => f.Version.ProjectId));

            // Installing the exact installed version again is a no-op.
            plan = await service.PlanAsync(manager, target, platform.Versions["api"][0], location, ct: ct);
            Assert.Empty(plan.Files);
            Assert.Single(plan.AlreadyInstalled);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Scan_HashesOnce_TogglesAndKeepsRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Jar.Write(Path.Combine(dir, "mods"), "a.jar", ("fabric.mod.json", Jar.FabricMod("a")));
            Jar.Write(Path.Combine(dir, "mods"), "b.jar.disabled", ("fabric.mod.json", Jar.FabricMod("b")));
            File.WriteAllText(Path.Combine(dir, "mods", "notes.txt"), "ignored");

            using var manager = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            var files = await manager.ScanAsync(new ContentLocation(ContentKind.Mod), ct);
            Assert.Equal(["a mod", "b mod"], files.Select(f => f.DisplayName));
            Assert.Equal([true, false], files.Select(f => f.Enabled));
            Assert.All(files, f => Assert.NotNull(f.Hashes));
            Assert.Equal("mods/b.jar", files[1].Key);

            await manager.UpdateAsync(i => { i.Files["mods/a.jar"].Pinned = true; return true; }, ct);
            var toggled = await manager.SetEnabledAsync(files[0], false, ct);
            Assert.EndsWith("a.jar.disabled", toggled.Path);

            using var reopened = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            var again = await reopened.ScanAsync(new ContentLocation(ContentKind.Mod), ct);
            Assert.True(again.Single(f => f.Key == "mods/a.jar").Record.Pinned);
            Assert.False(again.Single(f => f.Key == "mods/a.jar").Enabled);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task Review_ReadsNewManifests_UpdatesWhatTheyNeed_AndSkipsWhatCantWork()
    {
        var ct = TestContext.Current.CancellationToken;
        var jars = new Dictionary<string, byte[]>
        {
            // app 2 needs a newer lib and a newer Fabric Loader; neither is on the platform, only in its fabric.mod.json.
            ["https://cdn.test/app-2.jar"] = Jar.Build(("fabric.mod.json", Jar.FabricMod("app", "2.0.0", """{ "lib": ">=1.5.0", "fabricloader": ">=0.16.0" }"""))),
            ["https://cdn.test/lib-16.jar"] = Jar.Build(("fabric.mod.json", Jar.FabricMod("lib", "1.6.0"))),
            // tool 2 is tagged for 1.20.1 but its own manifest wants a newer Minecraft.
            ["https://cdn.test/tool-2.jar"] = Jar.Build(("fabric.mod.json", Jar.FabricMod("tool", "2.0.0", """{ "minecraft": ">=1.21" }"""))),
        };
        RemoteVersion Remote(string project, string id, string number, int day) => new(ContentPlatform.Modrinth, project, id, id, number)
        {
            Published = new DateTimeOffset(2026, 1, day, 0, 0, 0, TimeSpan.Zero),
            GameVersions = ["1.20.1"],
            Loaders = ["fabric"],
            Files = [new RemoteFile($"{project}-{number}.jar", 0) { Url = $"https://cdn.test/{id}.jar", Primary = true }],
        };
        var platform = new FakePlatform();
        platform.Add(Remote("lib", "lib-1", "1.0.0", 1));
        platform.Add(Remote("lib", "lib-16", "1.6.0", 5));
        var app2 = Remote("app", "app-2", "2.0.0", 5);
        var tool2 = Remote("tool", "tool-2", "2.0.0", 5);

        var dir = Path.Combine(Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var mods = Path.Combine(dir, "mods");
            Jar.Write(mods, "lib-1.0.0.jar", ("fabric.mod.json", Jar.FabricMod("lib", "1.0.0")));
            Jar.Write(mods, "app-1.0.0.jar", ("fabric.mod.json", Jar.FabricMod("app", "1.0.0", """{ "lib": ">=1.0.0" }""")));
            Jar.Write(mods, "tool-1.0.0.jar", ("fabric.mod.json", Jar.FabricMod("tool", "1.0.0")));
            using var http = new HttpClient(new Serve(jars));
            using var service = new ContentService([platform], new Core.Net.DownloadEngine(http), Core.Net.MirrorTable.Official);
            using var manager = new ContentManager(dir, Path.Combine(dir, ".fluorite"));
            var location = new ContentLocation(ContentKind.Mod);
            await manager.ScanAsync(location, ct);
            await manager.UpdateAsync(index =>
            {
                foreach (var name in new[] { "lib", "app", "tool" })
                    index.Files[$"mods/{name}-1.0.0.jar"].Source = new RemoteRef { Platform = ContentPlatform.Modrinth, ProjectId = name, VersionId = name + "-1", Published = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) };
                return true;
            }, ct);
            var files = await manager.ScanAsync(location, ct);
            var manifest = new InstanceManifest { Components = [new() { Uid = ComponentUids.Minecraft, Version = "1.20.1" }, new() { Uid = ComponentUids.FabricLoader, Version = "0.15.0" }] };
            var target = ContentService.TargetFor(manifest, ContentKind.Mod);
            ContentUpdate Update(string name, RemoteVersion v) => new(files.Single(f => f.Key == $"mods/{name}-1.0.0.jar"), v);

            var plan = await service.PlanUpdatesAsync(manager, target, [Update("app", app2), Update("tool", tool2)], ct: ct);
            await service.StageAsync(manager, plan, ct: ct);
            var review = await service.ReviewAsync(manager, plan, target, manifest, new FakeLoader("0.16.9", "0.16.5", "0.15.11", "0.15.0"), ct: ct);

            // The newer lib app 2 needs came along, for app.
            var lib = Assert.Single(review.Added);
            Assert.Equal(("lib-16", "mods/lib-1.0.0.jar", "Modrinth:app"), (lib.Version.Id, lib.ReplacesKey, Assert.Single(lib.NeededBy)));
            // The newest loader every mod accepts.
            Assert.Equal("0.16.9", review.Loader?.To);
            // With it, only tool 2 can't work; without it, app 2 can't either (and its lib update goes with it).
            var problem = Assert.Single(review.Problems(withLoader: true));
            Assert.Equal((ModIssueKind.WrongMinecraftVersion, "tool-2"), (problem.Issue.Kind, problem.Cause?.Version.Id));
            Assert.Equal(["tool-2"], review.SkipSet(withLoader: true).Select(f => f.Version.Id));
            Assert.Equal(["app-2", "tool-2", "lib-16"], review.SkipSet(withLoader: false).Select(f => f.Version.Id));

            review.Skip(withLoader: true);
            await ContentService.CommitAsync(manager, plan);
            Assert.Equal(["app-2.0.0.jar", "lib-1.6.0.jar", "tool-1.0.0.jar"], Directory.GetFiles(mods).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private sealed class Serve(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class FakeLoader(params string[] versions) : ILoaderProvider
    {
        public string Uid => ComponentUids.FabricLoader;
        public string DisplayName => "Fabric Loader";

        public Task<IReadOnlyList<LoaderVersion>> GetVersionsAsync(string minecraftVersion, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LoaderVersion>>(versions.Select(v => new LoaderVersion(Uid, minecraftVersion, v, Recommended: false)).ToList());

        public Task<string> InstallAsync(string minecraftVersion, string loaderVersion, LoaderInstallContext ctx, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakePlatform : IContentPlatform
    {
        public Dictionary<string, List<RemoteVersion>> Versions { get; } = [];

        public void Add(RemoteVersion v)
        {
            if (!Versions.TryGetValue(v.ProjectId, out var list))
                Versions[v.ProjectId] = list = [];
            list.Add(v);
        }

        public ContentPlatform Platform => ContentPlatform.Modrinth;
        public TextFormat TextFormat => TextFormat.Markdown;

        public Task<RemoteProject> GetProjectAsync(string projectId, CancellationToken ct = default) =>
            Task.FromResult(new RemoteProject(Platform, projectId, projectId, projectId.ToUpperInvariant()));

        public async Task<IReadOnlyList<RemoteProject>> GetProjectsAsync(IReadOnlyCollection<string> projectIds, CancellationToken ct = default)
        {
            var list = new List<RemoteProject>();
            foreach (var id in projectIds)
                list.Add(await GetProjectAsync(id, ct));
            return list;
        }

        public Task<IReadOnlyList<RemoteVersion>> GetVersionsAsync(string projectId, ContentTarget? target, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RemoteVersion>>(Versions.GetValueOrDefault(projectId) ?? []);

        public Task<RemoteVersion> GetVersionAsync(string projectId, string versionId, CancellationToken ct = default) =>
            Task.FromResult(Versions.Values.SelectMany(v => v).First(v => v.Id == versionId));

        public Task<ContentSearchResult> SearchAsync(ContentSearchQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> GetDescriptionAsync(RemoteProject project, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<string?> GetChangelogAsync(RemoteVersion version, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<RemoteCategory>> GetCategoriesAsync(ContentKind kind, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RemoteCategory>>([]);
        public Task<IReadOnlyList<IdentifiedFile>> IdentifyAsync(IReadOnlyCollection<ContentHashes> files, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<IdentifiedFile>>([]);
        public Task<IReadOnlyDictionary<string, RemoteVersion>> GetUpdatesAsync(IReadOnlyCollection<InstalledRef> installed, ContentTarget target, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, RemoteVersion>>(new Dictionary<string, RemoteVersion>());
    }
}

public class OptiFineOfficialTests
{
    private const string Page = """
        <h2>Minecraft 1.20.1</h2>
        <table class='downloadTable'>
          <tr class='downloadLine downloadLineMain'>
            <td class='colFile'>OptiFine HD U I6</td>
            <td class='colDownload'><a href="http://adfoc.us/serve/sitelinks/?id=475250&url=http://optifine.net/adloadx?f=OptiFine_1.20.1_HD_U_I6.jar&x=c222">Download</a></td>
            <td class='colMirror'><a href="http://optifine.net/adloadx?f=OptiFine_1.20.1_HD_U_I6.jar">(Mirror)</a></td>
            <td class='colForge'>Forge 47.1.0</td>
            <td class='colDate'>01.01.2024</td>
          </tr>
          <tr class='downloadLine downloadLinePreview'>
            <td class='colFile'>OptiFine HD U K2 pre5</td>
            <td class='colMirror'><a href="http://optifine.net/adloadx?f=preview_OptiFine_26.2_HD_U_K2_pre5.jar">(Mirror)</a></td>
            <td class='colForge'>Forge N/A</td>
          </tr>
          <tr class='downloadLine'>
            <td class='colMirror'><a href="http://optifine.net/adloadx?f=OptiFine_1.8.0_HD_U_I7.jar">(Mirror)</a></td>
          </tr>
        </table>
        """;

    [Fact]
    public void ParsesRows()
    {
        var builds = OptiFineOfficialSite.ParseDownloadsPage(Page);
        Assert.Equal(3, builds.Count);
        Assert.Equal(new OptiFineBuild("1.20.1", "HD_U_I6", "OptiFine_1.20.1_HD_U_I6.jar", false) { Forge = "Forge 47.1.0" }, builds[0]);
        Assert.Equal(("26.2", "HD_U_K2_pre5", true, (string?)null), (builds[1].MinecraftVersion, builds[1].Version, builds[1].IsPreview, builds[1].Forge));
        Assert.Equal("1.8", builds[2].MinecraftVersion);
    }

    [Fact]
    public void FileNames()
    {
        Assert.Equal("OptiFine_1.20.1_HD_U_I6.jar", OptiFineOfficialSite.FileNameFor("1.20.1", "HD_U_I6", null));
        Assert.Equal("preview_OptiFine_26.2_HD_U_K2_pre5.jar", OptiFineOfficialSite.FileNameFor("26.2", "HD_U_K2_pre5", null));
        var known = OptiFineOfficialSite.ParseDownloadsPage(Page);
        Assert.Equal("OptiFine_1.8.0_HD_U_I7.jar", OptiFineOfficialSite.FileNameFor("1.8", "HD_U_I7", known));
    }
}
