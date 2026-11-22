using System.Collections.ObjectModel;

namespace Fluorite.App.ViewModels;

/// <summary>
/// Brings a bound list to a new content with the fewest changes (removes, moves, inserts) instead of clearing and
/// refilling it. A refresh that finds the same items changes nothing at all; the list keeps its selection, focus and
/// scroll position, and isn't rebuilt from scratch on every refresh (a list control reset again and again in quick
/// succession, F5 held down, ended the app inside WinUI).
/// </summary>
public static class CollectionSync
{
    public static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        var wanted = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }
        for (var i = 0; i < desired.Count; i++)
        {
            if (i < target.Count && ReferenceEquals(target[i], desired[i]))
                continue;
            var at = -1;
            for (var j = i + 1; j < target.Count; j++)
            {
                if (ReferenceEquals(target[j], desired[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0)
                target.Move(at, i);
            else
                target.Insert(i, desired[i]);
        }
        while (target.Count > desired.Count)
            target.RemoveAt(target.Count - 1);
    }
}
