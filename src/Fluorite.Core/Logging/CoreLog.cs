using Microsoft.Extensions.Logging;

namespace Fluorite.Core.Logging;

/// <summary>Source-generated log messages for the core library.</summary>
internal static partial class CoreLog
{
    [LoggerMessage(1001, LogLevel.Warning, "Download failed: {Item}")]
    public static partial void DownloadFailed(ILogger logger, Exception ex, string item);

    [LoggerMessage(1002, LogLevel.Warning, "Version manifest unreachable or unusable, using cached copy")]
    public static partial void ManifestFromCache(ILogger logger, Exception ex);

    [LoggerMessage(1003, LogLevel.Warning, "Couldn't save the offline copy {Path}; the one from before stays")]
    public static partial void CacheNotSaved(ILogger logger, Exception ex, string path);

    [LoggerMessage(2001, LogLevel.Information, "Prepared {Version} with {Java}: {Command}")]
    public static partial void Prepared(ILogger logger, string version, string java, string command);

    [LoggerMessage(2002, LogLevel.Information, "Command line is {Length} chars; using @argfile {File}")]
    public static partial void UsingArgFile(ILogger logger, int length, string file);

    [LoggerMessage(2003, LogLevel.Information, "Command line is {Length} chars on Java 8; using pathing jar {Jar}")]
    public static partial void UsingPathingJar(ILogger logger, int length, string jar);

    [LoggerMessage(2004, LogLevel.Information, "Using LWJGL {Lwjgl} for its ARM64 natives; {Version} has none")]
    public static partial void SwappedLwjgl(ILogger logger, string lwjgl, string version);
}
