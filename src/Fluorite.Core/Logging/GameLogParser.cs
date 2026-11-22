using System.Globalization;
using System.Text;
using System.Xml;

namespace Fluorite.Core.Logging;

public enum GameLogLevel
{
    Unknown,
    Trace,
    Debug,
    Info,
    Warn,
    Error,
    Fatal,
}

public sealed record GameLogEntry(
    DateTimeOffset Timestamp,
    GameLogLevel Level,
    string? Thread,
    string? Logger,
    string Message,
    string? Throwable,
    bool FromStdErr);

/// <summary>
/// Turns raw game output into structured entries. Versions launched with Mojang's <c>logging.client</c> config emit
/// log4j2 XMLLayout events (possibly spanning many lines); anything else (pre-log4j println, mods writing to stdout,
/// JVM crashes) is classified heuristically.
/// </summary>
public sealed partial class GameLogParser
{
    private const string Log4jNs = "http://jakarta.apache.org/log4j/";
    private readonly StringBuilder _buffer = new();
    private bool _inEvent;
    private string? _julLogger;

    /// <summary><c>java.util.logging.SimpleFormatter</c> header: "Nov 09, 2026 12:31:03 AM net.minecraft.launchwrapper.LogWrapper log".</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Z][a-z]{2} \d{1,2}, \d{4} \d{1,2}:\d{2}:\d{2} (AM|PM) (?<logger>[\w.$]+) [\w$<>]+$")]
    private static partial System.Text.RegularExpressions.Regex JulHeaderRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<level>SEVERE|WARNING|INFO|CONFIG|FINE|FINER|FINEST): (?<msg>.*)$")]
    private static partial System.Text.RegularExpressions.Regex JulMessageRegex();

    /// <summary>Feed one raw line; returns zero or more completed entries.</summary>
    public IEnumerable<GameLogEntry> Feed(string line, bool isError, DateTimeOffset received)
    {
        // java.util.logging (LaunchWrapper, old Forge, JInput) writes a header line, then "LEVEL: message".
        if (!_inEvent && JulHeaderRegex().Match(line) is { Success: true } header)
        {
            _julLogger = header.Groups["logger"].Value;
            return [];
        }
        if (_julLogger is not null)
        {
            var logger = _julLogger;
            _julLogger = null;
            if (JulMessageRegex().Match(line) is { Success: true } msg)
                return [new GameLogEntry(received, ParseLevel(msg.Groups["level"].Value), null, logger, msg.Groups["msg"].Value, null, isError)];
        }

        if (!_inEvent && line.TrimStart().StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            _inEvent = true;
            _buffer.Clear();
        }

        if (_inEvent)
        {
            _buffer.AppendLine(line);
            if (line.Contains("</log4j:Event>", StringComparison.Ordinal))
            {
                _inEvent = false;
                var xml = _buffer.ToString();
                _buffer.Clear();
                return [ParseEvent(xml, isError, received)];
            }
            // Guard against a never-closed event swallowing the whole log.
            if (_buffer.Length > 1_000_000)
            {
                _inEvent = false;
                var raw = _buffer.ToString();
                _buffer.Clear();
                return [Plain(raw, isError, received)];
            }
            return [];
        }

        return [Plain(line, isError, received)];
    }

    /// <summary>Flushes any partially buffered event (e.g. when the process exits mid-event).</summary>
    public IEnumerable<GameLogEntry> Flush(DateTimeOffset now)
    {
        if (!_inEvent || _buffer.Length == 0)
            return [];
        _inEvent = false;
        var raw = _buffer.ToString();
        _buffer.Clear();
        return [Plain(raw, false, now)];
    }

    private static GameLogEntry ParseEvent(string xml, bool isError, DateTimeOffset received)
    {
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, ConformanceLevel = ConformanceLevel.Fragment };
            var wrapped = $"<root xmlns:log4j=\"{Log4jNs}\">{xml}</root>";
            using var reader = XmlReader.Create(new StringReader(wrapped), settings);

            string? logger = null, thread = null, message = null, throwable = null;
            var level = GameLogLevel.Unknown;
            var ts = received;

            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element)
                    continue;
                switch (reader.LocalName)
                {
                    case "Event":
                        logger = reader.GetAttribute("logger");
                        thread = reader.GetAttribute("thread");
                        level = ParseLevel(reader.GetAttribute("level"));
                        if (long.TryParse(reader.GetAttribute("timestamp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
                            ts = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                        break;
                    case "Message":
                        message = reader.ReadElementContentAsString();
                        break;
                    case "Throwable":
                        throwable = reader.ReadElementContentAsString();
                        break;
                }
            }
            return new GameLogEntry(ts, level, thread, logger, message ?? "", throwable, isError);
        }
        catch (XmlException)
        {
            return Plain(xml.TrimEnd(), isError, received);
        }
    }

    private static GameLogEntry Plain(string line, bool isError, DateTimeOffset received)
    {
        var level = isError ? GameLogLevel.Error : GameLogLevel.Unknown;
        // Common "[HH:mm:ss] [Thread/LEVEL]: msg" pattern (log4j PatternLayout, Forge, Fabric).
        var slash = line.IndexOf('/', StringComparison.Ordinal);
        var close = slash > 0 ? line.IndexOf(']', slash) : -1;
        if (slash > 0 && close > slash)
        {
            var parsed = ParseLevel(line[(slash + 1)..close]);
            if (parsed != GameLogLevel.Unknown)
                level = parsed;
        }
        else if (line.StartsWith("Exception in thread", StringComparison.Ordinal) || line.StartsWith("\tat ", StringComparison.Ordinal) || line.StartsWith("Caused by:", StringComparison.Ordinal))
        {
            level = GameLogLevel.Error;
        }
        return new GameLogEntry(received, level, null, null, line, null, isError);
    }

    public static GameLogLevel ParseLevel(string? s) => s?.Trim().ToUpperInvariant() switch
    {
        "TRACE" or "FINEST" or "FINER" => GameLogLevel.Trace,
        "DEBUG" or "FINE" => GameLogLevel.Debug,
        "INFO" or "CONFIG" => GameLogLevel.Info,
        "WARN" or "WARNING" => GameLogLevel.Warn,
        "ERROR" or "SEVERE" => GameLogLevel.Error,
        "FATAL" => GameLogLevel.Fatal,
        _ => GameLogLevel.Unknown,
    };
}
