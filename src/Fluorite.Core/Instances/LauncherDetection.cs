namespace Fluorite.Core.Instances;

public enum OtherLauncher
{
    Official,
    PrismLauncher,
    MultiMC,
    Hmcl,
    Pcl,
    CurseForge,
    ModrinthApp,
    GDLauncher,
    ATLauncher,
    FtbApp,
}

/// <param name="Layout">How the folder can be used in place (null = import only).</param>
public sealed record DetectedLauncher(OtherLauncher Launcher, string Path, LayoutKind? Layout, int InstanceCount);

/// <summary>Finds data folders of other launchers on this PC (well-known locations only; no disk-wide scan).</summary>
public static class LauncherDetection
{
    public static IReadOnlyList<DetectedLauncher> Detect()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var results = new List<DetectedLauncher>();

        // Official launcher (classic and Microsoft Store builds both use %APPDATA%\.minecraft).
        var dotMinecraft = Path.Combine(appData, ".minecraft");
        if (File.Exists(Path.Combine(dotMinecraft, "launcher_profiles.json")) || Directory.Exists(Path.Combine(dotMinecraft, "versions")))
        {
            var versions = CountDirs(Path.Combine(dotMinecraft, "versions"));
            // A .minecraft with per-version game dirs (versions/<id>/saves etc.) was set up by HMCL/PCL "version isolation".
            var isolated = Directory.Exists(Path.Combine(dotMinecraft, "versions"))
                && Directory.EnumerateDirectories(Path.Combine(dotMinecraft, "versions")).Any(d => Directory.Exists(Path.Combine(d, "saves")) || Directory.Exists(Path.Combine(d, "mods")));
            results.Add(new DetectedLauncher(isolated ? OtherLauncher.Hmcl : OtherLauncher.Official, dotMinecraft, isolated ? LayoutKind.Isolated : LayoutKind.Official, versions));
        }

        var prism = Path.Combine(appData, "PrismLauncher");
        if (Directory.Exists(prism))
            results.Add(new DetectedLauncher(OtherLauncher.PrismLauncher, prism, LayoutKind.MultiMC, CountInstances(new MultiMCLayout(prism, Dummy(prism)).InstancesDirectory)));
        AddInstances(results, OtherLauncher.Hmcl, Path.Combine(appData, ".hmcl"), Path.Combine(".minecraft", "versions"), LayoutKind.Isolated);
        AddInstances(results, OtherLauncher.CurseForge, Path.Combine(profile, "curseforge", "minecraft"), "Instances", null);
        AddInstances(results, OtherLauncher.ModrinthApp, Path.Combine(appData, "ModrinthApp"), "profiles", null);
        AddInstances(results, OtherLauncher.ModrinthApp, Path.Combine(appData, "com.modrinth.theseus"), "profiles", null);
        AddInstances(results, OtherLauncher.GDLauncher, Path.Combine(appData, "gdlauncher_carbon", "data"), "instances", null);
        AddInstances(results, OtherLauncher.GDLauncher, Path.Combine(appData, "gdlauncher_next"), "instances", null);
        AddInstances(results, OtherLauncher.ATLauncher, Path.Combine(appData, "ATLauncher"), "instances", null);
        AddInstances(results, OtherLauncher.FtbApp, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ".ftba"), "instances", null);

        // The official launcher folder: HMCL/PCL-style isolation means its versions are instances; otherwise its profiles are.
        if (results.FirstOrDefault(r => r.Path == dotMinecraft) is { } dm && InstanceRegistry.IsIsolated(dotMinecraft) && dm.Layout != LayoutKind.Isolated)
            results[results.IndexOf(dm)] = dm with { Launcher = OtherLauncher.Hmcl, Layout = LayoutKind.Isolated };
        return results;
    }

    private static ExternalLayoutContext Dummy(string root) => new("detect", OtherLauncher.PrismLauncher, root, new GameDirectories(root), false);

    private static int CountInstances(string instancesDir)
    {
        try
        {
            return Directory.Exists(instancesDir) ? Directory.EnumerateDirectories(instancesDir).Count(d => File.Exists(Path.Combine(d, "instance.cfg"))) : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static void AddInstances(List<DetectedLauncher> results, OtherLauncher launcher, string root, string instancesSubdir, LayoutKind? layout)
    {
        var dir = Path.Combine(root, instancesSubdir);
        if (Directory.Exists(dir))
            results.Add(new DetectedLauncher(launcher, root, layout, CountDirs(dir)));
    }

    private static int CountDirs(string path)
    {
        try
        {
            return Directory.Exists(path) ? Directory.EnumerateDirectories(path).Count() : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
