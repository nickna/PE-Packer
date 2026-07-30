namespace PEPacker.Bundling;

/// <summary>
/// Everything a bundler needs in order to produce a single-file executable.
/// </summary>
/// <remarks>
/// <see cref="IBundler"/> previously took three positional strings, which could express only
/// "one assembly to one path" and left the target framework, target platform and apphost
/// source implicit in whatever the bundling process happened to be running on.
/// </remarks>
public sealed record BundleRequest
{
    /// <summary>
    /// The managed assembly the host launches.
    /// </summary>
    public required string EntryAssemblyPath { get; init; }

    /// <summary>
    /// Path of the executable to produce.
    /// </summary>
    public required string OutputPath { get; init; }

    /// <summary>
    /// Assembly name without extension, used for the embedded file names.
    /// </summary>
    public required string AssemblyName { get; init; }

    /// <summary>
    /// Further managed assemblies to embed, such as a runtime library the entry assembly
    /// depends on.
    /// </summary>
    /// <remarks>
    /// Each is embedded at the bundle root as <c>&lt;AssemblySimpleName&gt;.dll</c>, because the
    /// host's bundle probe matches that exact relative path. A file whose name does not match
    /// the assembly's simple name, or one placed in a subdirectory, is invisible to the probe
    /// and fails when the runtime first needs the type — so the bundler validates the name
    /// rather than producing an executable that breaks later.
    /// <para>
    /// No <c>.deps.json</c> is required. Measured on .NET 10.0.10: bundled app assemblies
    /// resolve through the host's bundle probe, which does not consult a dependency manifest.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> AdditionalAssemblies { get; init; } = [];

    /// <summary>
    /// Target runtime identifier, e.g. <c>win-x64</c>. Defaults to the current platform.
    /// </summary>
    /// <remarks>
    /// Selects the apphost template, so it determines which platform the output runs on.
    /// </remarks>
    public string? RuntimeIdentifier { get; init; }

    /// <summary>
    /// Target framework version. Only major and minor are used; the patch is always emitted
    /// as zero so any patch level satisfies the bundle. Defaults to the running framework.
    /// </summary>
    /// <remarks>
    /// Do not leave this null in a Native AOT build unless the running framework is genuinely
    /// the target: <see cref="Environment.Version"/> reports the ILCompiler runtime pack the
    /// tool was built against, which is a build-machine artifact.
    /// </remarks>
    public Version? FrameworkVersion { get; init; }

    /// <summary>
    /// Roll-forward policy recorded in the bundled <c>runtimeconfig.json</c>.
    /// </summary>
    public RollForwardPolicy RollForward { get; init; } = RollForwardPolicy.LatestMinor;

    /// <summary>
    /// An explicit apphost template to patch. When null, PEPacker uses its embedded
    /// template for supported Windows/Linux RIDs, then falls back to an installed
    /// <c>Microsoft.NETCore.App.Host.&lt;rid&gt;</c> pack.
    /// </summary>
    public string? AppHostTemplatePath { get; init; }

    /// <summary>
    /// Whether an existing file at <see cref="OutputPath"/> may be replaced.
    /// </summary>
    public bool Overwrite { get; init; } = true;

    /// <summary>
    /// Cancels bundling.
    /// </summary>
    public CancellationToken CancellationToken { get; init; }
}
