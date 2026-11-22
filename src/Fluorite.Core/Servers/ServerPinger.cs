using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Fluorite.Core.Servers;

/// <summary>A server's answer to a status ping.</summary>
public sealed record ServerStatus
{
    public string? VersionName { get; init; }
    public int Protocol { get; init; }
    public int OnlinePlayers { get; init; }
    public int MaxPlayers { get; init; }

    /// <summary>Names from the player sample (plain text).</summary>
    public IReadOnlyList<string> PlayerSample { get; init; } = [];

    public IReadOnlyList<ChatSpan> Motd { get; init; } = [];

    /// <summary>The server icon as base64 PNG (without the <c>data:</c> prefix).</summary>
    public string? FaviconBase64 { get; init; }

    public TimeSpan Latency { get; init; }

    /// <summary>Answered the pre-1.7 ping (Minecraft 1.6 and older).</summary>
    public bool IsLegacy { get; init; }
}

/// <summary>Looks up <c>_minecraft._tcp</c> SRV records (platform specific).</summary>
public interface ISrvResolver
{
    /// <returns>The target of the preferred record, or null when there is none.</returns>
    Task<(string Host, int Port)?> ResolveMinecraftAsync(string host, CancellationToken ct = default);
}

/// <summary>
/// Server List Ping: the status request the multiplayer screen sends (1.7+), falling back to the legacy ping that
/// 1.6 and older servers answer.
/// </summary>
public sealed class ServerPinger(ISrvResolver? srv = null)
{
    public const int DefaultPort = 25565;
    private const int MaxPacket = 4 * 1024 * 1024;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(6);

    public async Task<ServerStatus> PingAsync(string address, CancellationToken ct = default)
    {
        var (host, port, explicitPort) = ParseAddress(address);
        if (host.Length == 0)
            throw new ArgumentException("The server address is empty.", nameof(address));
        if (!explicitPort && srv is not null && !IPAddress.TryParse(host, out _))
        {
            try
            {
                if (await srv.ResolveMinecraftAsync(host, ct).ConfigureAwait(false) is { } target)
                    (host, port) = target;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No SRV record (or no DNS): connect to the name itself, like the game.
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            try
            {
                return await PingModernAsync(host, port, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is EndOfStreamException or InvalidDataException || ex is IOException { InnerException: not SocketException { SocketErrorCode: SocketError.ConnectionRefused or SocketError.HostNotFound or SocketError.TimedOut } })
            {
                return await PingLegacyAsync(host, port, cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"{address} didn't answer within {Timeout.TotalSeconds:0} seconds.");
        }
    }

    /// <summary><c>host</c>, <c>host:port</c>, <c>[v6]</c>, <c>[v6]:port</c>.</summary>
    public static (string Host, int Port, bool ExplicitPort) ParseAddress(string address)
    {
        var a = address.Trim();
        if (a.StartsWith('[') && a.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            var host = a[1..close];
            var rest = a[(close + 1)..];
            return rest.StartsWith(':') && TryPort(rest[1..], out var p) ? (host, p, true) : (host, DefaultPort, false);
        }
        var colon = a.LastIndexOf(':');
        if (colon > 0 && a.IndexOf(':', StringComparison.Ordinal) == colon)
            return TryPort(a[(colon + 1)..], out var p) ? (a[..colon], p, true) : (a[..colon], DefaultPort, false);
        return (a, DefaultPort, false);

        static bool TryPort(string s, out int port) =>
            int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is > 0 and <= 65535;
    }

    private static async Task<NetworkStream> ConnectAsync(string host, int port, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(host, port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // ── 1.7+ ──────────────────────────────────────────────────────────────

    private static async Task<ServerStatus> PingModernAsync(string host, int port, CancellationToken ct)
    {
        await using var stream = await ConnectAsync(host, port, ct).ConfigureAwait(false);
        var handshake = new List<byte>();
        WriteVarInt(handshake, 0x00);
        WriteVarInt(handshake, -1); // "Pinging to find out the version", by convention.
        WriteString(handshake, host);
        handshake.Add((byte)(port >> 8));
        handshake.Add((byte)port);
        WriteVarInt(handshake, 1); // Next state: status.
        await SendAsync(stream, handshake, ct).ConfigureAwait(false);

        var started = Stopwatch.StartNew();
        await SendAsync(stream, [0x00], ct).ConfigureAwait(false);
        var (id, data) = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
        var statusTime = started.Elapsed;
        if (id != 0x00)
            throw new InvalidDataException($"Unexpected packet 0x{id:X2} instead of the status response.");
        var json = ReadString(data);

        var latency = statusTime;
        try
        {
            var ping = new List<byte>();
            WriteVarInt(ping, 0x01);
            var payload = new byte[8];
            BinaryPrimitives.WriteInt64BigEndian(payload, Environment.TickCount64);
            ping.AddRange(payload);
            started.Restart();
            await SendAsync(stream, ping, ct).ConfigureAwait(false);
            var (pongId, _) = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
            if (pongId == 0x01)
                latency = started.Elapsed;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Some servers close after the status; the status round trip is the latency then.
        }
        return ParseStatus(json, latency);
    }

    internal static ServerStatus ParseStatus(string json, TimeSpan latency)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("The status response is not a JSON object.");

        string? versionName = null;
        var protocol = 0;
        if (root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Object)
        {
            if (version.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                versionName = ChatText.StripCodes(n.GetString() ?? "");
            if (version.TryGetProperty("protocol", out var p) && p.TryGetInt32(out var pv))
                protocol = pv;
        }

        int online = 0, max = 0;
        var sample = new List<string>();
        if (root.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Object)
        {
            if (players.TryGetProperty("online", out var o) && o.ValueKind == JsonValueKind.Number)
                o.TryGetInt32(out online);
            if (players.TryGetProperty("max", out var m) && m.ValueKind == JsonValueKind.Number)
                m.TryGetInt32(out max);
            if (players.TryGetProperty("sample", out var s) && s.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in s.EnumerateArray())
                {
                    if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                        sample.Add(ChatText.StripCodes(name.GetString() ?? ""));
                }
            }
        }

        var motd = root.TryGetProperty("description", out var description) ? ChatText.FromJson(description) : [];

        string? favicon = null;
        if (root.TryGetProperty("favicon", out var fav) && fav.ValueKind == JsonValueKind.String && fav.GetString() is { } f)
        {
            var comma = f.IndexOf(',', StringComparison.Ordinal);
            favicon = new string((comma >= 0 ? f[(comma + 1)..] : f).Where(c => !char.IsWhiteSpace(c)).ToArray());
        }

        return new ServerStatus
        {
            VersionName = versionName,
            Protocol = protocol,
            OnlinePlayers = online,
            MaxPlayers = max,
            PlayerSample = sample,
            Motd = motd,
            FaviconBase64 = favicon,
            Latency = latency,
        };
    }

    // ── 1.6 and older ─────────────────────────────────────────────────────

    private static async Task<ServerStatus> PingLegacyAsync(string host, int port, CancellationToken ct)
    {
        await using var stream = await ConnectAsync(host, port, ct).ConfigureAwait(false);
        // FE 01, then the 1.6 "MC|PingHost" plugin message; older servers stop reading after FE 01.
        var request = new List<byte> { 0xFE, 0x01, 0xFA };
        WriteUtf16(request, "MC|PingHost");
        var hostBytes = Encoding.BigEndianUnicode.GetBytes(host);
        var rest = 1 + 2 + hostBytes.Length + 4;
        request.Add((byte)(rest >> 8));
        request.Add((byte)rest);
        request.Add(74); // Protocol of 1.6.4.
        WriteUtf16(request, host);
        request.AddRange([(byte)(port >> 24), (byte)(port >> 16), (byte)(port >> 8), (byte)port]);

        var started = Stopwatch.StartNew();
        await stream.WriteAsync(request.ToArray(), ct).ConfigureAwait(false);
        var head = new byte[3];
        await stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var latency = started.Elapsed;
        if (head[0] != 0xFF)
            throw new InvalidDataException("The server didn't answer the legacy ping.");
        var chars = head[1] << 8 | head[2];
        var body = new byte[chars * 2];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return ParseLegacy(Encoding.BigEndianUnicode.GetString(body), latency);
    }

    internal static ServerStatus ParseLegacy(string text, TimeSpan latency)
    {
        if (text.StartsWith("§1\0", StringComparison.Ordinal))
        {
            // 1.4–1.6: §1 \0 protocol \0 version \0 motd \0 online \0 max
            var parts = text.Split('\0');
            if (parts.Length < 6)
                throw new InvalidDataException("Malformed legacy ping response.");
            return new ServerStatus
            {
                Protocol = int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0,
                VersionName = parts[2],
                Motd = ChatText.FromLegacy(parts[3]),
                OnlinePlayers = int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var on) ? on : 0,
                MaxPlayers = int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mx) ? mx : 0,
                Latency = latency,
                IsLegacy = true,
            };
        }
        // Beta 1.8–1.3: motd § online § max
        var fields = text.Split('§');
        if (fields.Length < 3)
            throw new InvalidDataException("Malformed legacy ping response.");
        return new ServerStatus
        {
            Motd = [new ChatSpan(string.Join('§', fields[..^2]))],
            OnlinePlayers = int.TryParse(fields[^2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var online) ? online : 0,
            MaxPlayers = int.TryParse(fields[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ? max : 0,
            Latency = latency,
            IsLegacy = true,
        };
    }

    // ── Wire format ───────────────────────────────────────────────────────

    private static async Task SendAsync(Stream stream, List<byte> payload, CancellationToken ct)
    {
        var packet = new List<byte>(payload.Count + 5);
        WriteVarInt(packet, payload.Count);
        packet.AddRange(payload);
        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
    }

    private static async Task<(int Id, byte[] Data)> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, legacyCheck: true, ct).ConfigureAwait(false);
        if (length is <= 0 or > MaxPacket)
            throw new InvalidDataException($"Bad packet length {length}.");
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
        var offset = 0;
        var id = ReadVarInt(buffer, ref offset);
        return (id, buffer[offset..]);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, bool legacyCheck, CancellationToken ct)
    {
        var value = 0;
        var one = new byte[1];
        for (var i = 0; i < 5; i++)
        {
            await stream.ReadExactlyAsync(one, ct).ConfigureAwait(false);
            if (i == 0 && legacyCheck && one[0] == 0xFF)
                throw new InvalidDataException("The server answered with a pre-1.7 kick packet.");
            value |= (one[0] & 0x7F) << (7 * i);
            if ((one[0] & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("VarInt is too long.");
    }

    private static int ReadVarInt(byte[] data, ref int offset)
    {
        var value = 0;
        for (var i = 0; i < 5; i++)
        {
            if (offset >= data.Length)
                throw new InvalidDataException("Truncated VarInt.");
            var b = data[offset++];
            value |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
                return value;
        }
        throw new InvalidDataException("VarInt is too long.");
    }

    private static string ReadString(byte[] data)
    {
        var offset = 0;
        var length = ReadVarInt(data, ref offset);
        if (length < 0 || offset + length > data.Length)
            throw new InvalidDataException("Truncated string.");
        return Encoding.UTF8.GetString(data, offset, length);
    }

    private static void WriteVarInt(List<byte> buffer, int value)
    {
        var v = (uint)value;
        do
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0)
                b |= 0x80;
            buffer.Add(b);
        }
        while (v != 0);
    }

    private static void WriteString(List<byte> buffer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(buffer, bytes.Length);
        buffer.AddRange(bytes);
    }

    private static void WriteUtf16(List<byte> buffer, string value)
    {
        buffer.Add((byte)(value.Length >> 8));
        buffer.Add((byte)value.Length);
        buffer.AddRange(Encoding.BigEndianUnicode.GetBytes(value));
    }
}
