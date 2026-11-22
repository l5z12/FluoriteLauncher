using Fluorite.Core.Versions;

namespace Fluorite.Core.Libraries;

/// <summary>How a Minecraft version runs on a Windows ARM64 PC.</summary>
public enum Arm64Support
{
    /// <summary>The version ships Windows ARM64 natives (Minecraft 1.19 and newer): ARM64 Java, no emulation.</summary>
    Native,

    /// <summary>
    /// LWJGL 3 without ARM64 natives (Minecraft 1.13–1.18): the LWJGL that 1.19 ships (3.3.1, which has them) is
    /// swapped in so ARM64 Java can run it.
    /// </summary>
    SwappedLwjgl,

    /// <summary>LWJGL 2 (Minecraft 1.12.2 and older): there's no ARM64 build, so x64 Java runs under emulation.</summary>
    Emulated,
}

/// <summary>What to do on an ARM64 PC (an instance setting).</summary>
public enum Arm64Mode
{
    /// <summary>Native where possible (swapping LWJGL if needed), emulation otherwise.</summary>
    Automatic,

    /// <summary>Always x64 Java under emulation: the way every version ran before, for when native misbehaves.</summary>
    Emulate,
}

/// <summary>Swaps a 1.13–1.18 version's LWJGL 3 for Mojang's 3.3.1 (from 1.19), which has Windows ARM64 natives.</summary>
public static class Arm64Lwjgl
{
    public const string Version = "3.3.1";

    /// <summary>The jars and ARM64 natives from Mojang's 1.19 version JSON (libraries.minecraft.net, so mirrors serve them too).</summary>
    private static readonly (string Name, string Sha1, long Size)[] s_libraries =
    [
        ("org.lwjgl:lwjgl:3.3.1", "ae58664f88e18a9bb2c77b063833ca7aaec484cb", 724243),
        ("org.lwjgl:lwjgl-jemalloc:3.3.1", "a817bcf213db49f710603677457567c37d53e103", 36601),
        ("org.lwjgl:lwjgl-openal:3.3.1", "2623a6b8ae1dfcd880738656a9f0243d2e6840bd", 88237),
        ("org.lwjgl:lwjgl-opengl:3.3.1", "831a5533a21a5f4f81bbc51bb13e9899319b5411", 921563),
        ("org.lwjgl:lwjgl-glfw:3.3.1", "cbac1b8d30cb4795149c1ef540f912671a8616d0", 128801),
        ("org.lwjgl:lwjgl-stb:3.3.1", "b119297cf8ed01f247abe8685857f8e7fcf5980f", 112380),
        ("org.lwjgl:lwjgl-tinyfd:3.3.1", "0ff1914111ef2e3e0110ef2dabc8d8cdaad82347", 6767),
        ("org.lwjgl:lwjgl:3.3.1:natives-windows-arm64", "0f46cadcf95675908fd3a550d63d9d709cb68998", 130064),
        ("org.lwjgl:lwjgl-jemalloc:3.3.1:natives-windows-arm64", "cae85c4edb219c88b6a0c26a87955ad98dc9519d", 114250),
        ("org.lwjgl:lwjgl-openal:3.3.1:natives-windows-arm64", "40d65f1a7368a2aa47336f9cb69f5a190cf9975a", 505234),
        ("org.lwjgl:lwjgl-opengl:3.3.1:natives-windows-arm64", "527d78f1e9056aff3ed02ce93019c73c5e8f1721", 82445),
        ("org.lwjgl:lwjgl-glfw:3.3.1:natives-windows-arm64", "beda65ee503443e60aa196d58ed31f8d001dc22a", 123808),
        ("org.lwjgl:lwjgl-stb:3.3.1:natives-windows-arm64", "fde63cdd2605c00636721a6c8b961e41d1f6b247", 216848),
        ("org.lwjgl:lwjgl-tinyfd:3.3.1:natives-windows-arm64", "83a5e780df610829ff3a737822b4f931cffecd91", 109139),
    ];

    private static readonly HashSet<string> s_modules = s_libraries.Select(l => l.Name.Split(':')[1]).ToHashSet(StringComparer.Ordinal);

    public static bool HasArm64Natives(IEnumerable<Library> libraries) =>
        libraries.Any(l => l.Name.Contains("natives-windows-arm64", StringComparison.OrdinalIgnoreCase));

    public static Arm64Support Classify(IEnumerable<Library> libraries)
    {
        var list = libraries as IReadOnlyCollection<Library> ?? libraries.ToList();
        if (HasArm64Natives(list))
            return Arm64Support.Native;
        return Lwjgl3Modules(list) is { } modules && modules.IsSubsetOf(s_modules) ? Arm64Support.SwappedLwjgl : Arm64Support.Emulated;
    }

    /// <summary>
    /// The libraries with every <c>org.lwjgl</c> 3.x entry replaced by 3.3.1 and its ARM64 natives, for the modules the
    /// version used, at the place the first one was (classpath order). Unchanged when there's nothing to swap.
    /// </summary>
    public static IReadOnlyList<Library> Apply(IReadOnlyList<Library> libraries)
    {
        if (Classify(libraries) != Arm64Support.SwappedLwjgl)
            return libraries;
        var modules = Lwjgl3Modules(libraries)!;
        var result = new List<Library>(libraries.Count);
        var inserted = false;
        foreach (var lib in libraries)
        {
            if (!IsLwjgl3(lib))
            {
                result.Add(lib);
                continue;
            }
            if (inserted)
                continue;
            inserted = true;
            foreach (var (name, sha1, size) in s_libraries)
            {
                if (modules.Contains(name.Split(':')[1]))
                    result.Add(Create(name, sha1, size));
            }
        }
        return result;
    }

    private static bool IsLwjgl3(Library lib)
    {
        var parts = lib.Name.Split(':');
        return parts.Length >= 3 && parts[0] == "org.lwjgl" && parts[2].StartsWith("3.", StringComparison.Ordinal);
    }

    /// <summary>LWJGL 3 modules in use (<c>lwjgl</c>, <c>lwjgl-glfw</c>…); null for LWJGL 2 (group <c>org.lwjgl.lwjgl</c>) or none.</summary>
    private static HashSet<string>? Lwjgl3Modules(IEnumerable<Library> libraries)
    {
        HashSet<string>? modules = null;
        foreach (var lib in libraries)
        {
            if (IsLwjgl3(lib))
                (modules ??= new HashSet<string>(StringComparer.Ordinal)).Add(lib.Name.Split(':')[1]);
        }
        return modules;
    }

    private static Library Create(string name, string sha1, long size)
    {
        var parts = name.Split(':');
        var file = parts.Length > 3 ? $"{parts[1]}-{parts[2]}-{parts[3]}.jar" : $"{parts[1]}-{parts[2]}.jar";
        var path = $"org/lwjgl/{parts[1]}/{parts[2]}/{file}";
        return new Library
        {
            Name = name,
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadArtifact { Path = path, Sha1 = sha1, Size = size, Url = "https://libraries.minecraft.net/" + path },
            },
        };
    }
}
