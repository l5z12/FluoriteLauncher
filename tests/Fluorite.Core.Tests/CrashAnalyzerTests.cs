using Fluorite.Core.Diagnostics;
using Fluorite.Core.Launch;
using Fluorite.Core.Platform;

namespace Fluorite.Core.Tests;

public class CrashAnalyzerTests
{
    private static CrashAnalysis Analyze(string log, int exitCode = 1, CpuArch arch = CpuArch.X64, int? java = null, int? required = null) =>
        CrashAnalyzer.Analyze(new CrashContext { Log = log, ExitCode = exitCode, JavaArch = arch, JavaMajor = java, RequiredJavaMajor = required });

    [Fact]
    public void JavaTooOld_FromClassVersion()
    {
        var a = Analyze("Exception in thread \"main\" java.lang.UnsupportedClassVersionError: net/minecraft/client/main/Main has been compiled by a more recent version of the Java Runtime (class file version 65.0), this version of the Java Runtime only recognizes class file versions up to 52.0");
        var f = Assert.Single(a.Findings);
        Assert.Equal("JavaTooOld", f.Id);
        Assert.Equal("21", Assert.Single(f.Args));
    }

    [Fact]
    public void FabricResolution_ShowsTheLoadersExplanation()
    {
        const string log = """
            [12:00:01] [main/ERROR] Incompatible mods found!
            net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible with the game or each other!
            A potential solution has been determined:
            	 - Install fabric-api, any version.
            Unmet dependency listing:
            	 - Mod 'Sodium Extra' (sodium-extra) 0.5.1 requires any version of fabric-api, which is missing!
            	at net.fabricmc.loader.impl.FormattedException.ofLocalized(FormattedException.java:51)
            [12:00:02] [main/INFO] Stopping
            """;
        var f = Assert.Single(Analyze(log).Findings);
        Assert.Equal("FabricIncompatibleMods", f.Id);
        Assert.Contains("Install fabric-api, any version.", f.Details);
        Assert.Contains("requires any version of fabric-api", f.Details);
        Assert.DoesNotContain("FormattedException.ofLocalized", f.Details);
        Assert.DoesNotContain("Stopping", f.Details);
    }

    [Fact]
    public void Mixin_NamesEveryFailingMod()
    {
        const string log = """
            [12:00:01] [main/ERROR] Mixin apply for mod sodium failed sodium.mixins.json:core.MinecraftClientMixin from mod sodium -> net.minecraft.class_310
            [12:00:01] [main/ERROR] Mixin apply failed iris.mixins.json:MixinGameRenderer -> net.minecraft.class_757
            Caused by: java.lang.NoSuchMethodError: 'void net.minecraft.class_310.method_1234()'
            """;
        var findings = Analyze(log).Findings;
        var mixin = Assert.Single(findings);
        Assert.Equal("MixinFailed", mixin.Id);
        Assert.Equal("sodium, iris", Assert.Single(mixin.Args)); // CodeMissing is hidden: the mixin failure explains it.
    }

    [Fact]
    public void GpuDriverCrash_NamesTheDriver()
    {
        const string log = """
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb1c2d3e4f, pid=1234, tid=5678
            #
            # Problematic frame:
            # C  [ig9icd64.dll+0x123e4f]
            """;
        var f = Assert.Single(Analyze(log, exitCode: unchecked((int)0xC0000005)).Findings);
        Assert.Equal("GpuDriverCrash", f.Id);
        Assert.Equal("ig9icd64.dll", Assert.Single(f.Args));
    }

    [Fact]
    public void ThirtyTwoBitJava_ExplainsHeapFailure()
    {
        var findings = Analyze("Error occurred during initialization of VM\nCould not reserve enough space for 4194304KB object heap", arch: CpuArch.X86).Findings;
        Assert.Equal("Java32Bit", Assert.Single(findings).Id);
    }

    [Fact]
    public void JavaMismatch_FromContextWhenTheLogSaysNothing()
    {
        var f = Assert.Single(Analyze("", java: 8, required: 17).Findings);
        Assert.Equal("JavaTooOld", f.Id);
        Assert.Equal("17", Assert.Single(f.Args));
    }

    [Fact]
    public void Redactor_RemovesTokensAndUserName()
    {
        var text = LogRedactor.Redact(
            "[main/INFO] Launching with --accessToken eyJhbGciOiJIUzI1NiJ9.abc --username Steve\nC:\\Users\\alice\\AppData\\Roaming\\.minecraft\nsecret-token-123",
            ["secret-token-123"]);
        Assert.DoesNotContain("eyJhbGci", text);
        Assert.DoesNotContain("alice", text);
        Assert.DoesNotContain("secret-token-123", text);
        Assert.Contains("--username Steve", text);
        Assert.Contains(@"C:\Users\<user>\AppData", text);
    }

    [Theory]
    [InlineData(16384, 0, 2048)]
    [InlineData(16384, 100, 4608)]
    [InlineData(8192, 300, 5632)]
    [InlineData(4096, 50, 2048)]
    public void MemoryAdvisor_LeavesRoomForWindows(long system, int mods, int expected) =>
        Assert.Equal(expected, MemoryAdvisor.Recommend(system, mods));

    [Fact]
    public void JvmPreset_StepsAsideForTheUsersCollector()
    {
        Assert.Empty(JvmPresets.For(JvmPreset.Balanced, 21, false, ["-XX:+UseShenandoahGC"]));
        Assert.Contains("-XX:+UseZGC", JvmPresets.For(JvmPreset.LowLatency, 21, false, []));
        Assert.Contains("-XX:+UseG1GC", JvmPresets.For(JvmPreset.LowLatency, 17, false, []));
    }
}
