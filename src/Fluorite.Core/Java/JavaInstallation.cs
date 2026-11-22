using Fluorite.Core.Platform;

namespace Fluorite.Core.Java;

public enum JavaSource
{
    Unknown,
    JavaHome,
    Path,
    Registry,
    WellKnownFolder,
    MinecraftRuntime,
    OtherLauncher,
    Fluorite,
    User,
}

/// <summary>A detected Java runtime. <see cref="Home"/> is the folder containing <c>bin\</c>.</summary>
public sealed record JavaInstallation(string Home, string Version, int Major, CpuArch Arch, string? Vendor, JavaSource Source)
{
    public string JavaExe => System.IO.Path.Combine(Home, "bin", "java.exe");
    public string JavawExe => System.IO.Path.Combine(Home, "bin", "javaw.exe");

    /// <summary>The executable to launch the game with (javaw avoids a console window).</summary>
    public string LaunchExecutable => File.Exists(JavawExe) ? JavawExe : JavaExe;

    public override string ToString() => $"Java {Version} ({Arch}, {Vendor ?? "unknown vendor"}) at {Home}";

    /// <summary>Parses Java version strings: <c>1.8.0_381</c> → 8, <c>17.0.8</c> → 17, <c>25</c> → 25.</summary>
    public static int ParseMajor(string version)
    {
        var v = version.Trim().Trim('"');
        if (v.StartsWith("1.", StringComparison.Ordinal) && v.Length > 2)
            v = v[2..];
        var end = 0;
        while (end < v.Length && char.IsDigit(v[end]))
            end++;
        return end > 0 && int.TryParse(v.AsSpan(0, end), out var major) ? major : 0;
    }
}
