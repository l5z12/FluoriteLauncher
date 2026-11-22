using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Fluorite.Core.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fluorite.Core.Net;

public sealed class DownloadEngineOptions
{
    public int MaxParallelism { get; set; } = 32;
    public int AttemptsPerSource { get; set; } = 3;
    public VerifyMode VerifyMode { get; set; } = VerifyMode.Fast;
}

/// <summary>
/// Parallel, hash-verifying, multi-source downloader. Files are written to <c>*.part</c> and atomically moved into place.
/// </summary>
public sealed class DownloadEngine(HttpClient http, DownloadEngineOptions? options = null, ILogger<DownloadEngine>? logger = null)
{
    private readonly DownloadEngineOptions _options = options ?? new DownloadEngineOptions();
    private readonly ILogger _logger = logger ?? NullLogger<DownloadEngine>.Instance;

    public static HttpClient CreateDefaultHttpClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            MaxConnectionsPerServer = 32,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            EnableMultipleHttp2Connections = true,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(FluoriteInfo.UserAgent);
        return client;
    }

    public async Task DownloadAllAsync(IEnumerable<DownloadItem> items, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        // Collapse duplicates targeting the same path (e.g. shared natives across patches).
        var all = items
            .GroupBy(i => Path.GetFullPath(i.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = _options.MaxParallelism, CancellationToken = ct };

        // Files already on disk are left out, so the progress shows only what is really downloaded.
        var list = await MissingAsync(all, progress, parallel).ConfigureAwait(false);

        var total = list.Count;
        long? totalBytes = list.All(i => i.Size is not null) ? list.Sum(i => i.Size!.Value) : null;
        var completed = 0;
        long downloaded = 0;
        string? current = null;
        var meter = new RateMeter();
        var failures = new System.Collections.Concurrent.ConcurrentBag<(DownloadItem, Exception)>();

        void Report(bool force = false)
        {
            if (progress is null)
                return;
            var bytes = Interlocked.Read(ref downloaded);
            if (meter.Sample(bytes, force) is { } rate)
                progress.Report(new DownloadProgress(Volatile.Read(ref completed), total, bytes, totalBytes, rate, Volatile.Read(ref current)));
        }

        Report(force: true);
        await Parallel.ForEachAsync(list, parallel, async (item, token) =>
        {
            try
            {
                Volatile.Write(ref current, item.DisplayName ?? Path.GetFileName(item.Path));
                await DownloadOneAsync(item, n =>
                {
                    Interlocked.Add(ref downloaded, n);
                    Report();
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add((item, ex));
                CoreLog.DownloadFailed(_logger, ex, item.DisplayName ?? item.Path);
            }
            finally
            {
                Interlocked.Increment(ref completed);
                Report();
            }
        }).ConfigureAwait(false);
        Report(force: true);

        if (!failures.IsEmpty)
        {
            var arr = failures.ToArray();
            var sample = string.Join(Environment.NewLine, arr.Take(5).Select(f => $"  {f.Item1.DisplayName ?? Path.GetFileName(f.Item1.Path)}: {f.Item2.Message}"));
            throw new DownloadFailedException($"{arr.Length} file(s) failed to download:{Environment.NewLine}{sample}", arr);
        }
    }

    /// <summary>The items that aren't on disk yet (or fail the check), in their original order.</summary>
    private async Task<List<DownloadItem>> MissingAsync(List<DownloadItem> items, IProgress<DownloadProgress>? progress, ParallelOptions parallel)
    {
        var missing = new bool[items.Count];
        var done = 0;
        var meter = new RateMeter();
        progress?.Report(new DownloadProgress(0, items.Count, 0, null, 0, Checking: true));
        await Parallel.ForAsync(0, items.Count, parallel, async (i, token) =>
        {
            try
            {
                missing[i] = !await IsSatisfiedAsync(items[i], token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                missing[i] = true; // Unreadable: downloading it again either fixes it or reports why not.
            }
            var n = Interlocked.Increment(ref done);
            if (progress is not null && meter.Sample(0, force: false) is not null)
                progress.Report(new DownloadProgress(n, items.Count, 0, null, 0, Checking: true));
        }).ConfigureAwait(false);
        return items.Where((_, i) => missing[i]).ToList();
    }

    /// <summary>Limits progress reports to about ten a second and smooths the transfer rate across them.</summary>
    private sealed class RateMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Lock _gate = new();
        private double _lastTime = double.NegativeInfinity;
        private long _lastBytes;
        private double _rate;

        /// <summary>The smoothed bytes per second when a report is due (always when <paramref name="force"/>), else null.</summary>
        public double? Sample(long bytes, bool force)
        {
            lock (_gate)
            {
                var now = _clock.Elapsed.TotalSeconds;
                var elapsed = now - _lastTime;
                if (!force && elapsed < 0.1)
                    return null;
                if (double.IsFinite(elapsed) && elapsed > 0)
                    _rate = 0.75 * _rate + 0.25 * Math.Max(0, (bytes - _lastBytes) / elapsed);
                _lastTime = now;
                _lastBytes = bytes;
                return _rate;
            }
        }
    }

    /// <summary>Whether the file already exists and is acceptable under the current <see cref="VerifyMode"/>.</summary>
    public async Task<bool> IsSatisfiedAsync(DownloadItem item, CancellationToken ct)
    {
        var fi = new FileInfo(item.Path);
        if (!fi.Exists)
            return false;
        if (item.Size is { } size && size > 0 && fi.Length != size)
            return false;

        var mustHash = _options.VerifyMode == VerifyMode.Full || (item.Size is null && item.Sha1 is not null);
        if (!mustHash || item.Sha1 is null)
            return true;

        var actual = await HashUtil.Sha1FileAsync(item.Path, ct).ConfigureAwait(false);
        return MatchesHash(item, actual);
    }

    private static bool MatchesHash(DownloadItem item, string actual) =>
        (item.Sha1 is null && (item.AlternativeSha1 is null || item.AlternativeSha1.Count == 0))
        || HashUtil.HashEquals(item.Sha1, actual)
        || (item.AlternativeSha1?.Any(h => HashUtil.HashEquals(h, actual)) ?? false);

    private async Task DownloadOneAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        if (item.Sources.Count == 0)
            throw new InvalidOperationException($"No download source for '{item.Path}' (it must be generated locally).");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(item.Path))!);
        var part = item.Path + ".part";
        Exception? last = null;

        foreach (var source in item.Sources)
        {
            for (var attempt = 1; attempt <= _options.AttemptsPerSource; attempt++)
            {
                long countedThisAttempt = 0;
                try
                {
                    using var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
                    {
                        last = new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} ({source})", null, response.StatusCode);
                        break; // Next source; retrying won't help.
                    }
                    response.EnsureSuccessStatusCode();

                    using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
                    await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                    await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                    {
                        var buffer = new byte[1 << 16];
                        int read;
                        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        while (true)
                        {
                            idle.CancelAfter(TimeSpan.FromSeconds(30)); // stall detection
                            read = await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
                            if (read == 0)
                                break;
                            sha1.AppendData(buffer, 0, read);
                            await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                            countedThisAttempt += read;
                            onBytes(read);
                        }
                    }

                    var actual = Convert.ToHexStringLower(sha1.GetHashAndReset());
                    if (!MatchesHash(item, actual))
                    {
                        last = new InvalidDataException($"SHA-1 mismatch from {source.Host}: expected {item.Sha1}, got {actual}");
                        TryDelete(part);
                        onBytes(-countedThisAttempt);
                        break; // Corrupt mirror → next source.
                    }
                    if (item.Size is { } size && size > 0 && new FileInfo(part).Length != size)
                    {
                        last = new InvalidDataException($"Size mismatch from {source.Host}: expected {size}");
                        TryDelete(part);
                        onBytes(-countedThisAttempt);
                        break;
                    }

                    File.Move(part, item.Path, overwrite: true);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    TryDelete(part);
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
                {
                    last = ex;
                    onBytes(-countedThisAttempt);
                    TryDelete(part);
                    if (attempt < _options.AttemptsPerSource)
                        await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt * attempt + Random.Shared.Next(0, 250)), ct).ConfigureAwait(false);
                }
            }
        }

        throw last ?? new IOException($"Failed to download {item.Path}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
