using System.Text.RegularExpressions;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Versions;

/// <summary>Launch-time feature flags referenced by <c>rules[].features</c>.</summary>
public sealed record LaunchFeatures
{
    public bool IsDemoUser { get; init; }
    public bool HasCustomResolution { get; init; }
    public bool HasQuickPlaysSupport { get; init; }
    public bool IsQuickPlaySingleplayer { get; init; }
    public bool IsQuickPlayMultiplayer { get; init; }
    public bool IsQuickPlayRealms { get; init; }

    public bool Get(string name) => name switch
    {
        "is_demo_user" => IsDemoUser,
        "has_custom_resolution" => HasCustomResolution,
        "has_quick_plays_support" => HasQuickPlaysSupport,
        "is_quick_play_singleplayer" => IsQuickPlaySingleplayer,
        "is_quick_play_multiplayer" => IsQuickPlayMultiplayer,
        "is_quick_play_realms" => IsQuickPlayRealms,
        _ => false, // Unknown features are treated as unset, like the official launcher.
    };
}

/// <summary>
/// Evaluates Mojang-style rules. Note <see cref="Arch"/> is the architecture of the <em>Java runtime</em>
/// that will run the game (x64 Java on an ARM64 PC must get x64 natives).
/// </summary>
public sealed record RuleContext(string OsName, string OsVersion, CpuArch Arch, LaunchFeatures Features)
{
    public static RuleContext ForCurrentOs(CpuArch javaArch, LaunchFeatures? features = null) =>
        new(RuntimePlatform.Current.OsName, RuntimePlatform.Current.OsVersion, javaArch, features ?? new LaunchFeatures());
}

public static class RuleEvaluator
{
    /// <summary>
    /// No rules → allowed. Otherwise start disallowed and let every matching rule set the outcome (last match wins).
    /// </summary>
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, RuleContext ctx)
    {
        if (rules is null || rules.Count == 0)
            return true;

        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, ctx))
                allowed = !string.Equals(rule.Action, "disallow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool Matches(Rule rule, RuleContext ctx)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !OsNameMatches(os.Name, ctx.OsName))
                return false;
            if (os.Version is not null && !SafeRegexMatch(ctx.OsVersion, os.Version))
                return false;
            if (os.Arch is not null && !ArchMatches(os.Arch, ctx.Arch))
                return false;
        }

        if (rule.Features is not null)
        {
            foreach (var (name, expected) in rule.Features)
            {
                if (ctx.Features.Get(name) != expected)
                    return false;
            }
        }
        return true;
    }

    private static bool OsNameMatches(string ruleName, string osName) =>
        string.Equals(ruleName, osName, StringComparison.OrdinalIgnoreCase)
        // Some third-party JSONs use "macos"/"mac".
        || (osName == "osx" && ruleName is "macos" or "mac");

    private static bool ArchMatches(string ruleArch, CpuArch arch)
    {
        var parsed = CpuArchExtensions.Parse(ruleArch);
        // Mojang's "x86" means 32-bit x86 specifically.
        return parsed != CpuArch.Unknown && parsed == arch;
    }

    private static bool SafeRegexMatch(string input, string pattern)
    {
        try
        {
            return Regex.IsMatch(input, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
