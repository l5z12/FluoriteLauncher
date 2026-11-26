using Fluorite.Core.Net;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class VersionManifestTests
{
    private const string Manifest = """
    { "latest": { "release": "1.21.4", "snapshot": "25w01a" },
      "versions": [ { "id": "1.21.4", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/x/1.21.4.json" } ] }
    """;

    private static string CachePath(TempDir dir) => Path.Combine(new GameDirectories(dir.Path).Versions, "version_manifest_v2.json");

    [Fact]
    public async Task AManifestListingNoVersions_NeverReplacesTheGoodOne()
    {
        using var dir = new TempDir();
        var answer = Manifest;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(answer)));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("1.21.4", (await versions.GetManifestAsync(ct: ct)).Latest?.Release);
        var onDisk = await File.ReadAllTextAsync(CachePath(dir), ct);

        // Every source answering "{}" with 200: the copy on disk is used, and neither it nor the one in memory changes.
        answer = "{}";
        Assert.Equal("1.21.4", (await versions.GetManifestAsync(refresh: true, ct)).Latest?.Release);
        Assert.Single((await versions.GetManifestAsync(ct: ct)).Versions);
        Assert.Equal(onDisk, await File.ReadAllTextAsync(CachePath(dir), ct));

        // Not JSON at all (an error page), or versions but no latest release: the same.
        foreach (var bad in new[] { "<html>Bad gateway</html>", """{ "versions": [ { "id": "1.0", "type": "release" } ] }""" })
        {
            answer = bad;
            Assert.Equal("1.21.4", (await versions.GetManifestAsync(refresh: true, ct)).Latest?.Release);
            Assert.Equal(onDisk, await File.ReadAllTextAsync(CachePath(dir), ct));
        }
    }

    [Fact]
    public async Task AMirrorAnsweringNothingUsable_FallsThroughToTheNextSource()
    {
        using var dir = new TempDir();
        var asked = new List<string>();
        using var http = new HttpClient(new StubHandler(req =>
        {
            asked.Add(req.RequestUri!.Host);
            return StubHandler.Json(req.RequestUri.Host == "piston-meta.mojang.com" ? Manifest : "{}");
        }));
        // BMCLAPI first, as the user chose: it answers "{}", and the official source has it.
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, new MirrorTable(DownloadSourceKind.Bmclapi));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("1.21.4", (await versions.GetManifestAsync(ct: ct)).Latest?.Release);
        Assert.Equal(["bmclapi2.bangbang93.com", "piston-meta.mojang.com"], asked);
        Assert.Contains("1.21.4", await File.ReadAllTextAsync(CachePath(dir), ct));
    }

    [Fact]
    public async Task ACopyThatCantBeSaved_DoesntUndoTheDownload()
    {
        using var dir = new TempDir();
        var answer = Manifest;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(answer)));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);
        var ct = TestContext.Current.CancellationToken;
        await versions.GetManifestAsync(ct: ct);
        var onDisk = await File.ReadAllTextAsync(CachePath(dir), ct);

        // Another launcher sharing the folder holds the file open.
        answer = Manifest.Replace("1.21.4", "1.21.5");
        using (new FileStream(CachePath(dir), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal("1.21.5", (await versions.GetManifestAsync(refresh: true, ct)).Latest?.Release);

        Assert.Equal(onDisk, await File.ReadAllTextAsync(CachePath(dir), ct));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(CachePath(dir))!, "*.tmp"));
    }

    [Fact]
    public async Task EntriesWithoutAnId_AreDropped()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("""
            { "latest": { "release": "1.21.4" }, "versions": [ null, { "type": "release" }, { "id": "1.21.4", "type": "release" } ] }
            """)));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);

        Assert.Equal("1.21.4", Assert.Single((await versions.GetManifestAsync(ct: TestContext.Current.CancellationToken)).Versions).Id);
    }

    [Fact]
    public async Task AManifestListingNoVersions_WithNothingCached_Throws()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("{}")));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);

        var ex = await Assert.ThrowsAsync<InvalidDataException>(() => versions.GetManifestAsync(ct: TestContext.Current.CancellationToken));
        Assert.True(VersionRepository.IsManifestUnavailable(ex)); // What the version pickers catch.
    }
}
