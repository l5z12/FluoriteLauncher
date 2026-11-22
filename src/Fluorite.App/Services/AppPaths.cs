namespace Fluorite.App.Services;

/// <summary>
/// Resolves the data root. The default lives under %LOCALAPPDATA% (real path: write virtualization is disabled in
/// the package manifest). A user-chosen root is recorded in a small pointer file at the default location so it works
/// identically packaged and unpackaged.
/// </summary>
public static class AppPaths
{
    public static string DefaultRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FluoriteLauncher");

    private static string PointerFile => Path.Combine(DefaultRoot, "data-root.txt");

    public static string DataRoot { get; } = Resolve();

    public static string LogsDir => Path.Combine(DataRoot, "logs");

    /// <summary>
    /// <c>--fresh</c>: a new, empty folder per run, so the app starts like a first install. Temp files, which Windows
    /// clears over time; the real data root isn't read or written.
    /// </summary>
    private static string FreshRoot() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "FluoriteFresh", DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture))).FullName;

    private static string Resolve()
    {
        // --fresh with --root: a fresh run continuing in its folder after a restart (a language change).
        if (StartupFlags.Current.Fresh)
            return StartupFlags.Current.Root is { } continued ? Path.GetFullPath(continued) : FreshRoot();
        if (StartupFlags.Current.Root is { } root) // --root or FLUORITE_ROOT
            return Path.GetFullPath(root);
        try
        {
            if (File.Exists(PointerFile) && File.ReadAllText(PointerFile).Trim() is { Length: > 0 } custom && Directory.Exists(custom))
                return custom;
        }
        catch (IOException)
        {
        }
        return DefaultRoot;
    }

    /// <summary>Persists a new data root; takes effect after restart.</summary>
    public static void SetDataRoot(string path)
    {
        Directory.CreateDirectory(DefaultRoot);
        if (string.Equals(Path.GetFullPath(path), DefaultRoot, StringComparison.OrdinalIgnoreCase))
            File.Delete(PointerFile);
        else
            File.WriteAllText(PointerFile, Path.GetFullPath(path));
    }
}
