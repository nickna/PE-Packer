using System.Runtime.InteropServices;
using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers the bundling paths that used to guess or race rather than fail: runtime-identifier
/// inference, the commit of a finished bundle onto its destination, and input validation.
/// </summary>
public class BundlerHardeningTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("pepacker_hardening_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("win", Architecture.X86, "win-x86")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("osx", Architecture.Arm, "osx-arm")]
    public void KnownPlatforms_InferTheExpectedRid(string os, Architecture arch, string expected)
    {
        Assert.Equal(expected, ManualBundler.BuildRuntimeIdentifier(os, arch));
    }

    /// <summary>
    /// An unrecognised OS used to produce <c>win-{arch}</c>, which is a specific wrong answer: the
    /// inferred RID picks the apphost template, so guessing produces an executable for a machine
    /// the caller is not on.
    /// </summary>
    [Fact]
    public void UnknownOperatingSystem_FailsClosed()
    {
        var ex = Assert.Throws<PEPackerException>(() =>
            ManualBundler.BuildRuntimeIdentifier(null, Architecture.X64));

        Assert.Contains("RuntimeIdentifier", ex.Message);
    }

    /// <summary>
    /// An unrecognised architecture used to produce <c>x64</c>, with the same consequence.
    /// </summary>
    [Fact]
    public void UnknownArchitecture_FailsClosed()
    {
        var ex = Assert.Throws<PEPackerException>(() =>
            ManualBundler.BuildRuntimeIdentifier("linux", (Architecture)0x7ff));

        Assert.Contains("RuntimeIdentifier", ex.Message);
    }

    /// <summary>
    /// The running machine is one of the three, so inference must succeed here and agree with the
    /// platform checks.
    /// </summary>
    [Fact]
    public void CurrentRuntimeIdentifier_IsInferable()
    {
        var rid = ManualBundler.GetCurrentRuntimeIdentifier();

        Assert.Contains('-', rid);
        Assert.StartsWith(
            OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsLinux() ? "linux" : "osx",
            rid,
            StringComparison.Ordinal);
    }

    [Fact]
    public void MoveIntoPlace_Overwriting_ReplacesAnExistingFile()
    {
        var source = Path.Combine(_work, "staged.bin");
        var destination = Path.Combine(_work, "final.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "old");

        ManualBundler.MoveIntoPlace(source, destination, overwrite: true);

        Assert.Equal("new", File.ReadAllText(destination));
        Assert.False(File.Exists(source));
    }

    /// <summary>
    /// The up-front <c>Overwrite</c> check runs before the bundle is built, so it only narrows the
    /// window. A file that appears while the bundle is being written must still not be replaced —
    /// the previous delete-then-move replaced it regardless.
    /// </summary>
    [Fact]
    public void MoveIntoPlace_NotOverwriting_RefusesAFileThatAppearedMeanwhile()
    {
        var source = Path.Combine(_work, "staged2.bin");
        var destination = Path.Combine(_work, "final2.bin");
        File.WriteAllText(source, "new");
        File.WriteAllText(destination, "raced-in");

        var ex = Assert.Throws<PEPackerException>(() =>
            ManualBundler.MoveIntoPlace(source, destination, overwrite: false));

        Assert.Contains("Overwrite", ex.Message);
        Assert.Equal("raced-in", File.ReadAllText(destination));
    }

    [Fact]
    public void MoveIntoPlace_NotOverwriting_MovesOntoAFreePath()
    {
        var source = Path.Combine(_work, "staged3.bin");
        var destination = Path.Combine(_work, "final3.bin");
        File.WriteAllText(source, "new");

        ManualBundler.MoveIntoPlace(source, destination, overwrite: false);

        Assert.Equal("new", File.ReadAllText(destination));
    }

    /// <summary>
    /// A missing entry assembly used to surface as a raw <see cref="FileNotFoundException"/> from
    /// the streaming copy, after a temp output file had already been created — every other input
    /// is validated up front and reported as a <see cref="PEPackerException"/>.
    /// </summary>
    [Fact]
    public void MissingEntryAssembly_IsRejectedUpFront()
    {
        var missing = Path.Combine(_work, "not-there.dll");
        var outputPath = Path.Combine(_work, "out.exe");

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = missing,
                OutputPath = outputPath,
                AssemblyName = "NotThere",
            }));

        Assert.Contains("EntryAssemblyPath", ex.Message);
        Assert.Contains("not-there.dll", ex.Message);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(Directory.GetFiles(_work, "out.exe.tmp*"));
    }

    /// <summary>
    /// Version directory names are parsed, never compared as strings: this is the ordering that
    /// has been got wrong three separate times in this repository.
    /// </summary>
    [Theory]
    [InlineData("10.0.10", "10.0.9")]
    [InlineData("10.0.0", "9.0.17")]
    [InlineData("10.0.100", "10.0.100-rc.1.25451.107")]
    public void VersionUtil_OrdersLikeAVersionRatherThanAString(string higher, string lower)
    {
        Assert.True(VersionUtil.TryParse(higher, out var parsedHigher));
        Assert.True(VersionUtil.TryParse(lower, out var parsedLower));

        Assert.True(parsedHigher > parsedLower, $"{higher} should sort above {lower}");

        // Every case here is one an ordinal string comparison gets backwards, which is the whole
        // reason the parse exists.
        Assert.True(StringComparer.Ordinal.Compare(higher, lower) < 0,
            $"'{higher}' vs '{lower}' no longer demonstrates the string-ordering trap");
    }

    [Fact]
    public void VersionUtil_ReportsPrereleaseSuffixes()
    {
        Assert.True(VersionUtil.TryParse("10.0.100-preview.3", out var prerelease));
        Assert.True(prerelease.IsPrerelease);
        Assert.Equal(new Version(10, 0, 100), prerelease.Version);

        Assert.True(VersionUtil.TryParse("10.0.100", out var stable));
        Assert.False(stable.IsPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not-a-version")]
    [InlineData("Microsoft.NETCore.App.Host.win-x64")]
    public void VersionUtil_RejectsNonVersions(string? text)
    {
        Assert.False(VersionUtil.TryParse(text, out _));
    }

    /// <summary>
    /// There is deliberately no <c>RuntimeEnvironment.GetRuntimeDirectory()</c> fallback: under
    /// Native AOT that returns the application's own directory, and three levels up from it is a
    /// path that exists and is not a dotnet root. Whatever this returns must at least look like
    /// one.
    /// </summary>
    [Fact]
    public void DotNetRoot_EitherFindsARealRootOrNothing()
    {
        var root = DotNetRoot.Find();

        if (root is null)
        {
            return;
        }

        Assert.True(Directory.Exists(root));
        Assert.True(
            Directory.Exists(Path.Combine(root, "shared"))
            || Directory.Exists(Path.Combine(root, "sdk"))
            || Directory.Exists(Path.Combine(root, "packs")),
            $"'{root}' holds none of shared/, sdk/ or packs/, so it is not a dotnet root.");
    }
}
