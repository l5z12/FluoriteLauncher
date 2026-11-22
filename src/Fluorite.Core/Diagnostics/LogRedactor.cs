using System.Text.RegularExpressions;

namespace Fluorite.Core.Diagnostics;

/// <summary>Removes what shouldn't leave the PC from a log before it's uploaded or shared.</summary>
public static partial class LogRedactor
{
    public const string Mask = "********";

    /// <summary>
    /// Masks <paramref name="secrets"/> (access tokens, sign-in names), token arguments the game or a loader echoed,
    /// and the Windows user name in paths (<c>C:\Users\name\…</c> → <c>C:\Users\&lt;user&gt;\…</c>).
    /// </summary>
    public static string Redact(string text, IEnumerable<string?> secrets)
    {
        foreach (var secret in secrets)
        {
            if (secret is { Length: >= 6 })
                text = text.Replace(secret, Mask, StringComparison.Ordinal);
        }
        text = TokenArgument().Replace(text, "${name}" + Mask);
        text = SessionToken().Replace(text, "token:" + Mask + ":");
        text = UserFolder().Replace(text, "${prefix}<user>");
        return text;
    }

    /// <summary><c>--accessToken abc</c>, <c>--accessToken, abc</c> (LaunchWrapper's argument dump), <c>--session abc</c>.</summary>
    [GeneratedRegex(@"(?<name>--(?:accessToken|session|clientToken)[,=]?\s+)(?!\*{8})[^\s,\]]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenArgument();

    /// <summary>Old versions print <c>(Session ID is token:&lt;token&gt;:&lt;uuid&gt;)</c>.</summary>
    [GeneratedRegex(@"token:(?!\*{8})[\w.\-]{16,}:")]
    private static partial Regex SessionToken();

    [GeneratedRegex(@"(?<prefix>[\\/]Users[\\/]{1,2})(?!Public\b|Default\b|<user>)[^\\/\r\n""':;*?<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();
}
