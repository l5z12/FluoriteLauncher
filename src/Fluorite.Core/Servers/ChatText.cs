using System.Text;
using System.Text.Json;

namespace Fluorite.Core.Servers;

/// <summary>A run of Minecraft text with one style. <see cref="Color"/> is <c>#RRGGBB</c>, or null for the default.</summary>
public sealed record ChatSpan(string Text, string? Color = null, bool Bold = false, bool Italic = false, bool Underlined = false, bool Strikethrough = false, bool Obfuscated = false);

/// <summary>
/// Turns Minecraft text (JSON text components and legacy <c>§</c> formatting codes) into styled runs, e.g. for a
/// server's message of the day.
/// </summary>
public static class ChatText
{
    private static readonly string[] s_codeColors =
    [
        "#000000", "#0000AA", "#00AA00", "#00AAAA", "#AA0000", "#AA00AA", "#FFAA00", "#AAAAAA",
        "#555555", "#5555FF", "#55FF55", "#55FFFF", "#FF5555", "#FF55FF", "#FFFF55", "#FFFFFF",
    ];

    private static readonly Dictionary<string, string> s_namedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = s_codeColors[0], ["dark_blue"] = s_codeColors[1], ["dark_green"] = s_codeColors[2], ["dark_aqua"] = s_codeColors[3],
        ["dark_red"] = s_codeColors[4], ["dark_purple"] = s_codeColors[5], ["gold"] = s_codeColors[6], ["gray"] = s_codeColors[7],
        ["dark_gray"] = s_codeColors[8], ["blue"] = s_codeColors[9], ["green"] = s_codeColors[10], ["aqua"] = s_codeColors[11],
        ["red"] = s_codeColors[12], ["light_purple"] = s_codeColors[13], ["yellow"] = s_codeColors[14], ["white"] = s_codeColors[15],
    };

    private readonly record struct Style(string? Color, bool Bold, bool Italic, bool Underlined, bool Strikethrough, bool Obfuscated);

    public static IReadOnlyList<ChatSpan> FromLegacy(string text)
    {
        var spans = new List<ChatSpan>();
        AppendLegacy(spans, text, default);
        return Merge(spans);
    }

    /// <summary>A JSON text component: a string, an array, or an object with <c>text</c>/<c>extra</c>/styles.</summary>
    public static IReadOnlyList<ChatSpan> FromJson(JsonElement component)
    {
        var spans = new List<ChatSpan>();
        AppendComponent(spans, component, default, 0);
        return Merge(spans);
    }

    public static string ToPlainText(IEnumerable<ChatSpan> spans) => string.Concat(spans.Select(s => s.Text));

    /// <summary>Removes <c>§</c> formatting codes.</summary>
    public static string StripCodes(string text) => ToPlainText(FromLegacy(text));

    private static void AppendComponent(List<ChatSpan> spans, JsonElement e, Style parent, int depth)
    {
        if (depth > 64)
            return;
        switch (e.ValueKind)
        {
            case JsonValueKind.String:
                AppendLegacy(spans, e.GetString() ?? "", parent);
                break;
            case JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False:
                AppendLegacy(spans, e.GetRawText(), parent);
                break;
            case JsonValueKind.Array:
            {
                // [first, rest…]: the rest inherit the first element's style.
                var first = true;
                var style = parent;
                foreach (var item in e.EnumerateArray())
                {
                    if (first)
                    {
                        style = item.ValueKind == JsonValueKind.Object ? Apply(parent, item) : parent;
                        AppendComponent(spans, item, parent, depth + 1);
                        first = false;
                    }
                    else
                    {
                        AppendComponent(spans, item, style, depth + 1);
                    }
                }
                break;
            }
            case JsonValueKind.Object:
            {
                var style = Apply(parent, e);
                var text = Prop(e, "text");
                if (text is null && Prop(e, "translate") is { } key)
                    text = Prop(e, "fallback") ?? key;
                text ??= Prop(e, "keybind") ?? Prop(e, "selector");
                if (text is not null)
                    AppendLegacy(spans, text, style);
                if (e.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
                {
                    foreach (var child in extra.EnumerateArray())
                        AppendComponent(spans, child, style, depth + 1);
                }
                break;
            }
        }
    }

    private static string? Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) ? p.ValueKind == JsonValueKind.String ? p.GetString() : p.ValueKind is JsonValueKind.Number ? p.GetRawText() : null : null;

    private static Style Apply(Style s, JsonElement e)
    {
        if (e.TryGetProperty("color", out var c) && c.ValueKind == JsonValueKind.String)
            s = s with { Color = ParseColor(c.GetString()) };
        return s with
        {
            Bold = Flag(e, "bold") ?? s.Bold,
            Italic = Flag(e, "italic") ?? s.Italic,
            Underlined = Flag(e, "underlined") ?? s.Underlined,
            Strikethrough = Flag(e, "strikethrough") ?? s.Strikethrough,
            Obfuscated = Flag(e, "obfuscated") ?? s.Obfuscated,
        };
    }

    private static bool? Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => string.Equals(v.GetString(), "true", StringComparison.OrdinalIgnoreCase),
            _ => null,
        } : null;

    private static string? ParseColor(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Equals("reset", StringComparison.OrdinalIgnoreCase))
            return null;
        if (s_namedColors.TryGetValue(value, out var named))
            return named;
        return value.Length == 7 && value[0] == '#' && value.Skip(1).All(char.IsAsciiHexDigit) ? value.ToUpperInvariant() : null;
    }

    /// <summary>Appends text with <c>§</c> codes; a colour code resets bold/italic/…, <c>§r</c> resets to <paramref name="base"/>.</summary>
    private static void AppendLegacy(List<ChatSpan> spans, string text, Style @base)
    {
        var style = @base;
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch != '§' || i + 1 >= text.Length)
            {
                sb.Append(ch);
                continue;
            }
            var code = char.ToLowerInvariant(text[i + 1]);
            Style next;
            var consumed = 1;
            if (code is >= '0' and <= '9' or >= 'a' and <= 'f')
            {
                next = new Style(s_codeColors[Convert.ToInt32(code.ToString(), 16)], false, false, false, false, false);
            }
            else if (code == 'x' && TryHexSequence(text, i + 2, out var hex))
            {
                // BungeeCord hex colours: §x§R§R§G§G§B§B
                next = new Style(hex, false, false, false, false, false);
                consumed = 13;
            }
            else
            {
                next = code switch
                {
                    'k' => style with { Obfuscated = true },
                    'l' => style with { Bold = true },
                    'm' => style with { Strikethrough = true },
                    'n' => style with { Underlined = true },
                    'o' => style with { Italic = true },
                    'r' => @base,
                    _ => style,
                };
            }
            Flush();
            style = next;
            i += consumed;
        }
        Flush();

        void Flush()
        {
            if (sb.Length == 0)
                return;
            spans.Add(new ChatSpan(sb.ToString(), style.Color, style.Bold, style.Italic, style.Underlined, style.Strikethrough, style.Obfuscated));
            sb.Clear();
        }
    }

    private static bool TryHexSequence(string text, int start, out string hex)
    {
        hex = "";
        if (start + 12 > text.Length)
            return false;
        var sb = new StringBuilder("#");
        for (var k = 0; k < 6; k++)
        {
            var p = start + k * 2;
            if (text[p] != '§' || !char.IsAsciiHexDigit(text[p + 1]))
                return false;
            sb.Append(char.ToUpperInvariant(text[p + 1]));
        }
        hex = sb.ToString();
        return true;
    }

    private static List<ChatSpan> Merge(List<ChatSpan> spans)
    {
        var result = new List<ChatSpan>(spans.Count);
        foreach (var s in spans)
        {
            if (result.Count > 0 && result[^1] is var last && last with { Text = "" } == s with { Text = "" })
                result[^1] = last with { Text = last.Text + s.Text };
            else
                result.Add(s);
        }
        return result;
    }
}
