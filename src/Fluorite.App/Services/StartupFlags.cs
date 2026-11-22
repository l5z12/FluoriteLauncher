namespace Fluorite.App.Services;

/// <summary>
/// Switches for trying things out: <c>FluoriteLauncher.exe --fresh --lang zh-Hans</c>. Each one also works as an
/// environment variable (<see cref="All"/> lists both). <c>--flags</c> shows the list in the app.
/// </summary>
public sealed record StartupFlags
{
    /// <summary>A switch, its environment variable, whether it takes a value, and its description's resource key.</summary>
    public sealed record Flag(string Switch, string? Variable, string? Value, string DescriptionKey);

    public static IReadOnlyList<Flag> All { get; } =
    [
        new("--welcome", "FLUORITE_SHOW_WELCOME", null, "Flags_Welcome"),
        new("--fresh", "FLUORITE_FRESH", null, "Flags_Fresh"),
        new("--root", "FLUORITE_ROOT", "<folder>", "Flags_Root"),
        new("--lang", "FLUORITE_LANG", "<language>", "Flags_Lang"),
        new("--theme", "FLUORITE_THEME", "light|dark|system", "Flags_Theme"),
        new("--pretend-arm64", "FLUORITE_PRETEND_ARM64", null, "Flags_PretendArm64"),
        new("--verbose", "FLUORITE_VERBOSE", null, "Flags_Verbose"),
        new("--trace-exceptions", "FLUORITE_TRACE_EXCEPTIONS", null, "Flags_TraceExceptions"),
        new("--page", null, "<page>", "Flags_Page"),
        new("--launch", null, "<instance id>", "Flags_Launch"),
        new("--install", null, "<link or file>", "Flags_Install"),
        new("--flags", null, null, "Flags_Flags"),
        new("", "FLUORITE_CURSEFORGE_API_KEY", "<key>", "Flags_CurseForgeKey"),
        new("", "FLUORITE_DISCORD_APP_ID", "<application id>", "Flags_DiscordAppId"),
#if DEBUG
        new("", "FLUORITE_DEV_ALLOW_OFFLINE", null, "Flags_DevOffline"),
#endif
    ];

    /// <summary>Switches followed by a value (so the value isn't mistaken for a file to open).</summary>
    public static IReadOnlySet<string> ValueSwitches { get; } = new HashSet<string>(
        ["--root", "--lang", "--theme", "--page", "--launch", "--world", "--server", "--install", "--restarted"], StringComparer.OrdinalIgnoreCase);

    public bool ShowWelcome { get; init; }
    public bool Fresh { get; init; }
    public string? Root { get; init; }
    public string? Language { get; init; }

    /// <summary>"Light", "Dark" or "System", as the settings store it.</summary>
    public string? Theme { get; init; }

    public bool PretendArm64 { get; init; }
    public bool Verbose { get; init; }
    public bool TraceExceptions { get; init; }
    public bool ShowFlags { get; init; }

    /// <summary>Flags that only take effect when the app starts (a running window can't apply them).</summary>
    public bool HasStartupOnly => Fresh || Root is not null || Language is not null || Theme is not null || PretendArm64 || Verbose || TraceExceptions;

    /// <summary>This process's flags: its command line, then the environment.</summary>
    public static StartupFlags Current { get; } = Parse(Environment.GetCommandLineArgs().Skip(1).ToList(), Environment.GetEnvironmentVariable);

    /// <param name="environment">Reads environment variables; null for a redirected launch (that process's environment isn't ours).</param>
    public static StartupFlags Parse(IReadOnlyList<string> args, Func<string, string?>? environment = null)
    {
        bool Has(string name, string? variable = null) =>
            args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
            || (variable is not null && environment?.Invoke(variable) is "1" or "true" or "TRUE" or "True");

        string? Value(string name, string? variable = null)
        {
            for (var i = 0; i < args.Count - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && args[i + 1].Trim() is { Length: > 0 } value)
                    return value;
            }
            return variable is not null && environment?.Invoke(variable) is { Length: > 0 } env ? env.Trim() : null;
        }

        return new StartupFlags
        {
            ShowWelcome = Has("--welcome", "FLUORITE_SHOW_WELCOME"),
            Fresh = Has("--fresh", "FLUORITE_FRESH"),
            Root = Value("--root", "FLUORITE_ROOT"),
            Language = Value("--lang", "FLUORITE_LANG"),
            Theme = Value("--theme", "FLUORITE_THEME")?.ToLowerInvariant() switch
            {
                "light" => "Light",
                "dark" => "Dark",
                "system" => "System",
                _ => null,
            },
            PretendArm64 = Has("--pretend-arm64", "FLUORITE_PRETEND_ARM64"),
            Verbose = Has("--verbose", "FLUORITE_VERBOSE"),
            TraceExceptions = Has("--trace-exceptions", "FLUORITE_TRACE_EXCEPTIONS"),
            ShowFlags = Has("--flags") || Has("--help") || Has("-h") || Has("-?") || Has("/?"),
        };
    }
}
