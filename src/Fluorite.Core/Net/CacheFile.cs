namespace Fluorite.Core.Net;

/// <summary>
/// Saves a downloaded copy kept for offline use (the version manifest, the news feeds). Through a temp file, so a save
/// cut short never leaves half a file for the next offline start to fail on; and without the caller's cancellation,
/// since the copy is small and already downloaded.
/// </summary>
public static class CacheFile
{
    /// <returns>
    /// Null once saved; otherwise why it couldn't be (a read-only folder, the file held open by another launcher
    /// sharing the folder). The download is still good then, and the copy from before stays as it was.
    /// </returns>
    public static async Task<Exception?> TrySaveAsync(string path, byte[] bytes)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex;
        }
        finally
        {
            try
            {
                File.Delete(tmp); // Only there when the write or the move failed.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
