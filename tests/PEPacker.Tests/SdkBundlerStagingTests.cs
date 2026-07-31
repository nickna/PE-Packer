using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers where the SDK bundler puts its output.
/// </summary>
/// <remarks>
/// HostModel names its bundle after the host, so PEPacker used to hand it the caller's output
/// directory and let it write <c>{assemblyName}.exe</c> there before moving the file to the
/// requested name. Any unrelated file already at that name was destroyed — including when
/// <see cref="BundleRequest.Overwrite"/> was false, since that guard only ever examined the
/// requested output path.
/// </remarks>
public class SdkBundlerStagingTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("pepacker_sdkstaging_").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Bundling_DoesNotTouchAnUnrelatedFileNamedAfterTheAssembly()
    {
        if (!SdkBundlerDetector.IsSdkAvailable)
        {
            // Not a silent pass. Assert the reason this machine cannot run the real check, so a
            // machine that does have an SDK cannot quietly skip the only coverage there is of the
            // SDK bundler's staging.
            Assert.Throws<PEPackerException>(() => BundlerFactory.GetBundler(BundlerMode.Sdk));
            return;
        }

        var entryAssembly = Path.Combine(_work, "Probe.dll");
        File.Copy(typeof(PEPackerException).Assembly.Location, entryAssembly);

        var outputDir = Path.Combine(_work, "out");
        Directory.CreateDirectory(outputDir);

        // Exactly the name the bundler used to stage into.
        var bystander = Path.Combine(outputDir, "Probe.exe");
        File.WriteAllText(bystander, "DO-NOT-CLOBBER");

        var result = new SdkBundler().CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = entryAssembly,
            OutputPath = Path.Combine(outputDir, "Bundled.exe"),
            AssemblyName = "Probe",
        });

        Assert.True(File.Exists(result.OutputPath), "the SDK bundler produced no output");
        Assert.True(new FileInfo(result.OutputPath).Length > new FileInfo(entryAssembly).Length,
            "the output is smaller than the assembly it bundles, so nothing was bundled");
        Assert.Equal("DO-NOT-CLOBBER", File.ReadAllText(bystander));
    }

    /// <summary>
    /// Inputs are validated before any staging happens, the same way the built-in bundler does it.
    /// </summary>
    [Fact]
    public void MissingEntryAssembly_IsRejectedUpFront()
    {
        if (!SdkBundlerDetector.IsSdkAvailable)
        {
            Assert.Throws<PEPackerException>(() => BundlerFactory.GetBundler(BundlerMode.Sdk));
            return;
        }

        var ex = Assert.Throws<PEPackerException>(() => new SdkBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = Path.Combine(_work, "not-there.dll"),
                OutputPath = Path.Combine(_work, "out.exe"),
                AssemblyName = "NotThere",
            }));

        Assert.Contains("EntryAssemblyPath", ex.Message);
    }
}
