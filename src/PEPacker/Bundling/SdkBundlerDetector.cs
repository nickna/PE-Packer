using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace PEPacker.Bundling;

/// <summary>
/// Detects whether the .NET SDK's Microsoft.NET.HostModel.dll is available
/// and provides access to the SDK bundler via reflection.
/// </summary>
public static class SdkBundlerDetector
{
    /// <summary>
    /// AppContext switch that controls whether the SDK bundler is available.
    /// </summary>
    /// <remarks>
    /// Native AOT consumers can set this switch to <see langword="false"/> with a
    /// <c>RuntimeHostConfigurationOption</c> whose <c>Trim</c> metadata is
    /// <see langword="true"/>. That lets the compiler remove the SDK bundler and its
    /// reflection-only implementation from the native image. The switch defaults to
    /// <see langword="true"/> so managed applications retain the existing behavior.
    /// </remarks>
    public const string EnableSdkBundlerFeatureSwitchName = "PEPacker.EnableSdkBundler";

    /// <summary>
    /// Gets whether this application enables the SDK bundler.
    /// </summary>
    [FeatureSwitchDefinition(EnableSdkBundlerFeatureSwitchName)]
    public static bool IsSdkBundlerEnabled =>
        !AppContext.TryGetSwitch(EnableSdkBundlerFeatureSwitchName, out bool enabled) || enabled;

    /// <summary>
    /// Result of SDK detection containing availability status and assembly path.
    /// </summary>
    public record SdkDetectionResult(bool IsAvailable, string? HostModelPath, Assembly? HostModelAssembly, Type? BundlerType);

    /// <summary>
    /// Gets whether the .NET SDK bundler is available.
    /// </summary>
    public static bool IsSdkAvailable =>
        IsSdkBundlerEnabled && DetectionCache.Result.Value.IsAvailable;

    /// <summary>
    /// Gets the detection result with full details.
    /// </summary>
    public static SdkDetectionResult DetectionResult => DetectionCache.Result.Value;

    /// <summary>
    /// Builds the diagnostic for a caller that demanded the SDK bundler when it is
    /// unavailable, distinguishing the three reasons that can happen.
    /// </summary>
    /// <remarks>
    /// The previous single message told every caller to "Ensure the .NET SDK is installed",
    /// which is actively wrong under Native AOT: detection fails there even with a complete
    /// SDK present, because <see cref="Assembly.LoadFrom"/> is unavailable as a property of
    /// the compilation model rather than because anything is missing. Measured on a machine
    /// with four SDKs and seven runtimes installed, a native binary located
    /// <c>Microsoft.NET.HostModel.dll</c> on disk and still could not load it — so the
    /// advice sent the reader to install something they already had.
    /// </remarks>
    internal static PEPackerException CreateUnavailableException()
    {
        if (!IsSdkBundlerEnabled)
        {
            return new PEPackerException(
                $"The SDK bundler is disabled by the '{EnableSdkBundlerFeatureSwitchName}' " +
                "application feature switch. Use BundlerMode.BuiltIn, or remove the switch " +
                "from a managed application that requires BundlerMode.Sdk.");
        }

        var detection = DetectionResult;

        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            string found = detection.HostModelPath is null
                ? "."
                : $", even though it was found at '{detection.HostModelPath}'.";

            return new PEPackerException(
                "The SDK bundler must load Microsoft.NET.HostModel.dll at run time, and dynamic " +
                $"assembly loading is unavailable in a Native AOT build{found} " +
                "Installing the .NET SDK does not enable it — the limitation is the compilation " +
                "model, not a missing installation. Use BundlerMode.BuiltIn, which BundlerMode.Auto " +
                "already selects automatically here, or run a managed (non-AOT) build if the SDK " +
                "bundler is specifically required.");
        }

        if (detection.HostModelPath is null)
        {
            return new PEPackerException(
                "The SDK bundler is unavailable because no .NET SDK was found: " +
                "Microsoft.NET.HostModel.dll is not present under any known dotnet root. Install " +
                "the .NET SDK, set DOTNET_ROOT if it is installed somewhere non-standard, or use " +
                "BundlerMode.BuiltIn.");
        }

        return new PEPackerException(
            $"The SDK bundler is unavailable: '{detection.HostModelPath}' was found but could not " +
            "be loaded, or does not contain Microsoft.NET.HostModel.Bundle.Bundler. The SDK " +
            "installation may be damaged or a different version than expected. Use " +
            "BundlerMode.BuiltIn to bundle without it.");
    }

    /// <summary>
    /// Performs SDK detection (called once via Lazy).
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "Loads Microsoft.NET.HostModel.dll from the installed SDK on disk, so the types read " +
            "from it are not part of this application's trimmed closure and cannot be removed by " +
            "trimming it. Under Native AOT the load throws and is caught here, which is the " +
            "supported outcome: detection reports unavailable and BundlerFactory selects " +
            "ManualBundler.")]
    private static SdkDetectionResult DetectSdk()
    {
        var hostModelPath = FindHostModelDll();
        if (hostModelPath == null)
        {
            return new SdkDetectionResult(false, null, null, null);
        }

        try
        {
            var assembly = Assembly.LoadFrom(hostModelPath);
            var bundlerType = assembly.GetType("Microsoft.NET.HostModel.Bundle.Bundler");

            if (bundlerType == null)
            {
                return new SdkDetectionResult(false, hostModelPath, assembly, null);
            }

            return new SdkDetectionResult(true, hostModelPath, assembly, bundlerType);
        }
        catch
        {
            return new SdkDetectionResult(false, hostModelPath, null, null);
        }
    }

    /// <summary>
    /// Finds Microsoft.NET.HostModel.dll in the .NET SDK installation.
    /// </summary>
    private static string? FindHostModelDll()
    {
        var dotnetRoot = DotNetRoot.Find();
        if (dotnetRoot == null)
        {
            return null;
        }

        var sdkDir = Path.Combine(dotnetRoot, "sdk");
        if (!Directory.Exists(sdkDir))
        {
            return null;
        }

        // Find the highest version SDK
        var sdkVersion = FindHighestSdkVersion(sdkDir);
        if (sdkVersion == null)
        {
            return null;
        }

        var hostModelPath = Path.Combine(sdkDir, sdkVersion, "Microsoft.NET.HostModel.dll");
        if (File.Exists(hostModelPath))
        {
            return hostModelPath;
        }

        return null;
    }

    /// <summary>
    /// Finds the highest version SDK directory.
    /// </summary>
    /// <remarks>
    /// Ordered by parsed version, never by name: "10.0.9" sorts above "10.0.10" as a string, and
    /// picking the wrong SDK here means reflecting into a <c>Microsoft.NET.HostModel.dll</c> whose
    /// <c>Bundler</c> signature is not the one on the machine's newest SDK.
    /// </remarks>
    private static string? FindHighestSdkVersion(string sdkDir)
    {
        try
        {
            VersionUtil.Parsed? bestVersion = null;
            string? bestName = null;

            foreach (var dir in Directory.GetDirectories(sdkDir))
            {
                var name = Path.GetFileName(dir);

                if (VersionUtil.TryParse(name, out var version)
                    && (bestVersion is null || version > bestVersion.Value))
                {
                    bestVersion = version;
                    bestName = name;
                }
            }

            return bestName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Keeps the SDK-detection lazy and its reference to <see cref="DetectSdk"/> out of the
    /// detector's own type initializer. When the feature switch is compiled to
    /// <see langword="false"/>, this nested type is unreachable and ILC can remove the entire
    /// detection path.
    /// </summary>
    private static class DetectionCache
    {
        internal static readonly Lazy<SdkDetectionResult> Result =
            new(DetectSdk, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}
