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
            File.Move(tmp, path, overwrite: true);
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
            File.Move(tmp, path, overwrite: true);
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
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
