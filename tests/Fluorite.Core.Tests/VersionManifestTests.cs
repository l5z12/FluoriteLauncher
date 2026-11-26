using Fluorite.Core.Net;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class VersionManifestTests
{
    private const string Manifest = """
    { "latest": { "release": "1.21.4", "snapshot": "25w01a" },
      "versions": [ { "id": "1.21.4", "type": "release", "url": "https://piston-meta.mojang.com/v1/packages/x/1.21.4.json" } ] }
    """;

    [Fact]
    public async Task AManifestListingNoVersions_NeverReplacesTheGoodOne()
    {
        using var dir = new TempDir();
        var answer = Manifest;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(answer)));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("1.21.4", (await versions.GetManifestAsync(ct: ct)).Latest?.Release);

        // A mirror answering "{}" with 200: the copy on disk is used, and stays as it was.
        answer = "{}";
        Assert.Equal("1.21.4", (await versions.GetManifestAsync(refresh: true, ct)).Latest?.Release);
        Assert.Single((await versions.GetManifestAsync(ct: ct)).Versions);

        // Not JSON at all (an error page): the same.
        answer = "<html>Bad gateway</html>";
        Assert.Single((await versions.GetManifestAsync(refresh: true, ct)).Versions);
    }

    [Fact]
    public async Task AManifestListingNoVersions_WithNothingCached_Throws()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json("{}")));
        using var versions = new VersionRepository(new GameDirectories(dir.Path), http, MirrorTable.Official);

        await Assert.ThrowsAsync<InvalidDataException>(() => versions.GetManifestAsync(ct: TestContext.Current.CancellationToken));
    }
}
