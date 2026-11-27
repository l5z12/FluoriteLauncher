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

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, tmp => File.WriteAllBytes(tmp, bytes));

    public static Task WriteAllBytesAsync(string path, byte[] bytes, CancellationToken ct = default) =>
        WriteAsync(path, tmp => File.WriteAllBytesAsync(tmp, bytes, ct));

    /// <summary>UTF-8 without a byte order mark unless <paramref name="encoding"/> says otherwise, as <see cref="File.WriteAllText(string, string?)"/>.</summary>
    public static void WriteAllText(string path, string text, Encoding? encoding = null) =>
        Write(path, tmp => File.WriteAllText(tmp, text, encoding ?? s_utf8));

    /// <inheritdoc cref="WriteAllText"/>
    public static Task WriteAllTextAsync(string path, string text, Encoding? encoding = null, CancellationToken ct = default) =>
        WriteAsync(path, tmp => File.WriteAllTextAsync(tmp, text, encoding ?? s_utf8, ct));

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

    /// <summary>A temp path beside <paramref name="path"/> that no other write shares.</summary>
    public static string TempPath(string path) => $"{path}.{Guid.NewGuid():N}.tmp";

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
