using System.Text.Json;
using Fluorite.Core.Json;
using Fluorite.Core.Logging;
using Fluorite.Core.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fluorite.Core.Versions;

/// <summary>
/// Loads version JSONs from a <see cref="GameDirectories"/> store, fetching vanilla ones from the
/// version manifest when missing, and resolves <c>inheritsFrom</c> chains.
/// </summary>
public sealed class VersionRepository(GameDirectories dirs, HttpClient http, MirrorTable mirrors, ILogger<VersionRepository>? logger = null) : IDisposable
{
    public void Dispose() => _manifestLock.Dispose();

    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    private readonly ILogger _logger = logger ?? NullLogger<VersionRepository>.Instance;
    private VersionManifest? _manifest;
    private readonly SemaphoreSlim _manifestLock = new(1, 1);

    public GameDirectories Directories => dirs;

    /// <summary>Fetches the manifest (cached in memory and on disk; falls back to the disk copy when offline).</summary>
    public async Task<VersionManifest> GetManifestAsync(bool refresh = false, CancellationToken ct = default)
    {
        // The copy in memory needs no lock: a refresh downloading under it (Home's F5) mustn't hold up launching and
        // the version pickers, which are happy with what's there.
        if (!refresh && Volatile.Read(ref _manifest) is { } cached)
            return cached;
        await _manifestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_manifest is not null && !refresh)
                return _manifest;

            var cachePath = Path.Combine(dirs.Versions, "version_manifest_v2.json");
            try
            {
                // Each source's answer is checked before it's taken: a mirror answering "{}" or an error page with 200
                // moves on to the next source, and only when none has a usable one is the disk copy used, as offline.
                // Nothing unusable replaces the copy in memory or on disk.
                var (bytes, manifest) = await GetWithFallbackAsync(ManifestUrl,
                    (_, b) => (b, Usable(JsonSerializer.Deserialize(b, FluoriteJsonContext.Default.VersionManifest), "Downloaded")), ct).ConfigureAwait(false);
                _manifest = manifest;
                // A copy that can't be saved (another launcher holding it open in a shared .minecraft) doesn't undo
                // the download: it's logged, and the old copy stays for offline use.
                if (await CacheFile.TrySaveAsync(cachePath, bytes).ConfigureAwait(false) is { } notSaved)
                    CoreLog.CacheNotSaved(_logger, notSaved, cachePath);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException
                && !ct.IsCancellationRequested && File.Exists(cachePath))
            {
                CoreLog.ManifestFromCache(_logger, ex);
                await using var fs = File.OpenRead(cachePath);
                _manifest = Usable(await JsonSerializer.DeserializeAsync(fs, FluoriteJsonContext.Default.VersionManifest, ct).ConfigureAwait(false), "Cached");
            }
            return _manifest;
        }
        finally
        {
            _manifestLock.Release();
        }
    }

    /// <summary>
    /// What <see cref="GetManifestAsync"/> throws when there's no manifest to be had: offline with no copy on disk,
    /// every source answering something unusable, a disk copy that can't be read. Callers show what they can without
    /// one (an empty list, the instance's own version) instead.
    /// </summary>
    public static bool IsManifestUnavailable(Exception ex) =>
        ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    /// <summary>
    /// <paramref name="manifest"/> when it can be used: entries without an id are dropped, and at least one has to be
    /// left, with the latest release named. A refresh that fails this keeps the copy it had.
    /// </summary>
    private static VersionManifest Usable(VersionManifest? manifest, string which)
    {
        manifest?.Versions?.RemoveAll(v => v is null || string.IsNullOrEmpty(v.Id));
        return manifest is { Versions.Count: > 0, Latest.Release.Length: > 0 }
            ? manifest
            : throw new InvalidDataException($"{which} version manifest lists no versions or no latest release.");
    }

    /// <summary>Lists version ids that have a JSON in the local <c>versions/</c> folder.</summary>
    public IEnumerable<string> GetInstalledVersionIds()
    {
        if (!Directory.Exists(dirs.Versions))
            yield break;
        foreach (var dir in Directory.EnumerateDirectories(dirs.Versions))
        {
            var id = Path.GetFileName(dir);
            if (File.Exists(dirs.VersionJson(id)))
                yield return id;
        }
    }

    public async Task<VersionJson?> TryLoadLocalAsync(string id, CancellationToken ct = default)
    {
        var path = dirs.VersionJson(id);
        if (!File.Exists(path))
            return null;
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var json = await JsonSerializer.DeserializeAsync(fs, FluoriteJsonContext.Default.VersionJson, ct).ConfigureAwait(false);
        if (json is not null && string.IsNullOrEmpty(json.Id))
            json.Id = id;
        return json;
    }

    /// <summary>Returns the local JSON, downloading it from the manifest if this is a known vanilla version.</summary>
    public async Task<VersionJson> GetOrFetchAsync(string id, CancellationToken ct = default)
    {
        var local = await TryLoadLocalAsync(id, ct).ConfigureAwait(false);
        if (local is not null)
            return local;

        var manifest = await GetManifestAsync(ct: ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == id)
            ?? throw new VersionNotFoundException(id);

        var bytes = await GetBytesWithFallbackAsync(entry.Url, ct, entry.Sha1).ConfigureAwait(false);
        var path = dirs.VersionJson(id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await AtomicFile.WriteAllBytesAsync(path, bytes, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize(bytes, FluoriteJsonContext.Default.VersionJson)
            ?? throw new InvalidDataException($"Version JSON for '{id}' is empty.");
    }

    /// <summary>Loads <paramref name="id"/> and its <c>inheritsFrom</c> ancestors, returning base-first.</summary>
    public async Task<IReadOnlyList<VersionJson>> LoadChainAsync(string id, CancellationToken ct = default)
    {
        var chain = new List<VersionJson>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? current = id;
        while (current is not null)
        {
            if (!seen.Add(current))
                throw new InvalidDataException($"Cyclic inheritsFrom chain at '{current}'.");
            var json = await GetOrFetchAsync(current, ct).ConfigureAwait(false);
            chain.Add(json);
            current = string.IsNullOrWhiteSpace(json.InheritsFrom) ? null : json.InheritsFrom;
        }
        chain.Reverse();
        return chain;
    }

    public async Task<ResolvedVersion> ResolveAsync(string id, CancellationToken ct = default) =>
        VersionMerger.Merge(await LoadChainAsync(id, ct).ConfigureAwait(false));

    private Task<byte[]> GetBytesWithFallbackAsync(string url, CancellationToken ct, string? sha1 = null) =>
        GetWithFallbackAsync(url, (uri, bytes) =>
            sha1 is null || HashUtil.HashEquals(sha1, Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(bytes)))
                ? bytes
                : throw new InvalidDataException($"SHA-1 mismatch for {uri}"), ct);

    /// <summary>
    /// The first source (official or mirror, in the user's order) whose answer <paramref name="read"/> accepts. It
    /// rejects one by throwing <see cref="InvalidDataException"/> or <see cref="JsonException"/>: a hash mismatch, a
    /// mirror's "{}" or error page served with 200. The next source may have it right.
    /// </summary>
    private async Task<T> GetWithFallbackAsync<T>(string url, Func<Uri, byte[], T> read, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var uri in mirrors.Candidates(url))
        {
            try
            {
                return read(uri, await http.GetByteArrayAsync(uri, ct).ConfigureAwait(false));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidDataException or JsonException)
            {
                last = ex;
            }
        }
        throw last ?? new HttpRequestException($"Could not fetch {url}");
    }
}

public sealed class VersionNotFoundException(string id) : Exception($"Minecraft version '{id}' was not found locally or in the version manifest.")
{
    public string VersionId { get; } = id;
}
