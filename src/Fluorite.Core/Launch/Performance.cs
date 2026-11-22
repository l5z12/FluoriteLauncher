namespace Fluorite.Core.Launch;

/// <summary>Which GPU Windows runs the game on (laptops with integrated and dedicated graphics).</summary>
public enum GpuPreference
{
    /// <summary>Leave Windows' choice (and the user's own setting) alone.</summary>
    Default,
    PowerSaving,
    HighPerformance,
}

/// <summary>Garbage collector tuning added before the user's own JVM arguments.</summary>
public enum JvmPreset
{
    /// <summary>Java's defaults.</summary>
    None,

    /// <summary>G1 with the official launcher's settings: short pauses, works on every Java version.</summary>
    Balanced,

    /// <summary>Generational ZGC on Java 21+ (near-pause-free, more memory); <see cref="Balanced"/> on older Java.</summary>
    LowLatency,
}

public static class JvmPresets
{
    /// <summary>The official launcher's default JVM arguments, minus the memory size.</summary>
    private static readonly string[] s_g1 =
    [
        "-XX:+UnlockExperimentalVMOptions",
        "-XX:+UseG1GC",
        "-XX:G1NewSizePercent=20",
        "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50",
        "-XX:G1HeapRegionSize=32M",
    ];

    /// <summary>
    /// Arguments for <paramref name="preset"/>; none when the user's arguments already pick a collector (two would
    /// stop Java from starting).
    /// </summary>
    public static IReadOnlyList<string> For(JvmPreset preset, int javaMajor, bool is32Bit, IEnumerable<string> userArguments)
    {
        if (preset == JvmPreset.None || userArguments.Any(SelectsCollector))
            return [];
        if (preset == JvmPreset.LowLatency && javaMajor >= 21 && !is32Bit)
            return javaMajor < 23 ? ["-XX:+UseZGC", "-XX:+ZGenerational"] : ["-XX:+UseZGC"]; // Generational is the only mode from 23.
        return s_g1;
    }

    private static bool SelectsCollector(string argument) =>
        argument.StartsWith("-XX:+Use", StringComparison.Ordinal) && argument.EndsWith("GC", StringComparison.Ordinal);
}

public static class MemoryAdvisor
{
    /// <summary>Physical memory (MB) as .NET sees it.</summary>
    public static long SystemMemoryMb => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);

    /// <summary>
    /// Maximum heap for an instance: 2 GB for vanilla (what the official launcher gives), 3 GB plus 16 MB a mod for
    /// modded ones, never more than 12 GB (bigger heaps only make collections slower), and always leaving Windows
    /// at least 2 GB or 30 % of the PC's memory.
    /// </summary>
    public static int Recommend(long systemMemoryMb, int modCount)
    {
        var want = modCount <= 0 ? 2048 : 3072 + modCount * 16;
        want = Math.Min(want, 12_288);
        var reserve = Math.Max(2048, (long)(systemMemoryMb * 0.3));
        var cap = Math.Max(1024, systemMemoryMb - reserve);
        var mb = Math.Clamp(want, 1024, cap);
        return (int)(mb / 256 * 256);
    }

    /// <summary>Mods in an instance's <c>mods</c> folder (enabled ones only).</summary>
    public static int CountMods(string gameDirectory)
    {
        var dir = Path.Combine(gameDirectory, "mods");
        try
        {
            return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*.jar").Count() : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
