namespace PEPacker.Bundling;

/// <summary>
/// Specifies the bundler selection mode for creating single-file executables.
/// </summary>
public enum BundlerMode
{
    /// <summary>
    /// Automatically select the best available bundler (SDK with fallback to built-in).
    /// </summary>
    Auto,

    /// <summary>
    /// Force use of the SDK bundler. Fails if SDK is not available or bundling fails.
    /// </summary>
    Sdk,

    /// <summary>
    /// Force use of the built-in bundler.
    /// </summary>
    BuiltIn
}

/// <summary>
/// Indicates which bundling technique was used to create the single-file executable.
/// </summary>
public enum BundleTechnique
{
    /// <summary>
    /// Used the official .NET SDK Bundler class from Microsoft.NET.HostModel.dll.
    /// </summary>
    SdkBundler,

    /// <summary>
    /// Used the built-in manual byte-patching bundler, which does not load the SDK's
    /// Microsoft.NET.HostModel.dll. It still reads the apphost template from the installed
    /// Microsoft.NETCore.App.Host.&lt;rid&gt; pack.
    /// </summary>
    ManualBundler
}

/// <summary>
/// Result of a bundle operation.
/// </summary>
/// <param name="OutputPath">Path to the generated single-file executable.</param>
/// <param name="Technique">Which bundling technique was used.</param>
public record BundleResult(string OutputPath, BundleTechnique Technique)
{
    /// <summary>
    /// Gets a human-readable description of the technique used.
    /// </summary>
    public string TechniqueDescription => Technique switch
    {
        BundleTechnique.SdkBundler => "SDK bundler",
        BundleTechnique.ManualBundler => "built-in bundler",
        _ => "unknown bundler"
    };
}

/// <summary>
/// Interface for creating single-file executables from managed assemblies.
/// </summary>
public interface IBundler
{
    /// <summary>
    /// Gets the bundling technique this bundler uses.
    /// </summary>
    BundleTechnique Technique { get; }

    /// <summary>
    /// Creates a single-file executable as described by <paramref name="request"/>.
    /// </summary>
    /// <param name="request">What to bundle, for which target, and where to put it.</param>
    /// <returns>Result containing output path and technique used.</returns>
    BundleResult CreateSingleFileExecutable(BundleRequest request);

    /// <summary>
    /// Creates a single-file executable from a single managed DLL, using defaults for the
    /// target platform and framework version.
    /// </summary>
    /// <param name="dllPath">Path to the managed assembly (.dll).</param>
    /// <param name="exePath">Path for the output executable (.exe).</param>
    /// <param name="assemblyName">Name of the assembly (without extension).</param>
    /// <returns>Result containing output path and technique used.</returns>
    BundleResult CreateSingleFileExecutable(string dllPath, string exePath, string assemblyName) =>
        CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = dllPath,
            OutputPath = exePath,
            AssemblyName = assemblyName
        });
}
