using System.Text;

namespace Fluorite.Core.Launch;

/// <summary>Windows command-line quoting compatible with <c>CommandLineToArgvW</c> and the MSVC CRT.</summary>
public static class CommandLine
{
    public static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
            return arg;

        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    public static string Join(IEnumerable<string> args) => string.Join(' ', args.Select(Quote));

    /// <summary>
    /// User-entered arguments as one line (null when empty): line breaks become spaces, so text typed or pasted one
    /// flag per line still fits the one-line fields of other launchers' files (instance.cfg, launcher_profiles.json).
    /// </summary>
    public static string? ToSingleLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Trim();

    /// <summary>Splits a user-entered argument string (respects double quotes).</summary>
    public static List<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;
        var hasToken = false;
        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                i++;
                hasToken = true;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }
        if (hasToken)
            result.Add(current.ToString());
        return result;
    }
}
