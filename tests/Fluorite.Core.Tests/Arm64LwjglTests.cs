using Fluorite.Core.Libraries;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class Arm64LwjglTests
{
    private static Library Lib(string name) => new() { Name = name };

    [Fact]
    public void Lwjgl32_IsSwappedForTheModulesInUse()
    {
        // 1.16.5-style: 3.2.2 for Windows/Linux, 3.2.1 for macOS, natives through the legacy map.
        IReadOnlyList<Library> libs =
        [
            Lib("com.mojang:patchy:1.3.9"),
            Lib("org.lwjgl:lwjgl:3.2.2"),
            new Library { Name = "org.lwjgl:lwjgl:3.2.2", Natives = new() { ["windows"] = "natives-windows" } },
            Lib("org.lwjgl:lwjgl-glfw:3.2.2"),
            Lib("org.lwjgl:lwjgl-glfw:3.2.1"),
            Lib("com.mojang:brigadier:1.0.17"),
        ];
        Assert.Equal(Arm64Support.SwappedLwjgl, Arm64Lwjgl.Classify(libs));

        var swapped = Arm64Lwjgl.Apply(libs).Select(l => l.Name).ToList();
        Assert.DoesNotContain(swapped, n => n.Contains(":3.2.", StringComparison.Ordinal));
        Assert.Equal(
            ["com.mojang:patchy:1.3.9", "org.lwjgl:lwjgl:3.3.1", "org.lwjgl:lwjgl-glfw:3.3.1",
             "org.lwjgl:lwjgl:3.3.1:natives-windows-arm64", "org.lwjgl:lwjgl-glfw:3.3.1:natives-windows-arm64", "com.mojang:brigadier:1.0.17"],
            swapped);
        var native = Arm64Lwjgl.Apply(libs).Single(l => l.Name == "org.lwjgl:lwjgl-glfw:3.3.1:natives-windows-arm64");
        Assert.Equal("https://libraries.minecraft.net/org/lwjgl/lwjgl-glfw/3.3.1/lwjgl-glfw-3.3.1-natives-windows-arm64.jar", native.Downloads!.Artifact!.Url);
        Assert.Equal(Arm64Support.Native, Arm64Lwjgl.Classify(Arm64Lwjgl.Apply(libs)));
    }

    [Fact]
    public void Lwjgl2_HasNoArm64Build()
    {
        IReadOnlyList<Library> libs = [Lib("org.lwjgl.lwjgl:lwjgl:2.9.4-nightly-20150209"), Lib("org.lwjgl.lwjgl:lwjgl_util:2.9.4-nightly-20150209")];
        Assert.Equal(Arm64Support.Emulated, Arm64Lwjgl.Classify(libs));
        Assert.Same(libs, Arm64Lwjgl.Apply(libs));
    }

    [Fact]
    public void Arm64Natives_RunAsTheyAre()
    {
        IReadOnlyList<Library> libs = [Lib("org.lwjgl:lwjgl:3.3.3"), Lib("org.lwjgl:lwjgl:3.3.3:natives-windows-arm64")];
        Assert.Equal(Arm64Support.Native, Arm64Lwjgl.Classify(libs));
        Assert.Same(libs, Arm64Lwjgl.Apply(libs));
    }
}
