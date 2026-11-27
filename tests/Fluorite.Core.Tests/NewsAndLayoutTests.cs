using System.Net;
using System.Text;
using Fluorite.Core.Instances;
using Fluorite.Core.News;

namespace Fluorite.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fluorite-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}

internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(respond(request));

    public static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public class MojangNewsClientTests
{
    private const string PatchNotes = """
    { "version": 1, "entries": [
      { "title": "Minecraft 26.4 Snapshot 7", "version": "26.4-snapshot-7", "type": "snapshot",
        "image": { "title": "x.jpg", "url": "/v2/images/x.jpg" }, "contentPath": "javaPatchNotes/26-4-snapshot-7.json",
        "id": "26-4-snapshot-7", "date": "2026-11-03T13:08:56Z", "shortText": "Calcite!" } ] }
    """;

    private const string News = """
    { "version": 1, "entries": [
      { "title": "Java thing", "tag": "News", "date": "2026-10-30", "text": "t", "newsType": ["Java", "News page"], "id": "a", "readMoreLink": "https://www.minecraft.net/a" },
      { "title": "Bedrock thing", "date": "2026-10-29", "newsType": ["Bedrock"], "id": "b" } ] }
    """;

    [Fact]
    public async Task ParsesFeeds_FiltersJavaNews_AndFallsBackToCacheOffline()
    {
        using var dir = new TempDir();
        var online = true;
        using var http = new HttpClient(new StubHandler(req =>
        {
            if (!online)
                throw new HttpRequestException("offline");
            return req.RequestUri!.AbsolutePath switch
            {
                "/v2/javaPatchNotes.json" => StubHandler.Json(PatchNotes),
                "/v2/news.json" => StubHandler.Json(News),
                "/v2/javaPatchNotes/26-4-snapshot-7.json" => StubHandler.Json("""{ "id": "26-4-snapshot-7", "title": "S7", "body": "<p>Hi</p>" }"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        }));
        var client = new MojangNewsClient(http, dir.Path);
        var ct = TestContext.Current.CancellationToken;

        var notes = await client.GetPatchNotesAsync(ct);
        var note = Assert.Single(notes);
        Assert.Equal("26.4-snapshot-7", note.Version);
        Assert.Equal(new DateTimeOffset(2026, 11, 3, 13, 8, 56, TimeSpan.Zero), note.Date);
        Assert.Equal("https://launchercontent.mojang.com/v2/images/x.jpg", MojangNewsClient.ImageUri(note.Image)?.ToString());
        Assert.Null(MojangNewsClient.ImageUri(null));
        Assert.Null(MojangNewsClient.ImageUri(new NewsImage { Url = "" }));

        Assert.Equal("Java thing", Assert.Single(await client.GetJavaNewsAsync(ct)).Title);
        Assert.Equal("<p>Hi</p>", (await client.GetPatchNoteAsync(note, ct))!.Body);

        online = false;
        Assert.Single(await client.GetPatchNotesAsync(ct));
        Assert.Equal("<p>Hi</p>", (await client.GetPatchNoteAsync(note, ct))!.Body);
    }

    [Fact]
    public async Task Offline_WithoutCache_ReturnsEmpty()
    {
        using var dir = new TempDir();
        using var http = new HttpClient(new StubHandler(_ => throw new HttpRequestException("offline")));
        Assert.Empty(await new MojangNewsClient(http, dir.Path).GetPatchNotesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task NullEntries_AreNothingToShow_NotAFailure()
    {
        using var dir = new TempDir();
        var answer = """{ "entries": null }""";
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(answer)));
        var client = new MojangNewsClient(http, dir.Path);
        var ct = TestContext.Current.CancellationToken;

        Assert.Empty(await client.GetPatchNotesAsync(ct));
        Assert.Empty(await client.GetJavaNewsAsync(ct));

        answer = """{ "entries": [ null, { "id": "a", "title": "Java thing", "newsType": ["Java"] } ] }""";
        Assert.Equal("a", Assert.Single(await client.GetPatchNotesAsync(ct)).Id);
        Assert.Equal("a", Assert.Single(await client.GetJavaNewsAsync(ct)).Id);
    }

    [Fact]
    public async Task AnAnswerWithNothingToShow_IsNotSavedOverTheOfflineCopy()
    {
        using var dir = new TempDir();
        var answer = PatchNotes;
        using var http = new HttpClient(new StubHandler(_ => StubHandler.Json(answer)));
        var client = new MojangNewsClient(http, dir.Path);
        var ct = TestContext.Current.CancellationToken;

        Assert.Single(await client.GetPatchNotesAsync(ct));
        var saved = await File.ReadAllTextAsync(Path.Combine(dir.Path, "patch-notes.json"), ct);

        foreach (var bad in new[] { "{}", """{ "entries": [] }""", """{ "entries": null }""" })
        {
            answer = bad;
            Assert.Equal("26-4-snapshot-7", Assert.Single(await client.GetPatchNotesAsync(ct)).Id); // The offline copy.
            Assert.Equal(saved, await File.ReadAllTextAsync(Path.Combine(dir.Path, "patch-notes.json"), ct));
        }
    }

    [Theory]
    [InlineData("http://")]
    [InlineData("http://[x")]
    public void AnAddressThatIsntOne_IsNoPicture(string url) =>
        Assert.Null(MojangNewsClient.ImageUri(new NewsImage { Url = url }));

    // Each checked against minecraft.net (2026-11-27).
    [Theory]
    [InlineData("26.3", "minecraft-java-edition-26-3")]
    [InlineData("26.4-snapshot-9", "minecraft-26-4-snapshot-9")]
    [InlineData("26.4-pre-1", "minecraft-26-4-pre-release-1")]
    [InlineData("26.3-rc-3", "minecraft-26-3-release-candidate-3")]
    [InlineData("1.21.4-pre3", "minecraft-1-21-4-pre-release-3")]
    [InlineData("1.21.8-rc1", "minecraft-1-21-8-release-candidate-1")]
    [InlineData("24w33a", "minecraft-snapshot-24w33a")]
    public void PatchNote_Article_IsWorkedOutFromTheVersion(string version, string slug) =>
        Assert.Equal("https://www.minecraft.net/article/" + slug, MojangNewsClient.ArticleUri(new PatchNoteEntry { Version = version })?.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.14 Pre-Release 5")]
    [InlineData("combat-test-8c")]
    public void PatchNote_AnUnknownVersion_HasNoArticle(string? version) =>
        Assert.Null(MojangNewsClient.ArticleUri(new PatchNoteEntry { Version = version }));

    [Fact]
    public void PatchNote_WikiPage_IsTheVersionsOwn()
    {
        Assert.Equal("https://minecraft.wiki/w/Java_Edition_26.4-snapshot-10", MojangNewsClient.WikiUri(new PatchNoteEntry { Version = "26.4-snapshot-10" })?.AbsoluteUri);
        Assert.Equal("https://minecraft.wiki/w/Java_Edition_1.14_Pre-Release_5", MojangNewsClient.WikiUri(new PatchNoteEntry { Version = "1.14 Pre-Release 5" })?.AbsoluteUri);
        Assert.Equal("https://zh.minecraft.wiki/w/Java%E7%89%8826.4-pre-1", MojangNewsClient.WikiUri(new PatchNoteEntry { Version = "26.4-pre-1" }, chinese: true)?.AbsoluteUri);
        Assert.Null(MojangNewsClient.WikiUri(new PatchNoteEntry()));
    }
}

public class FluoriteLayoutTests
{
    [Theory]
    [InlineData("My World", "My-World")]
    [InlineData("  spaced   name  ", "spaced-name")]
    [InlineData("中文名字", "instance")]
    [InlineData("Fabric 1.21 / test", "Fabric-1.21-test")]
    public void FolderNames_AreAsciiSafe(string name, string expected)
    {
        using var dir = new TempDir();
        Assert.Equal(expected, FluoriteLayout.UniqueFolderName(dir.Path, name));
    }

    [Fact]
    public async Task Create_Duplicate_RoundTrip()
    {
        using var dir = new TempDir();
        var layout = new FluoriteLayout(dir.Path);
        var ct = TestContext.Current.CancellationToken;

        var original = await layout.CreateAsync(new InstanceManifest
        {
            Name = "Survival",
            Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.21.4" }],
            Stats = new InstanceStats { TotalPlaySeconds = 3600, LaunchCount = 3 },
        }, ct);
        await File.WriteAllTextAsync(Path.Combine(original.GameDirectory, "options.txt"), "fov:90", ct);
        Directory.CreateDirectory(original.NativesDirectory);
        await File.WriteAllTextAsync(Path.Combine(original.NativesDirectory, "lwjgl.dll"), "x", ct);
        await File.WriteAllTextAsync(Path.Combine(original.MetadataDirectory, "export.json"), "{}", ct);

        var copy = await layout.DuplicateAsync(original, "Survival (copy)", ct);

        Assert.NotEqual(original.Directory, copy.Directory);
        Assert.Equal("Survival (copy)", copy.Name);
        Assert.Equal(0, copy.Manifest.Stats.LaunchCount);
        Assert.Equal("1.21.4", copy.LaunchVersionId);
        Assert.Equal("fov:90", await File.ReadAllTextAsync(Path.Combine(copy.GameDirectory, "options.txt"), ct));
        Assert.False(Directory.Exists(copy.NativesDirectory)); // natives are re-extracted, never copied
        Assert.False(File.Exists(Path.Combine(copy.MetadataDirectory, "export.json"))); // the copy hasn't been exported

        var all = await layout.GetInstancesAsync(ct);
        Assert.Equal(2, all.Count);
        Assert.Equal(3, all.Single(i => i.Id == original.Id).Manifest.Stats.LaunchCount);
    }

    [Fact]
    public async Task CorruptManifest_DoesNotHideOtherInstances()
    {
        using var dir = new TempDir();
        var layout = new FluoriteLayout(dir.Path);
        var ct = TestContext.Current.CancellationToken;
        await layout.CreateAsync(new InstanceManifest { Name = "Good", Components = [new ComponentRef { Uid = ComponentUids.Minecraft, Version = "1.20.1" }] }, ct);
        var bad = Path.Combine(layout.InstancesDir, "Bad");
        Directory.CreateDirectory(bad);
        await File.WriteAllTextAsync(Path.Combine(bad, FluoriteLayout.ManifestFileName), "{ not json", ct);

        Assert.Equal("Good", Assert.Single(await layout.GetInstancesAsync(ct)).Name);
    }
}
