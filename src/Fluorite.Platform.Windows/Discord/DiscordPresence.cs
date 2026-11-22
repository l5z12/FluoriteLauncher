using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;

namespace Fluorite.Platform.Windows.Discord;

/// <summary>What Discord shows under the user's name.</summary>
/// <param name="LargeImage">An art asset key of the Discord application (or an https image URL).</param>
public sealed record DiscordActivity(string Details, string? State, DateTimeOffset Start, string? LargeImage = null, string? LargeText = null);

/// <summary>
/// Discord Rich Presence through the desktop client's local IPC pipe (<c>\\.\pipe\discord-ipc-N</c>). Everything is
/// best effort: when Discord isn't running nothing happens, and when it restarts the next update reconnects. Closing
/// the connection (or the launcher exiting) clears the activity.
/// </summary>
public sealed class DiscordPresence(string applicationId) : IDisposable
{
    private const int OpHandshake = 0, OpFrame = 1, OpClose = 2;
    private static readonly TimeSpan s_replyTimeout = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NamedPipeClientStream? _pipe;

    /// <summary>Shows <paramref name="activity"/>, or clears it when null.</summary>
    public async Task SetAsync(DiscordActivity? activity, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (activity is null && _pipe is null)
                return; // Nothing shown, nothing to clear.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (_pipe is null && !await ConnectAsync(ct).ConfigureAwait(false))
                    return; // Discord isn't running.
                try
                {
                    await WriteAsync(OpFrame, SetActivityPayload(activity), ct).ConfigureAwait(false);
                    await ReadAsync(ct).ConfigureAwait(false);
                    if (activity is null)
                        Close(); // No reason to keep a pipe open while nothing is shown.
                    return;
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    Close(); // Discord restarted: reconnect once.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }

    private void Close()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    private async Task<bool> ConnectAsync(CancellationToken ct)
    {
        for (var i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(200, ct).ConfigureAwait(false);
                _pipe = pipe;
                await WriteAsync(OpHandshake, HandshakePayload(), ct).ConfigureAwait(false);
                if (await ReadAsync(ct).ConfigureAwait(false) == OpFrame)
                    return true; // READY
                Close(); // CLOSE: e.g. an unknown application id.
                return false;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (_pipe == pipe)
                    _pipe = null;
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
        }
        return false;
    }

    private async Task WriteAsync(int op, byte[] json, CancellationToken ct)
    {
        var frame = new byte[8 + json.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, op);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), json.Length);
        json.CopyTo(frame, 8);
        await _pipe!.WriteAsync(frame, ct).ConfigureAwait(false);
        await _pipe.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Reads one reply frame (and drops its body); returns its opcode.</summary>
    private async Task<int> ReadAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(s_replyTimeout);
        var header = new byte[8];
        await _pipe!.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
        var op = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (length is < 0 or > 1 << 20)
            throw new IOException("Malformed Discord IPC frame.");
        if (length > 0)
            await _pipe.ReadExactlyAsync(new byte[length], timeout.Token).ConfigureAwait(false);
        if (op == OpClose)
            throw new IOException("Discord closed the connection.");
        return op;
    }

    private byte[] HandshakePayload() => Json(w =>
    {
        w.WriteNumber("v", 1);
        w.WriteString("client_id", applicationId);
    });

    private static byte[] SetActivityPayload(DiscordActivity? a) => Json(w =>
    {
        w.WriteString("cmd", "SET_ACTIVITY");
        w.WriteString("nonce", Guid.NewGuid().ToString());
        w.WriteStartObject("args");
        w.WriteNumber("pid", Environment.ProcessId);
        if (a is null)
        {
            w.WriteNull("activity");
        }
        else
        {
            w.WriteStartObject("activity");
            w.WriteString("details", Clip(a.Details));
            if (a.State is { Length: > 0 } state)
                w.WriteString("state", Clip(state));
            w.WriteStartObject("timestamps");
            w.WriteNumber("start", a.Start.ToUnixTimeSeconds());
            w.WriteEndObject();
            if (a.LargeImage is { Length: > 0 } image)
            {
                w.WriteStartObject("assets");
                w.WriteString("large_image", image);
                if (a.LargeText is { Length: > 0 } text)
                    w.WriteString("large_text", Clip(text));
                w.WriteEndObject();
            }
            w.WriteBoolean("instance", false);
            w.WriteEndObject();
        }
        w.WriteEndObject();
    });

    /// <summary>Discord rejects activity strings over 128 characters (and under 2).</summary>
    private static string Clip(string text) => text.Length switch
    {
        > 128 => text[..127] + "…",
        < 2 => text.PadRight(2),
        _ => text,
    };

    private static byte[] Json(Action<Utf8JsonWriter> body)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            body(w);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}
