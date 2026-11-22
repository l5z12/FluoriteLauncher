namespace Fluorite.Core.Net;

/// <summary>A file that must exist at <see cref="Path"/>, obtainable from any of <see cref="Sources"/>.</summary>
public sealed record DownloadItem(IReadOnlyList<Uri> Sources, string Path, string? Sha1 = null, long? Size = null)
{
    /// <summary>Optional alternative acceptable SHA-1 values (legacy Forge <c>checksums</c>).</summary>
    public IReadOnlyList<string>? AlternativeSha1 { get; init; }

    /// <summary>Human-readable label for progress/errors.</summary>
    public string? DisplayName { get; init; }

    /// <summary>Mark the file executable (no-op on Windows, kept for runtime manifests).</summary>
    public bool Executable { get; init; }
}

/// <summary>
/// Progress of a <see cref="DownloadEngine.DownloadAllAsync"/> batch. Files already on disk are left out, so the counts
/// and sizes describe only what is really downloaded.
/// </summary>
/// <param name="BytesPerSecond">Current transfer rate (smoothed over the last second or so).</param>
/// <param name="Current">Label of the file most recently started.</param>
/// <param name="Checking">
/// True while files already on disk are being checked, before anything downloads; the counts are then files checked.
/// </param>
public readonly record struct DownloadProgress(
    int CompletedFiles, int TotalFiles, long DownloadedBytes, long? TotalBytes, double BytesPerSecond,
    string? Current = null, bool Checking = false);

/// <summary>How thoroughly to check files that already exist on disk.</summary>
public enum VerifyMode
{
    /// <summary>Existence + size (fast; used on every launch).</summary>
    Fast,

    /// <summary>Full SHA-1 check of every file (repair).</summary>
    Full,
}

public sealed class DownloadFailedException(string message, IReadOnlyList<(DownloadItem Item, Exception Error)> failures)
    : Exception(message)
{
    public IReadOnlyList<(DownloadItem Item, Exception Error)> Failures { get; } = failures;
}
