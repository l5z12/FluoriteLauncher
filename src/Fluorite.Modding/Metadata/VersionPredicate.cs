namespace Fluorite.Modding.Metadata;

/// <summary>
/// Lenient evaluation of dependency version constraints. Returns <c>null</c> when either side can't be understood, so
/// callers only warn about constraints they're sure are violated.
/// <list type="bullet">
/// <item>Fabric/Quilt predicates: <c>*</c>, <c>1.20.x</c>, <c>=</c> <c>&gt;=</c> <c>&lt;=</c> <c>&gt;</c> <c>&lt;</c> <c>~</c> <c>^</c>,
/// space-separated terms are AND, <c>||</c> (our join of predicate arrays) is OR.</item>
/// <item>Maven ranges (Forge/NeoForge): <c>[a,b)</c>, <c>(a,]</c>, <c>[a]</c>, comma-joined unions; a bare version is a
/// soft requirement that matches anything.</item>
/// </list>
/// </summary>
public static class VersionPredicate
{
    public static bool? Matches(string? predicate, string? version, ModLoaderKind loader) =>
        loader is ModLoaderKind.Fabric or ModLoaderKind.Quilt ? MatchesFabric(predicate, version) : MatchesMaven(predicate, version);

    public static bool? MatchesFabric(string? predicate, string? version)
    {
        if (string.IsNullOrWhiteSpace(predicate) || predicate.Trim() == "*")
            return true;
        if (LooseVersion.TryParse(version, out var v) is false)
            return null;

        bool? any = false;
        foreach (var alternative in predicate.Split("||", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            bool? all = true;
            foreach (var term in alternative.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var r = MatchFabricTerm(term, v);
                if (r is null)
                    return null;
                all &= r;
            }
            if (all == true)
                return true;
            any |= all;
        }
        return any;
    }

    private static bool? MatchFabricTerm(string term, LooseVersion v)
    {
        if (term == "*")
            return true;
        string op = "=";
        foreach (var candidate in new[] { ">=", "<=", ">", "<", "=", "~", "^" })
        {
            if (term.StartsWith(candidate, StringComparison.Ordinal))
            {
                op = candidate;
                term = term[candidate.Length..];
                break;
            }
        }
        term = term.TrimEnd('-');

        // Wildcards: 1.20.x / 1.20.X / 1.20.*
        var parts = term.Split('.');
        var wildcard = Array.FindIndex(parts, p => p is "x" or "X" or "*");
        if (wildcard >= 0)
        {
            if (op != "=" || !LooseVersion.TryParse(string.Join('.', parts[..wildcard]), out var prefix))
                return wildcard == 0 ? true : null;
            for (var i = 0; i < prefix.Core.Length; i++)
            {
                if (v.Part(i) != prefix.Core[i])
                    return false;
            }
            return true;
        }

        if (!LooseVersion.TryParse(term, out var target))
            return null;
        var cmp = v.CompareTo(target);
        return op switch
        {
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            ">" => cmp > 0,
            "<" => cmp < 0,
            "=" => cmp == 0,
            // ~1.2.3 → >=1.2.3 <1.3 ; ~1 → >=1 <2
            "~" => cmp >= 0 && v.CompareTo(target.Bump(Math.Min(1, target.Core.Length - 1))) < 0,
            // ^1.2.3 → >=1.2.3 <2 (Fabric bumps the major, no 0.x special case)
            "^" => cmp >= 0 && v.CompareTo(target.Bump(0)) < 0,
            _ => null,
        };
    }

    public static bool? MatchesMaven(string? range, string? version)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() == "*")
            return true;
        range = range.Trim();
        if (range[0] is not ('[' or '('))
            return true; // Soft requirement.
        if (!LooseVersion.TryParse(version, out var v))
            return null;

        var i = 0;
        while (i < range.Length)
        {
            var open = range[i];
            var close = range.IndexOfAny([']', ')'], i);
            if (open is not ('[' or '(') || close < 0)
                return null;
            var body = range[(i + 1)..close];
            var r = MatchMavenRestriction(open == '[', body, range[close] == ']', v);
            if (r is null)
                return null;
            if (r == true)
                return true;
            i = close + 1;
            while (i < range.Length && range[i] is ',' or ' ')
                i++;
        }
        return false;
    }

    private static bool? MatchMavenRestriction(bool lowerInclusive, string body, bool upperInclusive, LooseVersion v)
    {
        var comma = body.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            // [1.0] = exactly 1.0
            return LooseVersion.TryParse(body.Trim(), out var exact) ? v.CompareTo(exact) == 0 : null;
        }
        var lower = body[..comma].Trim();
        var upper = body[(comma + 1)..].Trim();
        if (lower.Length > 0)
        {
            if (!LooseVersion.TryParse(lower, out var lo))
                return null;
            var c = v.CompareTo(lo);
            if (c < 0 || (c == 0 && !lowerInclusive))
                return false;
        }
        if (upper.Length > 0)
        {
            if (!LooseVersion.TryParse(upper, out var hi))
                return null;
            var c = v.CompareTo(hi);
            if (c > 0 || (c == 0 && !upperInclusive))
                return false;
        }
        return true;
    }
}

/// <summary>
/// A dotted numeric version with an optional pre-release tail (<c>1.20.1</c>, <c>0.92.2+1.20.1</c>,
/// <c>0.5.0-beta.1</c>, <c>26.1</c>). Build metadata after <c>+</c> is ignored; missing parts compare as 0.
/// </summary>
public readonly record struct LooseVersion(int[] Core, string? PreRelease) : IComparable<LooseVersion>
{
    public static bool TryParse(string? text, out LooseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];
        var plus = s.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
            s = s[..plus];
        string? pre = null;
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        if (dash >= 0)
        {
            pre = s[(dash + 1)..];
            s = s[..dash];
            if (pre.Length == 0)
                pre = null;
        }
        var parts = s.Split('.');
        if (parts.Length == 0 || parts.Length > 6)
            return false;
        var core = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out core[i]))
                return false;
        }
        version = new LooseVersion(core, pre);
        return true;
    }

    public int Part(int index) => index < Core.Length ? Core[index] : 0;

    /// <summary>Increments part <paramref name="index"/> and drops everything after it (1.2.3 bumped at 1 → 1.3).</summary>
    public LooseVersion Bump(int index)
    {
        var core = new int[index + 1];
        for (var i = 0; i < index; i++)
            core[i] = Part(i);
        core[index] = Part(index) + 1;
        return new LooseVersion(core, null);
    }

    public int CompareTo(LooseVersion other)
    {
        var n = Math.Max(Core.Length, other.Core.Length);
        for (var i = 0; i < n; i++)
        {
            var c = Part(i).CompareTo(other.Part(i));
            if (c != 0)
                return c;
        }
        if (PreRelease is null)
            return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null)
            return -1;
        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var pa = a.Split('.');
        var pb = b.Split('.');
        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var na = int.TryParse(pa[i], out var ia);
            var nb = int.TryParse(pb[i], out var ib);
            var c = na && nb ? ia.CompareTo(ib) : na ? -1 : nb ? 1 : string.CompareOrdinal(pa[i], pb[i]);
            if (c != 0)
                return c;
        }
        return pa.Length.CompareTo(pb.Length);
    }

    public bool Equals(LooseVersion other) => CompareTo(other) == 0;

    public override int GetHashCode()
    {
        var h = new HashCode();
        var n = Core.Length;
        while (n > 0 && Core[n - 1] == 0)
            n--;
        for (var i = 0; i < n; i++)
            h.Add(Core[i]);
        h.Add(PreRelease);
        return h.ToHashCode();
    }

    public override string ToString() => string.Join('.', Core) + (PreRelease is null ? "" : "-" + PreRelease);

    public static bool operator <(LooseVersion left, LooseVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(LooseVersion left, LooseVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(LooseVersion left, LooseVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(LooseVersion left, LooseVersion right) => left.CompareTo(right) >= 0;
}
