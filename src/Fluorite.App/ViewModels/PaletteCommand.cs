using System.Globalization;

namespace Fluorite.App.ViewModels;

/// <summary>One entry in the command palette (Ctrl+K).</summary>
public sealed class PaletteCommand(string glyph, string title, string? subtitle, Func<Task> run)
{
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public string? Subtitle { get; } = subtitle;
    public Func<Task> Run { get; } = run;

    // Internal: XAML's type info writes setters for public properties, which init-only ones can't have.

    /// <summary>Extra words it's found by (an instance's name, a setting's description).</summary>
    internal string? Keywords { get; init; }

    /// <summary>Listed before anything is typed.</summary>
    internal bool Suggested { get; init; }

    /// <summary>What Narrator reads for the row.</summary>
    public override string ToString() => Subtitle is null ? Title : $"{Title}, {Subtitle}";

    /// <summary>
    /// How well every word typed matches (0: not at all). The title counts most; a match at the start of a word
    /// beats one inside a word.
    /// </summary>
    public int Score(IReadOnlyList<string> terms)
    {
        var total = 0;
        foreach (var term in terms)
        {
            var s = Match(Title, term) * 3;
            if (s == 0)
                s = Match(Keywords, term) * 2;
            if (s == 0)
                s = Match(Subtitle, term);
            if (s == 0)
                return 0;
            total += s;
        }
        return total;
    }

    private static int Match(string? text, string term)
    {
        if (string.IsNullOrEmpty(text))
            return 0;
        var i = CultureInfo.CurrentCulture.CompareInfo.IndexOf(text, term, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreWidth);
        if (i < 0)
            return 0;
        if (i == 0)
            return 4;
        return char.IsLetterOrDigit(text[i - 1]) ? 1 : 3;
    }
}
