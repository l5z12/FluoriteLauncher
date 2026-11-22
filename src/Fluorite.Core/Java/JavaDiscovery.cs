using Microsoft.Win32;

namespace Fluorite.Core.Java;

/// <summary>
/// Finds Java runtimes from JAVA_HOME, PATH, vendor registry keys, well-known install folders,
/// Mojang runtimes (classic and MSIX official launcher), other launchers and Fluorite's own runtime folder.
/// </summary>
public static class JavaDiscovery
{
    public static async Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(IEnumerable<string>? extraRuntimeRoots = null, CancellationToken ct = default)
    {
        var candidates = new List<(string Home, JavaSource Source)>();

        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            candidates.Add((javaHome, JavaSource.JavaHome));

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim().Trim('"');
            if (File.Exists(Path.Combine(trimmed, "java.exe")) && Path.GetFileName(trimmed).Equals("bin", StringComparison.OrdinalIgnoreCase))
                candidates.Add((Path.GetDirectoryName(trimmed)!, JavaSource.Path));
        }

        if (OperatingSystem.IsWindows())
        {
            foreach (var home in RegistryHomes())
                candidates.Add((home, JavaSource.Registry));
        }

        foreach (var folder in WellKnownFolders())
            AddChildren(candidates, folder, JavaSource.WellKnownFolder);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Mojang runtimes: runtime/<component>/<platform>/<component>/
        AddMojangRuntimes(candidates, Path.Combine(appData, ".minecraft", "runtime"));
        AddMojangRuntimes(candidates, Path.Combine(localAppData, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"));

        // Other launchers.
        AddChildren(candidates, Path.Combine(appData, "PrismLauncher", "java"), JavaSource.OtherLauncher);
        AddChildren(candidates, Path.Combine(appData, ".hmcl", "java"), JavaSource.OtherLauncher);
        AddChildren(candidates, Path.Combine(appData, "ModrinthApp", "meta", "java_versions"), JavaSource.OtherLauncher);
        AddChildren(candidates, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"), JavaSource.WellKnownFolder);

        foreach (var root in extraRuntimeRoots ?? [])
        {
            AddMojangRuntimes(candidates, root, JavaSource.Fluorite);
            AddChildren(candidates, root, JavaSource.Fluorite);
        }

        var unique = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c.Home))
            .Select(c => (Home: SafeFullPath(c.Home), c.Source))
            .Where(c => c.Home is not null)
            .GroupBy(c => c.Home!, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());

        var probes = unique.Select(c => JavaProbe.ProbeAsync(c.Home!, c.Source, ct));
        var results = await Task.WhenAll(probes).ConfigureAwait(false);
        return results.OfType<JavaInstallation>()
            .OrderByDescending(j => j.Major)
            .ThenBy(j => j.Home, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? SafeFullPath(string p)
    {
        try
        {
            return Path.GetFullPath(p.Trim().Trim('"').TrimEnd('\\', '/'));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static IEnumerable<string> WellKnownFolders()
    {
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf))
                continue;
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "AdoptOpenJDK", "Zulu", "Microsoft", "Amazon Corretto", "BellSoft", "Semeru", "IBM", "GraalVM", "Oracle" })
                yield return Path.Combine(pf, vendor);
        }
    }

    private static void AddChildren(List<(string, JavaSource)> list, string folder, JavaSource source)
    {
        if (!Directory.Exists(folder))
            return;
        try
        {
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                list.Add((child, source));
                // Some layouts nest one more level (e.g. jdk-17/jdk-17.0.8+7).
                foreach (var grandChild in Directory.EnumerateDirectories(child))
                {
                    if (File.Exists(Path.Combine(grandChild, "bin", "java.exe")))
                        list.Add((grandChild, source));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void AddMojangRuntimes(List<(string, JavaSource)> list, string runtimeRoot, JavaSource source = JavaSource.MinecraftRuntime)
    {
        if (!Directory.Exists(runtimeRoot))
            return;
        try
        {
            foreach (var component in Directory.EnumerateDirectories(runtimeRoot))
            foreach (var platform in Directory.EnumerateDirectories(component))
            {
                var home = Path.Combine(platform, Path.GetFileName(component));
                if (Directory.Exists(home))
                    list.Add((home, source));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> RegistryHomes()
    {
        var results = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    // Oracle-style: SOFTWARE\JavaSoft\<kind>\<version>\JavaHome
                    foreach (var kind in new[] { "Java Runtime Environment", "Java Development Kit", "JRE", "JDK" })
                        CollectValue(baseKey, $@"SOFTWARE\JavaSoft\{kind}", "JavaHome", results, depth: 1);

                    // MSI-style vendors: <vendor>\<kind>\<version>\hotspot\MSI\Path
                    foreach (var vendor in new[] { @"Eclipse Adoptium", @"Eclipse Foundation", @"AdoptOpenJDK", @"Microsoft", @"Semeru" })
                    foreach (var kind in new[] { "JDK", "JRE" })
                        CollectMsiPaths(baseKey, $@"SOFTWARE\{vendor}\{kind}", results);

                    CollectValue(baseKey, @"SOFTWARE\Azul Systems\Zulu", "InstallationPath", results, depth: 1);
                    CollectValue(baseKey, @"SOFTWARE\Amazon\Corretto", "InstallationPath", results, depth: 1);
                    CollectValue(baseKey, @"SOFTWARE\BellSoft\Liberica", "InstallationPath", results, depth: 1);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                }
            }
        }
        return results;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CollectValue(RegistryKey baseKey, string path, string valueName, List<string> results, int depth)
    {
        using var key = baseKey.OpenSubKey(path);
        if (key is null)
            return;
        if (depth == 0)
        {
            if (key.GetValue(valueName) is string s && s.Length > 0)
                results.Add(s);
            return;
        }
        foreach (var sub in key.GetSubKeyNames())
            CollectValue(key, sub, valueName, results, depth - 1);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CollectMsiPaths(RegistryKey baseKey, string path, List<string> results)
    {
        using var key = baseKey.OpenSubKey(path);
        if (key is null)
            return;
        foreach (var version in key.GetSubKeyNames())
        {
            foreach (var jvm in new[] { "hotspot", "openj9" })
            {
                using var msi = key.OpenSubKey($@"{version}\{jvm}\MSI");
                if (msi?.GetValue("Path") is string p && p.Length > 0)
                    results.Add(p);
            }
        }
    }
}
