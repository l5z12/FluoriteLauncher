using System.Security.Cryptography;
using System.Text.Json;
using Fluorite.Core.Net;

namespace Fluorite.Auth.Yggdrasil;

/// <summary>
/// Keeps a copy of authlib-injector (the Java agent that points the game at a Yggdrasil server) in
/// <c>&lt;root&gt;/authlib-injector</c>, checking for a new build at most once a day. Offline, the last download is used.
/// </summary>
public sealed class AuthlibInjector(HttpClient http, MirrorTable mirrors, string directory) : IDisposable
{
    private const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";
    private const string StampFile = "latest.json";
    private static readonly TimeSpan s_checkEvery = TimeSpan.FromDays(1);
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public void Dispose() => _gate.Dispose();

    /// <summary>The <c>-javaagent</c> and prefetch arguments for a server.</summary>
    public static IReadOnlyList<string> JvmArguments(string jarPath, string apiRoot, string? metadataBase64)
    {
        var args = new List<string> { $"-javaagent:{jarPath}={apiRoot}" };
        if (!string.IsNullOrEmpty(metadataBase64))
            args.Add("-Dauthlibinjector.yggdrasil.prefetched=" + metadataBase64);
        return args;
    }

    /// <summary>Path of a usable jar, downloading it first when there is none or a day has passed since the last check.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(directory);
            var existing = Newest();
            var stamp = Path.Combine(directory, StampFile);
            if (existing is not null && File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < s_checkEvery)
                return existing;
            try
            {
                var (raw, artifact) = await FetchLatestAsync(ct).ConfigureAwait(false);
                var target = Path.Combine(directory, $"authlib-injector-{artifact.Version}.jar");
                var sha256 = artifact.Checksums?.Sha256;
                if (!File.Exists(target) || (sha256 is not null && !HashMatches(target, sha256)))
                    await DownloadAsync(artifact.DownloadUrl, target, sha256, ct).ConfigureAwait(false);
                await File.WriteAllBytesAsync(stamp, raw, ct).ConfigureAwait(false);
                DeleteOthers(target);
                return target;
            }
            catch (Exception ex) when (existing is not null && !ct.IsCancellationRequested
                && ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
            {
                return existing; // Offline or the download API is down: the copy we have still works.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string? Newest() =>
        Directory.EnumerateFiles(directory, "authlib-injector-*.jar")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();

    private async Task<(byte[] Raw, AuthlibInjectorArtifact Artifact)> FetchLatestAsync(CancellationToken ct)
    {
        Exception? last = null;
        foreach (var uri in mirrors.Candidates(LatestUrl))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(s_timeout);
                var raw = await http.GetByteArrayAsync(uri, cts.Token).ConfigureAwait(false);
                var artifact = JsonSerializer.Deserialize(raw, YggdrasilJsonContext.Default.AuthlibInjectorArtifact);
                if (artifact is { DownloadUrl.Length: > 0, Version.Length: > 0 } && artifact.Version.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                    return (raw, artifact);
                last = new InvalidDataException("authlib-injector latest.json is incomplete.");
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or JsonException or OperationCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("authlib-injector could not be downloaded.");
    }

    private async Task DownloadAsync(string url, string target, string? sha256, CancellationToken ct)
    {
        Exception? last = null;
        var tmp = target + ".part";
        foreach (var uri in mirrors.Candidates(url))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(s_timeout);
                var bytes = await http.GetByteArrayAsync(uri, cts.Token).ConfigureAwait(false);
                if (sha256 is not null && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                {
                    last = new InvalidDataException($"authlib-injector from {uri.Host} failed its SHA-256 check.");
                    continue;
                }
                await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
                File.Move(tmp, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && ex is HttpRequestException or OperationCanceledException)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException("authlib-injector could not be downloaded.");
    }

    private static bool HashMatches(string path, string sha256)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteOthers(string keep)
    {
        foreach (var jar in Directory.EnumerateFiles(directory, "authlib-injector-*.jar"))
        {
            if (string.Equals(jar, keep, StringComparison.OrdinalIgnoreCase))
                continue;
            try
            {
                File.Delete(jar);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A running game still has it open; it goes next time.
            }
        }
    }
}
