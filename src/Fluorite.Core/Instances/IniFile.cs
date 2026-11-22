using System.Text;

namespace Fluorite.Core.Instances;

/// <summary>
/// Reads and edits MultiMC/Prism <c>instance.cfg</c>-style INI files: the old section-less MultiMC format and the
/// Qt <c>QSettings</c> format Prism writes (<c>[General]</c> section, quoted values, backslash escapes). Edits keep
/// every other line byte-for-byte, so writing back one key never reformats the file.
/// </summary>
public sealed class IniFile
{
    private readonly List<string> _lines;
    private readonly Dictionary<string, (int Line, string Value)> _values = new(StringComparer.Ordinal);

    private IniFile(List<string> lines)
    {
        _lines = lines;
        string? section = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
                continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                section = line[1..^1];
                continue;
            }
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
                continue;
            var key = line[..eq].Trim();
            if (section is not null && !section.Equals("General", StringComparison.OrdinalIgnoreCase))
                key = section + "/" + key;
            _values[key] = (i, Decode(line[(eq + 1)..].Trim()));
        }
    }

    /// <summary>True for Prism's QSettings format (it has a <c>[General]</c> section).</summary>
    public bool IsSectioned => _lines.Any(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));

    public static IniFile Parse(string text) => new([.. text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')]);

    public static IniFile Load(string path) => Parse(File.ReadAllText(path, Encoding.UTF8));

    public IEnumerable<string> Keys => _values.Keys;

    public string? Get(string key) => _values.TryGetValue(key, out var v) ? v.Value : null;

    public bool GetBool(string key) => Get(key) is { } v && (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1");

    public long? GetLong(string key) => long.TryParse(Get(key), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Sets (or adds) a top-level / <c>[General]</c> key.</summary>
    public void Set(string key, string value)
    {
        var encoded = $"{key}={Encode(value)}";
        if (_values.TryGetValue(key, out var existing))
        {
            _lines[existing.Line] = encoded;
            _values[key] = (existing.Line, value);
            return;
        }

        // Insert at the end of [General] (sectioned) or before the first section (old format).
        var insertAt = _lines.Count;
        var general = _lines.FindIndex(l => l.Trim().Equals("[General]", StringComparison.OrdinalIgnoreCase));
        var nextSection = _lines.FindIndex(general + 1, l => l.TrimStart().StartsWith('['));
        if (nextSection >= 0)
            insertAt = nextSection;
        while (insertAt > 0 && insertAt <= _lines.Count && _lines[insertAt - 1].Trim().Length == 0)
            insertAt--;
        _lines.Insert(insertAt, encoded);
        foreach (var k in _values.Keys.ToList())
        {
            if (_values[k].Line >= insertAt)
                _values[k] = (_values[k].Line + 1, _values[k].Value);
        }
        _values[key] = (insertAt, value);
    }

    public override string ToString() => string.Join("\n", _lines);

    public void Save(string path)
    {
        var tmp = path + ".fluorite-tmp";
        File.WriteAllText(tmp, ToString(), new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }

    internal static string Decode(string raw)
    {
        var quoted = raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"';
        var s = quoted ? raw[1..^1] : raw;
        if (!s.Contains('\\', StringComparison.Ordinal))
            return s;
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c != '\\' || i + 1 >= s.Length)
            {
                sb.Append(c);
                continue;
            }
            var n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case 'r': sb.Append('\r'); break;
                case 'x':
                    var j = i + 1;
                    while (j < s.Length && j < i + 5 && Uri.IsHexDigit(s[j]))
                        j++;
                    if (j > i + 1)
                    {
                        sb.Append((char)Convert.ToInt32(s[(i + 1)..j], 16));
                        i = j - 1;
                    }
                    break;
                default: sb.Append(n); break; // \\ \" \# \; \, …
            }
        }
        return sb.ToString();
    }

    public static string Encode(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append(@"\n"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\r': sb.Append(@"\r"); break;
                default: sb.Append(c); break;
            }
        }
        var text = sb.ToString();
        var needsQuotes = value.Length > 0 && (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || value.IndexOfAny([';', ',', '=', '#']) >= 0);
        return needsQuotes ? $"\"{text}\"" : text;
    }
}
