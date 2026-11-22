using System.Collections.ObjectModel;
using Fluorite.App.Services;
using Fluorite.Core.Java;
using Fluorite.Core.Platform;

namespace Fluorite.App.ViewModels;

/// <summary>
/// An entry in a Java dropdown: the default (<see cref="Path"/> null), a runtime found on the PC, a custom path, or
/// "Browse…" (<see cref="IsBrowse"/>).
/// </summary>
public sealed record JavaChoice(string? Path, string Title, string? Detail)
{
    public bool IsBrowse { get; init; }

    public static JavaChoice For(JavaInstallation java)
    {
        var parts = new List<string> { "Java " + java.Version };
        if (!string.IsNullOrWhiteSpace(java.Vendor))
            parts.Add(java.Vendor);
        if (java.Arch switch { CpuArch.X86 => "x86", CpuArch.X64 => "x64", CpuArch.Arm64 => "ARM64", _ => null } is { } arch)
            parts.Add(arch);
        return new JavaChoice(java.LaunchExecutable, string.Join(" · ", parts), java.Home);
    }

    /// <summary>
    /// Fills a dropdown: <paramref name="first"/>, the runtimes found (newest first), <paramref name="current"/> as a
    /// custom entry when it isn't one of them, then "Browse…". Returns the index to select.
    /// </summary>
    public static int Fill(ObservableCollection<JavaChoice> choices, JavaChoice first, IEnumerable<JavaInstallation> found, string? current)
    {
        choices.Clear();
        choices.Add(first);
        foreach (var java in found.DistinctBy(j => Normalize(j.Home), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(j => j.Major).ThenBy(j => j.Home, StringComparer.OrdinalIgnoreCase))
            choices.Add(For(java));
        var index = IndexOf(choices, current);
        if (index < 0)
        {
            choices.Add(new JavaChoice(current!.Trim().Trim('"'), current.Trim().Trim('"'), Loc.Get("Java_CustomPath")));
            index = choices.Count - 1;
        }
        choices.Add(new JavaChoice(null, Loc.Get("Java_Browse"), null) { IsBrowse = true });
        return index;
    }

    /// <summary>Adds a runtime picked with "Browse…" (unless it's listed already), before "Browse…"; returns its index.</summary>
    public static int Add(ObservableCollection<JavaChoice> choices, JavaInstallation java)
    {
        var index = IndexOf(choices, java.Home);
        if (index > 0)
            return index;
        var at = choices.Count > 0 && choices[^1].IsBrowse ? choices.Count - 1 : choices.Count;
        choices.Insert(at, For(java));
        return at;
    }

    /// <summary>Asks for java.exe/javaw.exe and checks it runs; null when cancelled or not usable (after saying so).</summary>
    public static async Task<JavaInstallation?> PickAsync()
    {
        if (App.Window is not { } window)
            return null;
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(window.AppWindow.Id)
        {
            SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        if ((await picker.PickSingleFileAsync())?.Path is not { } path)
            return null;
        var java = await JavaManager.ProbeUserPathAsync(path);
        if (java is null)
            await Dialogs.ShowMessageAsync(Loc.Get("Java_NotJavaTitle"), Loc.Format("Java_NotJava", path));
        return java;
    }

    /// <summary>Index of the entry for <paramref name="path"/> (a Java home, its bin folder or java(w).exe); 0 when empty.</summary>
    public static int IndexOf(IList<JavaChoice> choices, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return 0;
        var home = HomeOf(path);
        for (var i = 1; i < choices.Count; i++)
        {
            if (choices[i].Path is { } p && string.Equals(HomeOf(p), home, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }

    /// <summary>The Java home a user path points at, as <see cref="JavaManager.ProbeUserPathAsync"/> reads it.</summary>
    private static string HomeOf(string path)
    {
        var p = path.Trim().Trim('"');
        if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            p = System.IO.Path.GetDirectoryName(p) ?? p;
        if (System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(p)).Equals("bin", StringComparison.OrdinalIgnoreCase))
            p = System.IO.Path.GetDirectoryName(System.IO.Path.TrimEndingDirectorySeparator(p)) ?? p;
        return Normalize(p);
    }

    private static string Normalize(string path)
    {
        try
        {
            return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }
}
