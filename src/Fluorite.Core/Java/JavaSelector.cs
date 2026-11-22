using Fluorite.Core.Platform;

namespace Fluorite.Core.Java;

/// <param name="Major">Required major version (from <c>javaVersion.majorVersion</c>, 8 when absent).</param>
/// <param name="Component">Mojang runtime component to install when nothing suitable exists.</param>
/// <param name="PreferredArch">Arch whose natives the version provides (ARM64 only when arm64 natives exist).</param>
/// <param name="StrictMajor">Require exactly <paramref name="Major"/> (legacy LaunchWrapper/Forge break on newer Java).</param>
public sealed record JavaRequirement(int Major, string? Component, CpuArch PreferredArch, bool StrictMajor)
{
    /// <summary>
    /// Builds a requirement from version metadata. Versions needing Java ≤ 8 are strict because LaunchWrapper,
    /// old Forge and pre-1.13 LWJGL2 fail on Java 9+; newer requirements accept a newer Java but prefer exact.
    /// </summary>
    public static JavaRequirement For(int? majorFromJson, string? component, bool versionHasArm64Natives, CpuArch osArch)
    {
        var major = majorFromJson is > 0 ? majorFromJson.Value : 8;
        var arch = osArch switch
        {
            CpuArch.Arm64 => versionHasArm64Natives ? CpuArch.Arm64 : CpuArch.X64,
            CpuArch.Unknown => CpuArch.X64,
            _ => osArch,
        };
        return new JavaRequirement(major, component, arch, StrictMajor: major <= 8);
    }
}

public static class JavaSelector
{
    /// <summary>Picks the best installed runtime or null when none qualifies.</summary>
    public static JavaInstallation? Select(IEnumerable<JavaInstallation> installed, JavaRequirement req)
    {
        return installed
            .Select(j => (Java: j, Score: Score(j, req)))
            .Where(t => t.Score > 0)
            .OrderByDescending(t => t.Score)
            .ThenBy(t => t.Java.Major)
            .Select(t => t.Java)
            .FirstOrDefault();
    }

    public static int Score(JavaInstallation j, JavaRequirement req)
    {
        int score;
        if (j.Major == req.Major)
            score = 1000;
        else if (!req.StrictMajor && j.Major > req.Major)
            score = 500 - (j.Major - req.Major); // Closest newer wins.
        else
            return 0;

        if (j.Arch == req.PreferredArch)
            score += 100;
        else if (req.PreferredArch == CpuArch.Arm64 && j.Arch == CpuArch.X64)
            score += 40; // Emulated x64 works on ARM64 Windows.
        else if (req.PreferredArch == CpuArch.X64 && j.Arch == CpuArch.Arm64)
            return 0; // Natives would mismatch.
        else if (j.Arch == CpuArch.X86)
            score += 10; // 32-bit works but caps memory.

        // Prefer Mojang/Fluorite-managed runtimes: they're the ones Mojang tests against.
        if (j.Source is JavaSource.Fluorite or JavaSource.MinecraftRuntime)
            score += 20;
        return score;
    }
}
