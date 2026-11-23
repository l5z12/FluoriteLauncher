using System.Globalization;
using System.Text.RegularExpressions;

namespace Fluorite.Core.Diagnostics;

/// <summary>How long Fluorite keeps its own logs (<c>logs/launcher-yyyyMMdd.log</c>): by age, in days.</summary>
public static partial class LogRetention
{
    public const int MinDays = 1;
    public const int MaxDays = 365;

    /// <summary>The rolling file sink's path template: it adds the date before the extension.</summary>
    public const string FileTemplate = "launcher-.log";

    public static TimeSpan Limit(int days) => TimeSpan.FromDays(Math.Clamp(days, MinDays, MaxDays));

    /// <summary>
    /// Deletes launcher logs older than <paramref name="days"/>, by the file sink's rule: a file's date is the day it
    /// covers. The sink only applies it when it writes, so this runs at startup too, for sessions that log nothing.
    /// </summary>
    public static void Prune(string logsDir, int days, DateTime now)
    {
        if (!Directory.Exists(logsDir))
            return;
        var cutoff = now - Limit(days);
        foreach (var file in Directory.EnumerateFiles(logsDir, "launcher-*.log"))
        {
            var match = LauncherLog().Match(Path.GetFileName(file));
            if (!match.Success
                || !DateTime.TryParseExact(match.Groups["date"].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                || date >= cutoff)
                continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Open in another Fluorite window: it goes next time.
            }
        }
    }

    /// <summary><c>launcher-20261122.log</c>, or <c>launcher-20261122_001.log</c> when the sink had to roll to a new file.</summary>
    [GeneratedRegex(@"^launcher-(?<date>\d{8})(?:_\d+)?\.log$", RegexOptions.IgnoreCase)]
    private static partial Regex LauncherLog();
}
