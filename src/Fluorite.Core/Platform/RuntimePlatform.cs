using System.Runtime.InteropServices;

namespace Fluorite.Core.Platform;

/// <summary>CPU architecture as relevant to Minecraft natives and Java runtimes.</summary>
public enum CpuArch
{
    Unknown,
    X86,
    X64,
    Arm64,
}

public static class CpuArchExtensions
{
    /// <summary>Value substituted for <c>${arch}</c> in legacy native classifiers.</summary>
    public static string LegacyBits(this CpuArch arch) => arch == CpuArch.X86 ? "32" : "64";

    /// <summary>Mojang java-runtime platform key, e.g. <c>windows-x64</c>.</summary>
    public static string MojangRuntimePlatform(this CpuArch arch) => arch switch
    {
        CpuArch.X86 => "windows-x86",
        CpuArch.Arm64 => "windows-arm64",
        _ => "windows-x64",
    };

    public static CpuArch Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "x86" or "i386" or "i486" or "i586" or "i686" => CpuArch.X86,
        "x64" or "x86_64" or "amd64" => CpuArch.X64,
        "arm64" or "aarch64" => CpuArch.Arm64,
        _ => CpuArch.Unknown,
    };
}

/// <summary>Facts about the host OS used by rule evaluation.</summary>
public sealed record RuntimePlatform(string OsName, string OsVersion, CpuArch OsArch)
{
    public static RuntimePlatform Current { get; } = Detect();

    private static RuntimePlatform Detect()
    {
        var name = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "osx"
            : "linux";

        // Mojang rules match against Java's os.version, which on Windows is "major.minor" (e.g. "10.0").
        var v = Environment.OSVersion.Version;
        var version = $"{v.Major}.{v.Minor}";

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X86 => CpuArch.X86,
            Architecture.X64 => CpuArch.X64,
            Architecture.Arm64 => CpuArch.Arm64,
            _ => CpuArch.Unknown,
        };
        return new RuntimePlatform(name, version, arch);
    }
}
