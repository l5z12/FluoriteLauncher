using System.Text.RegularExpressions;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Launch;

public sealed record BuiltArguments(IReadOnlyList<string> Jvm, string MainClass, IReadOnlyList<string> Game);

/// <summary>Produces JVM and game arguments from a resolved version, rule context and placeholder values.</summary>
public static partial class ArgumentBuilder
{
    [GeneratedRegex(@"\$\{([A-Za-z0-9_]+)\}")]
    private static partial Regex PlaceholderRegex();

    /// <summary>JVM arguments implied for versions that predate <c>arguments.jvm</c>.</summary>
    public static IReadOnlyList<string> LegacyJvmDefaults(string osName) =>
    [
        .. osName == "windows" ? new[] { "-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump" } : [],
        "-Djava.library.path=${natives_directory}",
        "-Dminecraft.launcher.brand=${launcher_name}",
        "-Dminecraft.launcher.version=${launcher_version}",
        "-cp",
        "${classpath}",
    ];

    /// <param name="userJvmArgs">Memory, user args, account agents; inserted after the version's JVM args.</param>
    /// <param name="loggingArgument">Resolved log4j argument or null.</param>
    public static BuiltArguments Build(
        ResolvedVersion version,
        RuleContext ctx,
        IReadOnlyDictionary<string, string> vars,
        IReadOnlyList<string> userJvmArgs,
        string? loggingArgument,
        LaunchOptions options)
    {
        // ── JVM ───────────────────────────────────────────────────────────
        var jvm = new List<string>();
        if (version.JvmArguments is { } modernJvm)
            jvm.AddRange(Expand(modernJvm, ctx, vars));
        else
            jvm.AddRange(LegacyJvmDefaults(ctx.OsName).Select(a => Substitute(a, vars)));

        jvm.AddRange(userJvmArgs.Select(a => Substitute(a, vars)));
        if (loggingArgument is not null)
            jvm.Add(loggingArgument);

        // ── Game ──────────────────────────────────────────────────────────
        var game = new List<string>();
        if (version.GameArguments is { } modernGame)
        {
            game.AddRange(Expand(modernGame, ctx, vars));
        }
        else if (version.MinecraftArguments is { } legacy)
        {
            foreach (var token in legacy.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                game.Add(Substitute(token, vars));
        }

        // Features the version's own arguments didn't express.
        if (options.Resolution is { } res && !game.Contains("--width"))
            game.AddRange(["--width", res.Width.ToString(System.Globalization.CultureInfo.InvariantCulture), "--height", res.Height.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (options.Session.IsDemo && !game.Contains("--demo"))
            game.Add("--demo");
        if (options.Fullscreen && !game.Contains("--fullscreen"))
            game.Add("--fullscreen");

        if (options.QuickPlay is { Kind: QuickPlayKind.Multiplayer } qp
            && !game.Contains("--quickPlayMultiplayer"))
        {
            // Pre-23w14a: legacy direct-connect arguments.
            var (host, port) = SplitHostPort(qp.Target);
            game.AddRange(["--server", host, "--port", port]);
        }

        game.AddRange(options.ExtraGameArguments.Select(a => Substitute(a, vars)));
        return new BuiltArguments(jvm, version.MainClass, game);
    }

    public static IEnumerable<string> Expand(IEnumerable<ArgumentEntry> entries, RuleContext ctx, IReadOnlyDictionary<string, string> vars)
    {
        foreach (var e in entries)
        {
            if (!RuleEvaluator.IsAllowed(e.Rules, ctx))
                continue;
            foreach (var v in e.Values)
                yield return Substitute(v, vars);
        }
    }

    /// <summary>Replaces known <c>${name}</c> placeholders; unknown ones are left untouched.</summary>
    public static string Substitute(string input, IReadOnlyDictionary<string, string> vars) =>
        input.Contains("${", StringComparison.Ordinal)
            ? PlaceholderRegex().Replace(input, m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value)
            : input;

    public static (string Host, string Port) SplitHostPort(string target)
    {
        var t = target.Trim();
        // IPv6 literal: [::1]:25565
        if (t.StartsWith('[') && t.IndexOf(']', StringComparison.Ordinal) is var close and > 0)
        {
            var host = t[1..close];
            var rest = t[(close + 1)..];
            return (host, rest.StartsWith(':') ? rest[1..] : "25565");
        }
        var colon = t.LastIndexOf(':');
        return colon > 0 && t.IndexOf(':', StringComparison.Ordinal) == colon
            ? (t[..colon], t[(colon + 1)..])
            : (t, "25565");
    }
}
