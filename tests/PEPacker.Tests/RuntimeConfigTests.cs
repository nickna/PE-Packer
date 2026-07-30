using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Guards the framework version and roll-forward policy written into a bundle.
/// </summary>
/// <remarks>
/// Both bundlers previously pinned <c>Major.Minor.Build</c> from
/// <see cref="Environment.Version"/> with no roll-forward policy, so a bundle produced
/// on a machine with patch 10.0.9 was rejected by a target that only had 10.0.7 — the
/// host rolls forward, never backward. Under Native AOT the pinned value came from the
/// ILCompiler runtime pack the tool was built against, making it a build-machine
/// artifact stamped into every bundle its users produced. Nothing in the suite read the
/// generated manifest back, so none of that was visible.
/// </remarks>
public class RuntimeConfigTests
{
    [Fact]
    public void Generate_PinsPatchToZero_NotTheRunningPatch()
    {
        var json = RuntimeConfig.Generate(new Version(10, 0, 9));

        Assert.Contains("\"version\": \"10.0.0\"", json);
        Assert.DoesNotContain("10.0.9", json);
    }

    /// <summary>
    /// The running framework's patch level must not leak in, since under Native AOT it is
    /// the ILCompiler pack version rather than anything installed on a target.
    /// </summary>
    [Fact]
    public void Generate_DefaultVersion_DoesNotLeakRunningPatch()
    {
        var running = Environment.Version;

        var json = RuntimeConfig.Generate();

        Assert.Contains($"\"version\": \"{running.Major}.{running.Minor}.0\"", json);
        if (running.Build > 0)
        {
            Assert.DoesNotContain($"{running.Major}.{running.Minor}.{running.Build}", json);
        }
    }

    [Fact]
    public void Generate_EmitsRollForwardPolicy()
    {
        Assert.Contains("\"rollForward\": \"latestMinor\"", RuntimeConfig.Generate());
    }

    /// <summary>
    /// Every policy must round-trip to the camelCase spelling the host parses. A single
    /// fact rather than a theory because <c>RollForwardPolicy</c> is internal and cannot
    /// appear in a public test signature.
    /// </summary>
    [Fact]
    public void Generate_SpellsEveryPolicyTheWayTheHostExpects()
    {
        var expected = new (RollForwardPolicy Policy, string Spelling)[]
        {
            (RollForwardPolicy.LatestPatch, "latestPatch"),
            (RollForwardPolicy.Minor, "minor"),
            (RollForwardPolicy.LatestMinor, "latestMinor"),
            (RollForwardPolicy.Major, "major"),
            (RollForwardPolicy.LatestMajor, "latestMajor"),
            (RollForwardPolicy.Disable, "disable"),
        };

        // Fails if a policy is added without a spelling, rather than silently untested.
        Assert.Equal(Enum.GetValues<RollForwardPolicy>().Length, expected.Length);

        foreach (var (policy, spelling) in expected)
        {
            Assert.Contains($"\"rollForward\": \"{spelling}\"", RuntimeConfig.Generate(rollForward: policy));
        }
    }

    [Fact]
    public void Generate_DerivesTfmFromTheRequestedVersion()
    {
        var json = RuntimeConfig.Generate(new Version(9, 3, 7));

        Assert.Contains("\"tfm\": \"net9.3\"", json);
        Assert.Contains("\"version\": \"9.3.0\"", json);
    }

    /// <summary>
    /// <c>new Version(10, 0)</c> leaves <see cref="Version.Build"/> at -1, which must not
    /// reach the output.
    /// </summary>
    [Fact]
    public void Generate_HandlesVersionWithoutBuildComponent()
    {
        var json = RuntimeConfig.Generate(new Version(10, 0));

        Assert.Contains("\"version\": \"10.0.0\"", json);
        Assert.DoesNotContain("-1", json);
    }

    [Fact]
    public void Generate_ProducesParseableJsonWithTheExpectedShape()
    {
        using var doc = System.Text.Json.JsonDocument.Parse(RuntimeConfig.Generate(new Version(10, 0, 9)));

        var options = doc.RootElement.GetProperty("runtimeOptions");
        Assert.Equal("net10.0", options.GetProperty("tfm").GetString());
        Assert.Equal("latestMinor", options.GetProperty("rollForward").GetString());

        var framework = options.GetProperty("framework");
        Assert.Equal("Microsoft.NETCore.App", framework.GetProperty("name").GetString());
        Assert.Equal("10.0.0", framework.GetProperty("version").GetString());
    }
}
