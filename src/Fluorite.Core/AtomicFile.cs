using System.Text;

namespace Fluorite.Core;

/// <summary>
/// Writes a file whole or not at all: into a temp file beside it, moved over it only once complete. A write cut short
/// (an error, a cancel, a crash) leaves the file as it was, never half of one for the next read to fail on. A file
/// that's a link is written where the link points, as an ordinary write would.
/// </summary>
public static class AtomicFile
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>
    /// Runs <paramref name="write"/> on a temp path in <paramref name="path"/>'s folder (so the move is a rename), then
    /// moves the result into place. The folder must exist, as for <see cref="File.WriteAllBytes(string, byte[])"/>.
    /// </summary>
    public static void Write(string path, Action<string> write)
    {
        path = Target(path);
        var tmp = TempPath(path);
        try
        {
            write(tmp);
            Move(tmp, path);
        }
        finally
        {
            TryDelete(tmp); // Only there when the write or the move failed.
        }
    }

    /// <inheritdoc cref="Write(string, Action{string})"/>
    public static async Task WriteAsync(string path, Func<string, Task> write)
    {
        path = Target(path);
        var tmp = TempPath(path);
        try
        {
            await write(tmp).ConfigureAwait(false);
            await MoveAsync(tmp, path).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(tmp);
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/>, flushed to the disk before the move: the move is kept through a power cut, the
    /// data it points at only once it's on the disk, so without the flush a file saved just before one (settings,
    /// accounts, a manifest) could come back the right size and all zeros. Meant for small files, where the flush
    /// costs nothing anyone notices.
    /// </summary>
    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, tmp => WriteToDisk(tmp, bytes));

    /// <inheritdoc cref="WriteAllBytes"/>
    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default) =>
        WriteAsync(path, tmp => WriteToDiskAsync(tmp, bytes, ct));

    /// <summary>
    /// As <see cref="WriteAllBytes"/>; in UTF-8 without a byte order mark unless <paramref name="encoding"/> says
    /// otherwise, as <see cref="File.WriteAllText(string, string?)"/>.
    /// </summary>
    public static void WriteAllText(string path, string text, Encoding? encoding = null) => WriteAllBytes(path, Encode(text, encoding));

    /// <inheritdoc cref="WriteAllText"/>
    public static Task WriteAllTextAsync(string path, string text, Encoding? encoding = null, CancellationToken ct = default) =>
        WriteAllBytesAsync(path, Encode(text, encoding), ct);

    private static byte[] Encode(string text, Encoding? encoding)
    {
        encoding ??= s_utf8;
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private static void WriteToDisk(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    private static async Task WriteToDiskAsync(string path, byte[] bytes, CancellationToken ct)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await file.WriteAsync(bytes, ct).ConfigureAwait(false);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/>, replacing it if it's there.</summary>
    public static void Copy(string source, string target) => Write(target, tmp => File.Copy(source, tmp));

    /// <summary>
    /// Moves <paramref name="source"/> over <paramref name="target"/>, trying again for a moment while something has the
    /// target open (an antivirus or indexer looking at a file just written, another read of it): Windows won't replace
    /// an open file, and may say so with an <see cref="UnauthorizedAccessException"/> as well as an
    /// <see cref="IOException"/>. Throws as <see cref="File.Move(string, string, bool)"/> does when it still can't.
    /// </summary>
    public static void Move(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (MayPass(ex) && attempt < MoveAttempts)
            {
                Thread.Sleep(RetryDelay(attempt));
            }
        }
    }

    /// <inheritdoc cref="Move"/>
    public static async Task MoveAsync(string source, string target)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite: true);
                return;
            }
            catch (Exception ex) when (MayPass(ex) && attempt < MoveAttempts)
            {
                await Task.Delay(RetryDelay(attempt)).ConfigureAwait(false);
            }
        }
    }

    private const int MoveAttempts = 5;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(50 * attempt); // Half a second in all.

    /// <summary>A file in the way, not a path that's wrong.</summary>
    private static bool MayPass(Exception ex) =>
        ex is UnauthorizedAccessException or (IOException and not FileNotFoundException and not DirectoryNotFoundException and not PathTooLongException);

    /// <summary>
    /// A temp path beside <paramref name="path"/> that no other write shares: <c>&lt;name&gt;.&lt;8 hex&gt;.tmp</c>, or
    /// <c>fluorite.&lt;8 hex&gt;.tmp</c> when the name is too long to take more (a name has 255 characters at most).
    /// </summary>
    public static string TempPath(string path)
    {
        var suffix = "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        return Path.GetFileName(path).Length + suffix.Length <= MaxNameLength
            ? path + suffix
            : Path.Combine(Path.GetDirectoryName(path) ?? "", "fluorite" + suffix);
    }

    private const int MaxNameLength = 255;

    /// <summary>
    /// Whether <paramref name="fileName"/> is named as <see cref="TempPath"/> names temp files: one still there was left
    /// by a write that never finished (the launcher killed while it wrote), and isn't a file of anyone's.
    /// </summary>
    public static bool IsTempFile(string fileName)
    {
        if (!fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            return false;
        var stem = fileName[..^".tmp".Length];
        var dot = stem.LastIndexOf('.');
        return dot > 0 && stem.Length - dot - 1 == 8 && stem[(dot + 1)..].All(char.IsAsciiHexDigitLower);
    }

    private static string Target(string path)
    {
        path = Path.GetFullPath(path);
        try
        {
            var file = new FileInfo(path);
            if (file.LinkTarget is not null && file.ResolveLinkTarget(returnFinalTarget: true) is { } real)
                return real.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            File.SetAttributes(path, FileAttributes.Normal); // A copy of a read-only file is read-only too.
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
