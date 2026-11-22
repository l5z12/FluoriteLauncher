namespace Fluorite.Modding.Util;

/// <summary>
/// Orders loader versions like <c>0.16.10</c>, <c>47.2.0</c>, <c>21.1.77-beta</c>, <c>0.20.0-beta.9</c>,
/// <c>1.7.10-10.13.4.1614-1.7.10</c>: numeric segments compare numerically, and a pre-release tag
/// (alpha/beta/pre/rc) sorts before the same version without one.
/// </summary>
public sealed class LoaderVersionComparer : IComparer<string>
{
    public static LoaderVersionComparer Instance { get; } = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var (xMain, xPre) = Split(x);
        var (yMain, yPre) = Split(y);
        var c = CompareSegments(xMain, yMain);
        if (c != 0)
            return c;
        if (xPre is null && yPre is null)
            return 0;
        if (xPre is null)
            return 1; // release > pre-release
        if (yPre is null)
            return -1;
        return CompareSegments(xPre, yPre);
    }

    private static (string Main, string? Pre) Split(string v)
    {
        var lower = v.ToLowerInvariant();
        foreach (var tag in new[] { "-alpha", "-beta", "-pre", "-rc", "-snapshot", "+" })
        {
            var i = lower.IndexOf(tag, StringComparison.Ordinal);
            if (i > 0)
                return (v[..i], v[(i + 1)..]);
        }
        return (v, null);
    }

    private static int CompareSegments(string a, string b)
    {
        var sa = a.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var sb = b.Split(['.', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < Math.Max(sa.Length, sb.Length); i++)
        {
            var pa = i < sa.Length ? sa[i] : "0";
            var pb = i < sb.Length ? sb[i] : "0";
            var na = long.TryParse(pa, out var ia);
            var nb = long.TryParse(pb, out var ib);
            int c;
            if (na && nb)
                c = ia.CompareTo(ib);
            else if (na)
                c = 1; // numbers after words (e.g. "10" > "beta")
            else if (nb)
                c = -1;
            else
                c = string.Compare(pa, pb, StringComparison.OrdinalIgnoreCase);
            if (c != 0)
                return c;
        }
        return 0;
    }
}
