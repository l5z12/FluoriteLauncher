using System.Net;
using Fluorite.Core.Net;

namespace Fluorite.Core.Tests;

public sealed class DownloadEngineTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("fluorite-dl-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class FixedHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("hello"u8.ToArray()) });
        }
    }

    private sealed class Collect : IProgress<DownloadProgress>
    {
        public readonly List<DownloadProgress> Reports = [];

        public void Report(DownloadProgress value)
        {
            lock (Reports)
                Reports.Add(value);
        }
    }

    [Fact]
    public async Task Progress_CountsOnlyMissingFiles_AndNamesTheFile()
    {
        var handler = new FixedHandler();
        var engine = new DownloadEngine(new HttpClient(handler));
        var present = Path.Combine(_dir, "present.bin");
        await File.WriteAllTextAsync(present, "hello", TestContext.Current.CancellationToken);
        var uri = new Uri("https://example.invalid/f");
        var progress = new Collect();

        await engine.DownloadAllAsync(
        [
            new DownloadItem([uri], present, Size: 5) { DisplayName = "present" },
            new DownloadItem([uri], Path.Combine(_dir, "a.bin"), Size: 5) { DisplayName = "a" },
            new DownloadItem([uri], Path.Combine(_dir, "b.bin"), Size: 5) { DisplayName = "b" },
        ], progress, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests);
        Assert.Contains(progress.Reports, p => p.Checking && p.TotalFiles == 3);
        var downloads = progress.Reports.Where(p => !p.Checking).ToList();
        Assert.All(downloads, p => Assert.Equal(2, p.TotalFiles));
        Assert.All(downloads, p => Assert.Equal(10, p.TotalBytes));
        Assert.Equal((2, 10L), (downloads[^1].CompletedFiles, downloads[^1].DownloadedBytes));
        Assert.Contains(downloads, p => p.Current is "a" or "b");
        Assert.DoesNotContain(downloads, p => p.Current == "present");
    }
}
