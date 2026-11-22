using Fluorite.Core.Platform;
using Fluorite.Core.Versions;

namespace Fluorite.Core.Tests;

public class RuleEvaluatorTests
{
    private static RuleContext Win(CpuArch arch = CpuArch.X64, LaunchFeatures? f = null) => new("windows", "10.0", arch, f ?? new LaunchFeatures());

    [Fact]
    public void NoRules_IsAllowed() => Assert.True(RuleEvaluator.IsAllowed(null, Win()));

    [Fact]
    public void AllowThenDisallowOsx_AllowsWindows()
    {
        List<Rule> rules = [new() { Action = "allow" }, new() { Action = "disallow", Os = new OsRule { Name = "osx" } }];
        Assert.True(RuleEvaluator.IsAllowed(rules, Win()));
        Assert.False(RuleEvaluator.IsAllowed(rules, new RuleContext("osx", "14.0", CpuArch.Arm64, new LaunchFeatures())));
    }

    [Fact]
    public void AllowOnlyOsx_DisallowsWindows()
    {
        List<Rule> rules = [new() { Action = "allow", Os = new OsRule { Name = "osx" } }];
        Assert.False(RuleEvaluator.IsAllowed(rules, Win()));
    }

    [Theory]
    [InlineData("10.0", true)]
    [InlineData("6.1", false)]
    public void OsVersionRegex(string osVersion, bool expected)
    {
        List<Rule> rules = [new() { Action = "allow", Os = new OsRule { Name = "windows", Version = @"^10\." } }];
        Assert.Equal(expected, RuleEvaluator.IsAllowed(rules, new RuleContext("windows", osVersion, CpuArch.X64, new LaunchFeatures())));
    }

    [Theory]
    [InlineData(CpuArch.X86, true)]
    [InlineData(CpuArch.X64, false)]
    [InlineData(CpuArch.Arm64, false)]
    public void ArchX86_MeansThirtyTwoBitOnly(CpuArch arch, bool expected)
    {
        List<Rule> rules = [new() { Action = "allow", Os = new OsRule { Arch = "x86" } }];
        Assert.Equal(expected, RuleEvaluator.IsAllowed(rules, Win(arch)));
    }

    [Fact]
    public void Features_MustAllMatch()
    {
        List<Rule> rules = [new() { Action = "allow", Features = new() { ["has_custom_resolution"] = true } }];
        Assert.False(RuleEvaluator.IsAllowed(rules, Win()));
        Assert.True(RuleEvaluator.IsAllowed(rules, Win(f: new LaunchFeatures { HasCustomResolution = true })));
    }

    [Fact]
    public void UnknownFeature_TreatedAsFalse()
    {
        List<Rule> rules = [new() { Action = "allow", Features = new() { ["is_some_future_feature"] = true } }];
        Assert.False(RuleEvaluator.IsAllowed(rules, Win()));
    }

    [Fact]
    public void InvalidRegex_DoesNotThrow()
    {
        List<Rule> rules = [new() { Action = "allow", Os = new OsRule { Version = "([" } }];
        Assert.False(RuleEvaluator.IsAllowed(rules, Win()));
    }
}
