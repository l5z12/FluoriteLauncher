using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Diagnostics;

public enum CrashConfidence
{
    /// <summary>The log says so directly.</summary>
    Likely,

    /// <summary>A common cause that fits, shown when nothing more certain was found.</summary>
    Possible,
}

/// <summary>One thing that went wrong.</summary>
/// <param name="Id">Rule id. The UI shows <c>Crash_{Id}_Title</c> and <c>Crash_{Id}_Advice</c>, formatted with <paramref name="Args"/>.</param>
/// <param name="Details">The log lines that show it (the loader's own explanation, the file involved…).</param>
public sealed record CrashFinding(string Id, IReadOnlyList<string> Args, string? Details, CrashConfidence Confidence);

/// <summary>What the analyzer looks at: the session's output plus the files the game and Java wrote when it crashed.</summary>
public sealed record CrashContext
{
    /// <summary>The session's output as text (lines formatted like <c>latest.log</c>).</summary>
    public string Log { get; init; } = "";

    public int? ExitCode { get; init; }

    /// <summary>Where <c>crash-reports/</c> and <c>hs_err_pid*.log</c> are looked for.</summary>
    public string? GameDirectory { get; init; }

    /// <summary>Only crash files written after this count (the session's start).</summary>
    public DateTimeOffset? Since { get; init; }

    public int? JavaMajor { get; init; }
    public CpuArch JavaArch { get; init; }

    /// <summary>The Java version the Minecraft version asks for.</summary>
    public int? RequiredJavaMajor { get; init; }
}

/// <param name="CrashReportFile">Minecraft's crash report for this crash, if it wrote one.</param>
/// <param name="JvmCrashFile">Java's own crash log (<c>hs_err_pid*.log</c>), if Java itself crashed.</param>
public sealed record CrashAnalysis(IReadOnlyList<CrashFinding> Findings, string? CrashReportFile, string? JvmCrashFile);

/// <summary>
/// Finds the cause of a crash in the game's output, Minecraft's crash report and Java's crash log, using a table of
/// known error messages (Java version mismatches, memory, mod loader complaints, graphics drivers…). Each rule says
/// which lines prove it, so the UI can show the evidence next to the advice.
/// </summary>
public static partial class CrashAnalyzer
{
    private const int MaxLogChars = 4_000_000;
    private const int MaxReportChars = 1_000_000;
    private const int MaxJvmLogChars = 64 * 1024; // The cause is at the top; the rest is memory maps.

    private enum Show
    {
        /// <summary>The line the match is on.</summary>
        Line,

        /// <summary>The line and the explanation under it (up to a stack trace or the next log line).</summary>
        Block,

        None,
    }

    /// <param name="Args">Turns a match into the advice's arguments; null for none.</param>
    /// <param name="All">Collect arguments from every match (all the mods that failed), not just the first.</param>
    /// <param name="When">Extra condition on the context.</param>
    /// <param name="Hides">Less specific findings this one makes redundant.</param>
    private sealed record Rule(
        string Id,
        Regex Pattern,
        Func<Match, CrashContext, string?[]>? Args = null,
        Show Show = Show.Line,
        bool All = false,
        Func<CrashContext, bool>? When = null,
        Regex? Also = null,
        CrashConfidence Confidence = CrashConfidence.Likely,
        string[]? Hides = null);

    private static Regex R(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Multiline | extra, TimeSpan.FromSeconds(2));

    private static string? G(Match m, string group) => m.Groups[group] is { Success: true } g ? g.Value.Trim() : null;

    /// <summary>Class file version → Java version (52 → 8, 61 → 17, 65 → 21).</summary>
    private static string? JavaFromClassVersion(string? classVersion) =>
        int.TryParse(classVersion, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) && v >= 45 ? (v - 44).ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>A mixin config name without the noise (<c>sodium.mixins.json</c> → <c>sodium</c>).</summary>
    private static string ModFromMixinConfig(string config)
    {
        var name = config.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? config[..^5] : config;
        foreach (var noise in new[] { ".mixins", "-mixins", "_mixins", ".mixin", "-common", ".common", "-client", ".client", "-fabric", "-forge", "-neoforge" })
        {
            if (name.EndsWith(noise, StringComparison.OrdinalIgnoreCase))
                name = name[..^noise.Length];
        }
        if (name.StartsWith("mixins.", StringComparison.OrdinalIgnoreCase))
            name = name[7..];
        return name.Length > 0 ? name : config;
    }

    // Ordered by how specific (and how useful) they are; the first ones are shown first.
    private static readonly Rule[] s_rules =
    [
        new("JavaTooOld",
            R(@"UnsupportedClassVersionError: [^\r\n]*?class file version (?<cv>\d+)\.\d+|Unsupported major\.minor version (?<cv>\d+)\.\d+|The requested compatibility level JAVA_(?<java>\d+) could not be set"),
            (m, _) => [G(m, "java") ?? JavaFromClassVersion(G(m, "cv"))]),
        new("JavaTooNew",
            R(@"(?:class |java\.base/)?jdk\.internal\.loader\.ClassLoaders\$AppClassLoader cannot be cast to (?:class )?java\.net\.URLClassLoader|Unsupported class file major version \d+|java\.lang\.NoSuchFieldException: ucp|Unable to make protected final java\.lang\.Class java\.lang\.ClassLoader\.defineClass|ClassNotFoundException: jdk\.nashorn\.api\.scripting\.NashornScriptEngineFactory|because module java\.base does not export|Cannot read the array length because ""urls"" is null")),
        new("ForgeNeedsOlderJava8",
            R(@"sun\.security\.util\.ManifestEntryVerifier\.<init>")),
        new("OpenJ9",
            R(@"Open J9 is not supported|OpenJ9 is incompatible|\.J9VMInternals\.")),
        new("Java32Bit",
            R(@"Could not reserve enough space for (?:\d+KB )?object heap|Invalid maximum heap size|The specified size exceeds the maximum representable size|java\.lang\.OutOfMemoryError"),
            When: c => c.JavaArch == CpuArch.X86,
            Hides: ["HeapReserveFailed", "OutOfMemory"]),
        new("HeapReserveFailed",
            R(@"Could not reserve enough space for (?:\d+KB )?object heap|Invalid maximum heap size|The specified size exceeds the maximum representable size|Initial heap size set to a larger value than the maximum heap size")),
        new("SystemOutOfMemory",
            R(@"There is insufficient memory for the Java Runtime Environment to continue|The system is out of physical RAM or swap space"),
            Hides: ["OutOfMemory", "NativeCrash"]),
        new("OutOfMemory",
            R(@"java\.lang\.OutOfMemoryError(?:: (?<kind>[^\r\n]+))?|Out of Memory Error")),
        new("GcConflict",
            R(@"Conflicting collector combinations in option list|Multiple garbage collectors selected")),
        new("JvmOptionInvalid",
            R(@"Unrecognized VM option '(?<opt>[^']+)'|Unrecognized option: (?<opt>\S+)|Improperly specified VM option '(?<opt>[^']+)'|Missing \+/- setting for VM option '(?<opt>[^']+)'"),
            (m, _) => [G(m, "opt")]),
        new("FabricIncompatibleMods",
            R(@"Mod resolution encountered an incompatible mod set!|Incompatible mods? (?:set|found)!|ModResolutionException: [^\r\n]+|Mod resolution failed"),
            Show: Show.Block,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("MissingDependencies",
            R(@"Missing or unsupported mandatory dependencies:|MissingModsException: [^\r\n]+"),
            Show: Show.Block,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("DuplicateMods",
            R(@"Found (?:a )?duplicate mods?|Duplicate mods? found|DuplicateModsFoundException|Found \d+ mods with (?:the same )?id|Mod ID '?[\w-]+'? is provided by (?:multiple|\d+) mod files|ResolutionException: Module (?<mod>[\w.]+) reads another module named \k<mod>"),
            Show: Show.Block),
        new("LoaderTooOld",
            R(@"needs language provider (?<provider>\w+):(?<version>[\w.]+) to load"),
            (m, _) => [$"{G(m, "provider")} {G(m, "version")}"]),
        new("ModCrashedLoading",
            R(@"LoaderExceptionModCrash: Caught exception from (?<name>[^\r\n]+?) \((?<mod>[^)\r\n]+)\)|Failed to create mod instance\. ModID: (?<mod>[\w-]+)|Could not execute entrypoint stage '[^']+' due to errors, provided by '(?<mod>[^']+)'|^\s*-- MOD (?<mod>[\w-]+) --"),
            (m, _) => [G(m, "name") ?? G(m, "mod")],
            All: true,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("MixinFailed",
            R(@"Mixin apply for mod (?<mod>[\w-]+) failed|Mixin (?:apply|prepare) failed (?<cfg>[\w.-]+\.json)|in config \[(?<cfg>[^\]\r\n]+)\](?: from mod \[?(?<mod>[\w-]+)\]?)? FAILED during|(?:Critical injection failure|InvalidInjectionException|InvalidMixinException|InjectionError)[^\r\n]*? in (?<cfg>[\w.-]+\.json):"),
            (m, _) => [G(m, "mod") ?? (G(m, "cfg") is { } cfg ? ModFromMixinConfig(cfg) : null)],
            All: true,
            Hides: ["CodeMissing"]),
        new("OptiFineNeoForge",
            R(@"InvalidLauncherSetupException: Invalid Services found OptiFine"),
            Hides: ["OptiFineIncompatible"]),
        new("OptiFineIncompatible",
            R(@"java\.lang\.(?:NoSuchMethodError|NoSuchFieldError|IncompatibleClassChangeError)|TRANSFORMER/net\.optifine/net\.optifine\.reflect\.Reflector\.<clinit>"),
            Also: R(@"net\.optifine\.|OptiFine_[\w.]+|\[OptiFine\]"),
            Hides: ["CodeMissing"]),
        new("RtssSodium",
            R(@"RivaTuner Statistics Server[^\r\n]*?(?:not compatible|incompatible)|RTSS[^\r\n]*?(?:not compatible|incompatible)[^\r\n]*?Sodium", RegexOptions.IgnoreCase)),
        new("GraphicsDriver",
            R(@"Pixel format not accelerated|The driver does not appear to support OpenGL|Couldn't set pixel format|GLFW error 65542|WGL: The driver|No OpenGL context found in the current thread|detected an? (?:outdated|unsupported|incompatible) (?:graphics )?driver", RegexOptions.IgnoreCase),
            Hides: ["NativeCrash"]),
        new("GpuDriverCrash",
            R(@"^#\s+C\s+\[(?<dll>(?:ig|atio|atig|atiu|amdx|amdvlk|aticfx|nvogl|nvwgf|nvd3d|igxel|igd|igc|igvk|vulkan-1)\w*\.dll)", RegexOptions.IgnoreCase),
            (m, _) => [G(m, "dll")],
            Hides: ["NativeCrash"]),
        new("NativeLibrary",
            R(@"java\.lang\.UnsatisfiedLinkError: [^\r\n]+|Failed to locate library: \S+|Can't load (?:IA 32|AMD 64|ARM 64|AARCH64)-bit \.dll on a[n]? (?:AMD 64|IA 32|ARM 64|AARCH64)-bit platform"),
            Hides: ["NativeCrash", "CodeMissing"]),
        new("CorruptFile",
            R(@"java\.util\.zip\.ZipException: (?:zip END header not found|invalid LOC header|invalid CEN header|zip file is empty|invalid entry)[^\r\n]*|java\.util\.zip\.ZipError[^\r\n]*|Error opening zip file or JAR manifest missing : [^\r\n]+|Invalid or corrupt jarfile [^\r\n]+|java\.io\.EOFException: Unexpected end of ZLIB input stream"),
            Hides: ["CodeMissing"]),
        new("SignatureMismatch",
            R(@"SHA1 digest error for [^\r\n]+|signer information does not match signer information of other classes in the same package|Invalid signature file digest for Manifest main attributes")),
        new("ConfigCorrupt",
            R(@"Failed loading config file (?<file>[^\r\n]+?) of type \w+ for modid (?<mod>[\w-]+)|ParsingException: Not enough data available|Failed to load config file [^\r\n]+"),
            Show: Show.Line),
        new("ExtractedMods",
            R(@"The directories below appear to be extracted jar files|Extracted mod jars found, loading will NOT continue")),
        new("IncompleteLoader",
            R(@"Cannot find launch target fmlclient|Failed to find Minecraft resource version [^\r\n]+|Invalid paths argument, contained no existing paths[^\r\n]*|Could not find net/minecraft/client/Minecraft\.class in classloader|Missing or invalid installer profile"),
            Hides: ["CodeMissing"]),
        new("MainClassMissing",
            R(@"Could not find or load main class (?<cls>\S+)|ClassNotFoundException: (?<cls>net\.minecraft\.launchwrapper\.Launch|cpw\.mods\.bootstraplauncher\.BootstrapLauncher|net\.fabricmc\.loader\.impl\.launch\.knot\.KnotClient|org\.quiltmc\.loader\.impl\.launch\.knot\.KnotClient)\b"),
            (m, _) => [G(m, "cls")],
            Hides: ["CodeMissing"]),
        new("InvalidModuleName",
            R(@"Invalid module name: '' is not a Java identifier")),
        new("IdLimit",
            R(@"maximum id range exceeded")),
        new("TickingEntity",
            R(@"Description: Ticking entity[\s\S]{0,20000}?Entity Type: (?<type>[^\r\n]+)[\s\S]{0,4000}?Entity's Exact location: (?<loc>[^\r\n]+)"),
            (m, _) => [G(m, "type"), G(m, "loc")],
            Show: Show.None,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("TickingBlockEntity",
            R(@"Description: Ticking block entity[\s\S]{0,20000}?Block: (?<type>[^\r\n]+)[\s\S]{0,4000}?Block location: (?<loc>[^\r\n]+)"),
            (m, _) => [G(m, "type"), G(m, "loc")],
            Show: Show.None,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("ManualCrash",
            R(@"Manually triggered debug crash"),
            Show: Show.None,
            Hides: ["CodeMissing", "SuspectedMods"]),
        new("SuspectedMods",
            R(@"Suspected Mods?: (?!NONE|None|Unknown)(?<mods>[^\r\n]+)"),
            (m, _) => [G(m, "mods")],
            Show: Show.None,
            Confidence: CrashConfidence.Possible),
        // The "Problematic frame" line of Java's fatal error banner: "# C  [lwjgl.dll+0x1234]".
        new("NativeCrash",
            R(@"^#\s+[CjJVv]\s+(?:\[[^\]\r\n]+\]|[\w.$/<>]+\()[^\r\n]*")),
        new("CodeMissing",
            R(@"^(?:\s*Caused by: |Exception in thread ""[^""]+"" |[^\r\n]*\] )?java\.lang\.(?:NoSuchMethodError|NoSuchFieldError|NoClassDefFoundError|AbstractMethodError|IncompatibleClassChangeError): (?<what>[^\r\n]{1,200})"),
            (m, _) => [G(m, "what")],
            Confidence: CrashConfidence.Possible),
    ];

    /// <summary>Exit codes of Windows exceptions that kill Java outright (no Java exception, so nothing to read).</summary>
    private static readonly int[] s_nativeExitCodes =
    [
        unchecked((int)0xC0000005), // access violation
        unchecked((int)0xC0000409), // stack buffer overrun / fail fast
        unchecked((int)0xC00000FD), // stack overflow
        unchecked((int)0xC000001D), // illegal instruction
    ];

    public static CrashAnalysis Analyze(CrashContext context)
    {
        var crashReport = FindCrashReport(context);
        var jvmLog = FindJvmCrashLog(context);
        var sources = new List<string> { Trim(context.Log, MaxLogChars) };
        if (crashReport is not null && ReadText(crashReport, MaxReportChars) is { } reportText)
            sources.Add(reportText);
        if (jvmLog is not null && ReadText(jvmLog, MaxJvmLogChars) is { } jvmText)
            sources.Add(jvmText);

        var findings = new List<CrashFinding>();
        foreach (var rule in s_rules)
        {
            if (rule.When is { } when && !when(context))
                continue;
            if (Evaluate(rule, sources, context) is { } finding)
                findings.Add(finding);
        }
        AddContextFindings(context, findings);

        // Drop what more specific findings already explain; "possible" causes only when nothing is certain.
        var hidden = findings.SelectMany(f => s_rules.FirstOrDefault(r => r.Id == f.Id)?.Hides ?? []).ToHashSet(StringComparer.Ordinal);
        findings.RemoveAll(f => hidden.Contains(f.Id));
        if (findings.Any(f => f.Confidence == CrashConfidence.Likely && f.Id != "NonAsciiPath"))
            findings.RemoveAll(f => f.Confidence == CrashConfidence.Possible && f.Id != "SuspectedMods");
        return new CrashAnalysis(findings, crashReport, jvmLog);
    }

    private static CrashFinding? Evaluate(Rule rule, List<string> sources, CrashContext context)
    {
        var args = new List<string?[]>();
        var details = new List<string>();
        try
        {
            foreach (var text in sources)
            {
                if (rule.Also is { } also && !also.IsMatch(text))
                    continue;
                for (var m = rule.Pattern.Match(text); m.Success; m = m.NextMatch())
                {
                    args.Add(rule.Args?.Invoke(m, context) ?? []);
                    if (rule.Show != Show.None && details.Count < 16)
                    {
                        foreach (var line in Evidence(text, m, rule.Show))
                        {
                            if (!details.Contains(line))
                                details.Add(line);
                        }
                    }
                    if (!rule.All || args.Count >= 50)
                        break;
                }
                if (args.Count > 0 && !rule.All)
                    break;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        if (args.Count == 0)
            return null;

        string[] values;
        if (rule.All)
        {
            // One argument: every distinct value, in the order they appeared.
            var distinct = args.Select(a => a.FirstOrDefault()).OfType<string>().Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (distinct.Count == 0)
                return null;
            values = [string.Join(", ", distinct.Take(8)) + (distinct.Count > 8 ? ", …" : "")];
        }
        else
        {
            var first = args[0];
            if (first.Any(a => string.IsNullOrEmpty(a)))
                return null; // The advice needs every argument.
            values = first.Select(a => a!).ToArray();
        }
        return new CrashFinding(rule.Id, values, details.Count > 0 ? string.Join('\n', details.Take(16)) : null, rule.Confidence);
    }

    private static void AddContextFindings(CrashContext context, List<CrashFinding> findings)
    {
        if (context.JavaMajor is { } java && context.RequiredJavaMajor is { } required && java < required && findings.All(f => f.Id != "JavaTooOld"))
            findings.Insert(0, new CrashFinding("JavaTooOld", [required.ToString(CultureInfo.InvariantCulture)], null, CrashConfidence.Likely));

        if (context.ExitCode is { } code && s_nativeExitCodes.Contains(code) && findings.All(f => f.Id is not ("NativeCrash" or "GpuDriverCrash" or "GraphicsDriver")))
            findings.Add(new CrashFinding("NativeCrash", [], null, CrashConfidence.Likely));

        if (context.GameDirectory is { } dir && dir.Any(c => c > 127))
            findings.Add(new CrashFinding("NonAsciiPath", [dir], null, CrashConfidence.Possible));
    }

    /// <summary>The matched line, or for <see cref="Show.Block"/> the explanation that follows it.</summary>
    private static List<string> Evidence(string text, Match m, Show show)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, m.Index - 1)) + 1;
        if (m.Index == 0)
            start = 0;
        var lines = new List<string>();
        var pos = start;
        var max = show == Show.Block ? 14 : 1;
        while (pos < text.Length && lines.Count < max)
        {
            var end = text.IndexOf('\n', pos);
            if (end < 0)
                end = text.Length;
            var line = text[pos..end].TrimEnd('\r');
            pos = end + 1;
            if (lines.Count > 0)
            {
                var trimmed = line.TrimStart();
                // Stop at the stack trace, a blank line or the next log line.
                if (trimmed.Length == 0 || trimmed.StartsWith("at ", StringComparison.Ordinal) || LogLineStart().IsMatch(line))
                    break;
            }
            line = line.Trim();
            if (line.Length > 300)
                line = line[..300] + "…";
            if (line.Length > 0)
                lines.Add(line);
        }
        return lines;
    }

    [GeneratedRegex(@"^\[\d{1,2}:\d{2}:\d{2}")]
    private static partial Regex LogLineStart();

    [GeneratedRegex(@"Crash report saved to:? (?:#@!@# )?(?<path>[^\r\n]+?\.txt)\s*$", RegexOptions.Multiline)]
    private static partial Regex CrashReportPath();

    [GeneratedRegex(@"(?<path>[A-Za-z]:\\[^\r\n]*?hs_err_pid\d+\.log)")]
    private static partial Regex JvmLogPath();

    private static string? FindCrashReport(CrashContext context)
    {
        foreach (Match m in CrashReportPath().Matches(context.Log))
        {
            var path = m.Groups["path"].Value.Trim();
            if (!Path.IsPathRooted(path) && context.GameDirectory is { } gd)
                path = Path.Combine(gd, path);
            if (File.Exists(path))
                return path;
        }
        return NewestSince(context.GameDirectory is { } dir ? Path.Combine(dir, "crash-reports") : null, "*.txt", context.Since);
    }

    private static string? FindJvmCrashLog(CrashContext context)
    {
        if (JvmLogPath().Match(context.Log) is { Success: true } m && File.Exists(m.Groups["path"].Value))
            return m.Groups["path"].Value;
        return NewestSince(context.GameDirectory, "hs_err_pid*.log", context.Since);
    }

    private static string? NewestSince(string? dir, string pattern, DateTimeOffset? since)
    {
        if (dir is null || since is null || !Directory.Exists(dir))
            return null;
        try
        {
            var limit = since.Value.UtcDateTime.AddSeconds(-5);
            return new DirectoryInfo(dir).EnumerateFiles(pattern)
                .Where(f => f.LastWriteTimeUtc >= limit)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReadText(string path, int maxChars)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var buffer = new char[maxChars];
            var read = reader.ReadBlock(buffer, 0, maxChars);
            return new string(buffer, 0, read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The start (versions, mod list) and the end (the crash) of a log too long to search whole.</summary>
    private static string Trim(string text, int max) =>
        text.Length <= max ? text : string.Concat(text.AsSpan(0, max / 4), "\n", text.AsSpan(text.Length - max * 3 / 4));
}
