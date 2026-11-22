using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fluorite.Auth.Accounts;

namespace Fluorite.Auth.Yggdrasil;

/// <summary>Gives offline accounts their skins in game, through authlib-injector pointed at a server on this machine.</summary>
public interface ILocalSkinHost
{
    /// <summary>Serves <paramref name="account"/>'s skin and returns the API root and prefetched metadata for authlib-injector.</summary>
    Task<(string ApiRoot, string MetadataBase64)> ServeAsync(Account account, CancellationToken ct = default);
}

/// <summary>
/// A minimal Yggdrasil server on 127.0.0.1 (ephemeral port) for offline accounts with a custom skin: the profile
/// lookups the game makes return that skin, signed with a key generated for this run and advertised in the metadata.
/// Joining always succeeds, as offline play does. It runs until the launcher exits.
/// </summary>
public sealed class LocalSkinServer : ILocalSkinHost, IDisposable
{
    private sealed record Profile(string Id, string Name, string? SkinHash, bool Slim);

    private const int MaxRequestBytes = 64 * 1024;
    private readonly RSA _key = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, Profile> _profiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte[]> _textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stop = new();
    private readonly Lock _startLock = new();
    private TcpListener? _listener;
    private byte[]? _metadata;

    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>A game was given this server; the launcher must keep running for its skins to load.</summary>
    public bool IsRunning => _listener is not null;

    private string Root => $"http://127.0.0.1:{Port}";

    public async Task<(string ApiRoot, string MetadataBase64)> ServeAsync(Account account, CancellationToken ct = default)
    {
        Start();
        byte[]? skin = null;
        if (account.SkinFile is { } file && File.Exists(file))
            skin = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        Register(account.Uuid, account.PlayerName, skin, account.SkinSlim);
        return (Root, Convert.ToBase64String(_metadata!));
    }

    /// <summary>Adds or replaces a profile; <paramref name="skin"/> null serves no skin (the game picks Steve/Alex).</summary>
    public void Register(string uuid, string name, byte[]? skin, bool slim)
    {
        string? hash = null;
        if (skin is not null)
        {
            hash = Convert.ToHexStringLower(SHA256.HashData(skin));
            _textures[hash] = skin;
        }
        _profiles[uuid] = new Profile(uuid, name, hash, slim);
    }

    public void Start()
    {
        lock (_startLock)
        {
            if (_listener is not null)
                return;
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            _listener = listener;
            _metadata = BuildMetadata();
            _ = AcceptLoopAsync(listener, _stop.Token);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
        _key.Dispose();
        _stop.Dispose();
    }

    private byte[] BuildMetadata()
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartObject("meta");
            w.WriteString("serverName", "Fluorite");
            w.WriteString("implementationName", "Fluorite offline skins");
            w.WriteString("implementationVersion", "1");
            w.WriteBoolean("feature.non_email_login", true);
            w.WriteEndObject();
            w.WriteStartArray("skinDomains");
            w.WriteStringValue("127.0.0.1");
            w.WriteStringValue("localhost");
            w.WriteEndArray();
            w.WriteString("signaturePublickey", _key.ExportSubjectPublicKeyInfoPem() + "\n");
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = HandleAsync(client, ct);
        }
    }

    private async Task HandleAsync(TcpClient accepted, CancellationToken ct)
    {
        using var client = accepted;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var stream = client.GetStream();
            var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
            var (status, type, body) = request is null ? (400, "text/plain", "Bad request"u8.ToArray()) : Route(request.Value);
            var head = $"HTTP/1.1 {status} {Reason(status)}\r\nContent-Type: {type}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(head), timeout.Token).ConfigureAwait(false);
            if (body.Length > 0)
                await stream.WriteAsync(body, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        204 => "No Content",
        400 => "Bad Request",
        _ => "Not Found",
    };

    private readonly record struct Request(string Method, string Path, string Query, byte[] Body);

    private static async Task<Request?> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxRequestBytes];
        var filled = 0;
        var headerEnd = -1;
        while (headerEnd < 0)
        {
            if (filled == buffer.Length)
                return null;
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            filled += n;
            headerEnd = buffer.AsSpan(0, filled).IndexOf("\r\n\r\n"u8);
        }

        var lines = Encoding.ASCII.GetString(buffer, 0, headerEnd).Split("\r\n");
        var parts = lines[0].Split(' ');
        if (parts.Length < 2)
            return null;
        var contentLength = 0;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                _ = int.TryParse(line[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture, out contentLength);
        }
        var bodyStart = headerEnd + 4;
        if (contentLength < 0 || bodyStart + contentLength > buffer.Length)
            return null;
        while (filled < bodyStart + contentLength)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(filled), ct).ConfigureAwait(false);
            if (n == 0)
                return null;
            filled += n;
        }

        var target = parts[1];
        var q = target.IndexOf('?', StringComparison.Ordinal);
        return new Request(parts[0].ToUpperInvariant(), q < 0 ? target : target[..q], q < 0 ? "" : target[(q + 1)..], buffer[bodyStart..(bodyStart + contentLength)]);
    }

    private (int Status, string Type, byte[] Body) Route(Request r)
    {
        const string ProfilePrefix = "/sessionserver/session/minecraft/profile/";
        const string TexturePrefix = "/textures/";
        var path = r.Path.TrimEnd('/');

        if (path.Length == 0 && r.Method == "GET")
            return Json(_metadata!);
        if (path == "/status")
            return Json("{}"u8.ToArray());
        if (path == "/sessionserver/session/minecraft/join" && r.Method == "POST")
            return (204, "text/plain", []);
        if (path == "/sessionserver/session/minecraft/hasJoined")
        {
            var name = QueryValue(r.Query, "username");
            var profile = _profiles.Values.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
            return profile is null ? (204, "text/plain", []) : Json(ProfileJson(profile));
        }
        if (path.StartsWith(ProfilePrefix, StringComparison.Ordinal))
        {
            var id = path[ProfilePrefix.Length..].Replace("-", "", StringComparison.Ordinal);
            return _profiles.TryGetValue(id, out var profile) ? Json(ProfileJson(profile)) : (204, "text/plain", []);
        }
        if (path == "/api/profiles/minecraft" && r.Method == "POST")
            return Json(LookupNames(r.Body));
        if (path.StartsWith(TexturePrefix, StringComparison.Ordinal) && _textures.TryGetValue(path[TexturePrefix.Length..], out var png))
            return (200, "image/png", png);
        if (path == "/minecraftservices/player/attributes")
        {
            return Json("""{"privileges":{"onlineChat":{"enabled":true},"multiplayerServer":{"enabled":true},"multiplayerRealms":{"enabled":false},"telemetry":{"enabled":false}},"profanityFilterPreferences":{"profanityFilterOn":false}}"""u8.ToArray());
        }
        return (404, "application/json", """{"error":"Not Found","errorMessage":"Not Found"}"""u8.ToArray());
    }

    private static (int, string, byte[]) Json(byte[] body) => (200, "application/json; charset=utf-8", body);

    private static string? QueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && pair[..eq] == key)
                return Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
        }
        return null;
    }

    private byte[] LookupNames(byte[] body)
    {
        List<string>? names = null;
        try
        {
            names = JsonSerializer.Deserialize(body, YggdrasilJsonContext.Default.ListString);
        }
        catch (JsonException)
        {
        }
        var found = (names ?? [])
            .Select(n => _profiles.Values.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase)))
            .OfType<Profile>()
            .Select(p => new YggdrasilProfile { Id = p.Id, Name = p.Name })
            .ToList();
        return JsonSerializer.SerializeToUtf8Bytes(found, YggdrasilJsonContext.Default.ListYggdrasilProfile);
    }

    private byte[] ProfileJson(Profile p)
    {
        var textures = new TexturesPayload
        {
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ProfileId = p.Id,
            ProfileName = p.Name,
        };
        if (p.SkinHash is not null)
        {
            textures.Textures["SKIN"] = new TextureInfo
            {
                Url = $"{Root}/textures/{p.SkinHash}",
                Metadata = p.Slim ? new Dictionary<string, string> { ["model"] = "slim" } : null,
            };
        }
        var value = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(textures, YggdrasilJsonContext.Default.TexturesPayload));
        var property = new YggdrasilProperty { Name = "textures", Value = value, Signature = Sign(value) };
        var profile = new YggdrasilProfile { Id = p.Id, Name = p.Name, Properties = [property] };
        return JsonSerializer.SerializeToUtf8Bytes(profile, YggdrasilJsonContext.Default.YggdrasilProfile);
    }

    /// <summary>SHA1withRSA over the property value, as the game's authlib verifies it.</summary>
    private string Sign(string value)
    {
#pragma warning disable CA5350 // SHA-1 is what authlib verifies profile properties with; not our choice.
        var signature = _key.SignData(Encoding.ASCII.GetBytes(value), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1);
#pragma warning restore CA5350
        return Convert.ToBase64String(signature);
    }
}
