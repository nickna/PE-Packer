using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace PEPacker.Bundling;

/// <summary>
/// Creates single-file executables using the official .NET SDK Bundler class
/// from Microsoft.NET.HostModel.dll via reflection.
/// </summary>
public class SdkBundler : IBundler
{
    private readonly Type _bundlerType;
    private readonly Assembly _hostModelAssembly;

    /// <summary>
    /// Creates a new SdkBundler using the detected SDK.
    /// </summary>
    /// <exception cref="PEPackerException">Thrown if SDK is not available.</exception>
    public SdkBundler()
    {
        var detection = SdkBundlerDetector.DetectionResult;
        if (!detection.IsAvailable || detection.BundlerType == null || detection.HostModelAssembly == null)
        {
            throw SdkBundlerDetector.CreateUnavailableException();
        }

        _bundlerType = detection.BundlerType;
        _hostModelAssembly = detection.HostModelAssembly;
    }

    /// <inheritdoc/>
    public BundleTechnique Technique => BundleTechnique.SdkBundler;

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(BundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.CancellationToken.ThrowIfCancellationRequested();

        var dllPath = request.EntryAssemblyPath;
        var exePath = request.OutputPath;
        var assemblyName = request.AssemblyName;
        var rid = request.RuntimeIdentifier ?? ManualBundler.GetCurrentRuntimeIdentifier();

        if (string.IsNullOrEmpty(dllPath) || !File.Exists(dllPath))
        {
            throw new PEPackerException(
                $"BundleRequest.EntryAssemblyPath '{dllPath}' does not exist.");
        }

        if (!request.Overwrite && File.Exists(exePath))
        {
            throw new PEPackerException(
                $"'{exePath}' already exists and BundleRequest.Overwrite is false.");
        }

        // An explicit template wins. Embedded templates are resolved after the temporary
        // directory exists because HostModel requires a path rather than a stream.
        var apphostPath = request.AppHostTemplatePath;
        if (apphostPath is not null && !File.Exists(apphostPath))
        {
            throw new PEPackerException(
                $"BundleRequest.AppHostTemplatePath '{apphostPath}' does not exist.");
        }

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(exePath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Create a temporary working directory for bundling, plus a separate staging directory
        // for the bundler's output. HostModel names its output after the host, so staging it in
        // the caller's output directory clobbered any unrelated '{assemblyName}.exe' sitting
        // there — including when BundleRequest.Overwrite was false, since that guard only ever
        // looked at the requested output path.
        var tempBundleDir = Path.Combine(Path.GetTempPath(), $"pepacker_bundle_{Guid.NewGuid():N}");
        var tempStageDir = Path.Combine(Path.GetTempPath(), $"pepacker_stage_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempBundleDir);
        Directory.CreateDirectory(tempStageDir);

        try
        {
            if (apphostPath is null)
            {
                if (EmbeddedAppHostProvider.TryRead(rid, out var embeddedAppHost))
                {
                    apphostPath = Path.Combine(tempBundleDir, "apphost-template");
                    File.WriteAllBytes(apphostPath, embeddedAppHost!);
                }
                else
                {
                    apphostPath = ManualBundler.FindAppHostTemplateWithVersion(rid).Path;
                }
            }

            if (apphostPath is null)
            {
                throw new PEPackerException(
                    $"Could not resolve an apphost template for '{rid}'. No embedded template " +
                    "is shipped for that RID, no Microsoft.NETCore.App.Host pack was found " +
                    "under the dotnet root, and BundleRequest.AppHostTemplatePath was not set.");
            }

            // Copy the DLL to the bundle directory
            var bundleDllPath = Path.Combine(tempBundleDir, $"{assemblyName}.dll");
            File.Copy(dllPath, bundleDllPath);

            // Extra assemblies keep their own file names: the host resolves bundled assemblies
            // by simple name, so renaming one would make it unresolvable.
            var additional = new List<string>();
            foreach (var extra in request.AdditionalAssemblies)
            {
                var staged = Path.Combine(tempBundleDir, Path.GetFileName(extra));
                File.Copy(extra, staged, overwrite: true);
                additional.Add(Path.GetFileName(extra));
            }

            // Generate runtimeconfig.json. The apphost pack version describes the PE stub, not
            // the framework the bundle needs, so it plays no part here.
            var runtimeConfigContent = RuntimeConfig.Generate(request.FrameworkVersion, request.RollForward);
            var runtimeConfigPath = Path.Combine(tempBundleDir, $"{assemblyName}.runtimeconfig.json");
            File.WriteAllText(runtimeConfigPath, runtimeConfigContent);

            // Use the SDK Bundler via reflection
            var bundlePath = InvokeSdkBundler(apphostPath, tempStageDir, assemblyName, tempBundleDir,
                additional, request.FrameworkVersion, rid);

            ManualBundler.MoveIntoPlace(bundlePath, exePath, request.Overwrite);
            ManualBundler.SetExecutePermission(exePath);

            return new BundleResult(exePath, BundleTechnique.SdkBundler);
        }
        finally
        {
            TryDeleteDirectory(tempBundleDir);
            TryDeleteDirectory(tempStageDir);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // A leftover temp directory must not replace the result, successful or not.
        }
    }

    /// <summary>
    /// Invokes the SDK Bundler via reflection, producing the bundle inside a staging directory.
    /// </summary>
    /// <param name="apphostPath">Apphost template to patch.</param>
    /// <param name="stagingDir">A directory owned by this call, where the bundle is produced.</param>
    /// <param name="assemblyName">Managed assembly name without extension.</param>
    /// <param name="sourceDir">The directory holding the files to bundle.</param>
    /// <param name="additionalAssemblies">Extra assemblies, by file name within the source directory.</param>
    /// <param name="frameworkVersion">Target framework version, or null for the running one.</param>
    /// <param name="rid">Target runtime identifier.</param>
    /// <returns>The path of the bundle inside <paramref name="stagingDir"/>.</returns>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2080:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    private string InvokeSdkBundler(string apphostPath, string stagingDir, string assemblyName,
        string sourceDir, List<string> additionalAssemblies, Version? frameworkVersion, string rid)
    {
        // First, patch the apphost template with the DLL name using HostWriter
        var patchedApphostPath = Path.Combine(sourceDir, $"{assemblyName}.exe");
        PatchAppHost(apphostPath, patchedApphostPath, $"{assemblyName}.dll");

        // Get the BundleOptions enum type
        var bundleOptionsType = _hostModelAssembly.GetType("Microsoft.NET.HostModel.Bundle.BundleOptions");
        if (bundleOptionsType == null)
        {
            throw new PEPackerException("Could not find BundleOptions type in SDK.");
        }

        // BundleOptions.None = 0
        var bundleOptionsNone = Enum.ToObject(bundleOptionsType, 0);

        // Target platform comes from the requested RID rather than the host, so a bundle can be
        // produced for a platform other than the one bundling it.
        var targetOS = OSPlatformFor(rid);
        var targetArch = ArchitectureFor(rid);

        var effectiveVersion = frameworkVersion ?? Environment.Version;
        var targetFrameworkVersion = new Version(effectiveVersion.Major, effectiveVersion.Minor);

        // Construction is probed across the type's constructors, because HostModel's signature has
        // changed between SDK versions. Only construction is probed: once an instance exists, a
        // later failure is a bundling failure, and retrying it against the next constructor both
        // repeated the work and reported it as "no compatible constructor", which is a diagnosis
        // of the wrong problem with the real exception discarded.
        var bundler = ConstructBundler(assemblyName, stagingDir, bundleOptionsNone, targetOS,
            targetArch, targetFrameworkVersion, apphostPath);

        try
        {
            // Get FileSpec type and create file specs
            var fileSpecType = _hostModelAssembly.GetType("Microsoft.NET.HostModel.Bundle.FileSpec")
                ?? throw new PEPackerException("Could not find FileSpec type in SDK.");

            var fileSpecs = CreateFileSpecList(fileSpecType, sourceDir, assemblyName,
                patchedApphostPath, additionalAssemblies);

            // Find and invoke GenerateBundle method
            var generateBundleMethod = _bundlerType.GetMethod("GenerateBundle")
                ?? throw new PEPackerException("Could not find GenerateBundle method in SDK Bundler.");

            generateBundleMethod.Invoke(bundler, [fileSpecs]);
        }
        catch (Exception ex) when (ex is not PEPackerException)
        {
            var cause = Unwrap(ex);
            throw new PEPackerException(
                $"The SDK bundler failed while generating the bundle for '{rid}': {cause.Message}",
                cause);
        }

        // The bundler names its output after the hostName argument, which is always
        // "{assemblyName}.exe" — on Linux targets too, since it is a name rather than a platform
        // convention. There is no extension-less variant to look for.
        var producedPath = Path.Combine(stagingDir, $"{assemblyName}.exe");
        if (!File.Exists(producedPath))
        {
            throw new PEPackerException(
                $"The SDK bundler reported success but did not create '{producedPath}'.");
        }

        return producedPath;
    }

    /// <summary>
    /// Finds a HostModel <c>Bundler</c> constructor this version of PEPacker can satisfy, and
    /// invokes it.
    /// </summary>
    /// <exception cref="PEPackerException">No constructor could be satisfied or invoked.</exception>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2070:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2080:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2075:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    private object ConstructBundler(string assemblyName, string stagingDir, object bundleOptions,
        OSPlatform targetOS, Architecture targetArch, Version targetFrameworkVersion, string apphostPath)
    {
        var constructors = _bundlerType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);

        Exception? lastException = null;
        string? lastFailure = null;

        foreach (var constructor in constructors.OrderByDescending(c => c.GetParameters().Length))
        {
            var parameters = constructor.GetParameters();
            var args = BuildConstructorArgs(parameters, assemblyName, stagingDir, bundleOptions,
                targetOS, targetArch, targetFrameworkVersion, apphostPath);

            if (args is null)
            {
                lastFailure ??=
                    $"the {parameters.Length}-parameter constructor takes a parameter type PEPacker " +
                    "cannot supply";
                continue;
            }

            try
            {
                return constructor.Invoke(args);
            }
            catch (Exception ex)
            {
                // Unwrap TargetInvocationException to get the real error, then try the next
                // constructor: an argument this version rejects is exactly what probing is for.
                lastException = Unwrap(ex);
                lastFailure = lastException.Message;
            }
        }

        var message =
            "Could not construct the SDK Bundler from Microsoft.NET.HostModel: " +
            (lastFailure ?? "the type exposes no public instance constructor") + ".";

        throw lastException is null
            ? new PEPackerException(message)
            : new PEPackerException(message, lastException);
    }

    /// <summary>
    /// Unwraps the reflection wrapper so the reported failure is the one that actually happened.
    /// </summary>
    private static Exception Unwrap(Exception exception) =>
        exception is TargetInvocationException tie && tie.InnerException is not null
            ? tie.InnerException
            : exception;

    /// <summary>
    /// Builds constructor arguments based on parameter types.
    /// </summary>
    private object?[]? BuildConstructorArgs(ParameterInfo[] parameters, string assemblyName, string outputDir,
        object bundleOptions, OSPlatform targetOS, Architecture targetArch, Version targetFrameworkVersion, string apphostPath)
    {
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramType = param.ParameterType;
            var paramName = param.Name?.ToLowerInvariant() ?? "";

            // Match by parameter type and name
            if (paramType == typeof(string))
            {
                if (paramName == "hostname" || i == 0)
                {
                    // hostName: the name of the single-file bundle
                    args[i] = $"{assemblyName}.exe";
                }
                else if (paramName == "outputdir" || i == 1)
                {
                    args[i] = outputDir;
                }
                else if (paramName == "appassemblyname")
                {
                    // appAssemblyName: the managed assembly name WITHOUT extension
                    // The bundler uses this to compute file names like "{appAssemblyName}.runtimeconfig.json"
                    args[i] = assemblyName;
                }
                else if (IsAppHostSourceParameter(paramName)
                    || (paramName.Contains("apphost", StringComparison.Ordinal)
                        && !paramName.Contains("destination", StringComparison.Ordinal)))
                {
                    args[i] = apphostPath;
                }
                else
                {
                    // Unknown string parameter - try null if nullable
                    args[i] = null;
                }
            }
            else if (paramType == bundleOptions.GetType())
            {
                args[i] = bundleOptions;
            }
            else if (paramType == typeof(OSPlatform) || Nullable.GetUnderlyingType(paramType) == typeof(OSPlatform))
            {
                args[i] = targetOS;
            }
            else if (paramType == typeof(Architecture) || Nullable.GetUnderlyingType(paramType) == typeof(Architecture))
            {
                args[i] = targetArch;
            }
            else if (paramType == typeof(Version))
            {
                args[i] = targetFrameworkVersion;
            }
            else if (paramType == typeof(bool))
            {
                // Matched by name rather than forced to false. HostModel's macosCodesign
                // parameter defaults to true, and ad-hoc signing is the entire reason the
                // built-in bundler's macOS refusal tells callers to use BundlerMode.Sdk — an
                // arm64 macOS binary that is not signed will not launch. Passing false there
                // silently produced exactly the executable that refusal exists to avoid.
                // Everything else (diagnosticOutput) stays off.
                args[i] = paramName.Contains("sign", StringComparison.Ordinal);
            }
            else if (Nullable.GetUnderlyingType(paramType) != null)
            {
                args[i] = null; // Nullable parameter, use null
            }
            else
            {
                return null; // Unknown parameter type we can't satisfy
            }
        }

        return args;
    }

    /// <summary>
    /// Creates a List of FileSpec objects for the bundler.
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2070:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL3050:RequiresDynamicCode",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    private object CreateFileSpecList(Type fileSpecType, string sourceDir, string assemblyName,
        string apphostPath, List<string> additionalAssemblies)
    {
        // Find the FileSpec constructor
        var fileSpecCtor = fileSpecType.GetConstructor([
            typeof(string), // sourcePath
            typeof(string)  // bundleRelativePath
        ]);

        if (fileSpecCtor == null)
        {
            throw new PEPackerException("Could not find FileSpec constructor in SDK.");
        }

        // Create a List<FileSpec>
        var listType = typeof(List<>).MakeGenericType(fileSpecType);
        var list = Activator.CreateInstance(listType)!;
        var addMethod = listType.GetMethod("Add")!;

        // Add the apphost template (this is the host binary that bundler looks for)
        // The BundleRelativePath must match the hostName constructor parameter
        var hostName = $"{assemblyName}.exe";
        var hostSpec = fileSpecCtor.Invoke([apphostPath, hostName]);
        addMethod.Invoke(list, [hostSpec]);

        // Add the DLL
        var dllPath = Path.Combine(sourceDir, $"{assemblyName}.dll");
        var dllSpec = fileSpecCtor.Invoke([dllPath, $"{assemblyName}.dll"]);
        addMethod.Invoke(list, [dllSpec]);

        // Add any extra assemblies under their own names.
        foreach (var extra in additionalAssemblies)
        {
            var extraSpec = fileSpecCtor.Invoke([Path.Combine(sourceDir, extra), extra]);
            addMethod.Invoke(list, [extraSpec]);
        }

        // Add the runtimeconfig.json
        var configPath = Path.Combine(sourceDir, $"{assemblyName}.runtimeconfig.json");
        var configSpec = fileSpecCtor.Invoke([configPath, $"{assemblyName}.runtimeconfig.json"]);
        addMethod.Invoke(list, [configSpec]);

        return list;
    }

    /// <summary>
    /// Maps a runtime identifier's OS portion to an <see cref="OSPlatform"/>.
    /// </summary>
    private static OSPlatform OSPlatformFor(string rid)
    {
        if (rid.StartsWith("win", StringComparison.OrdinalIgnoreCase)) return OSPlatform.Windows;
        if (rid.StartsWith("linux", StringComparison.OrdinalIgnoreCase)) return OSPlatform.Linux;
        if (rid.StartsWith("osx", StringComparison.OrdinalIgnoreCase)) return OSPlatform.OSX;

        throw new PEPackerException($"Unrecognised runtime identifier '{rid}'.");
    }

    /// <summary>
    /// Maps a runtime identifier's architecture suffix to an <see cref="Architecture"/>.
    /// </summary>
    private static Architecture ArchitectureFor(string rid)
    {
        var dash = rid.LastIndexOf('-');
        var arch = dash >= 0 ? rid[(dash + 1)..] : rid;

        return arch.ToLowerInvariant() switch
        {
            "x64" => Architecture.X64,
            "x86" => Architecture.X86,
            "arm64" => Architecture.Arm64,
            "arm" => Architecture.Arm,
            _ => throw new PEPackerException($"Unrecognised architecture in runtime identifier '{rid}'.")
        };
    }

    /// <summary>
    /// Patches the apphost template with the DLL name using HostWriter.
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2072:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2075:DynamicallyAccessedMembers",
        Justification =
            "SdkBundler is constructed only when SdkBundlerDetector reports the SDK bundler " +
            "available, which requires Assembly.LoadFrom to have succeeded. That is impossible " +
            "under Native AOT, where detection returns unavailable and BundlerFactory selects " +
            "ManualBundler instead, so this code is unreachable there. The reflection targets " +
            "live in Microsoft.NET.HostModel.dll, loaded from the SDK on disk and therefore not " +
            "part of this application's trimmed closure.")]
    private void PatchAppHost(string apphostSourcePath, string apphostDestPath, string appBinaryName)
    {
        // Get the HostWriter type
        var hostWriterType = _hostModelAssembly.GetType("Microsoft.NET.HostModel.AppHost.HostWriter");
        if (hostWriterType == null)
        {
            throw new PEPackerException("Could not find HostWriter type in SDK.");
        }

        // Find the CreateAppHost method
        // Looking for: CreateAppHost(string appHostSourceFilePath, string appHostDestinationFilePath, string appBinaryFilePath, ...)
        var createAppHostMethod = hostWriterType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "CreateAppHost" && m.GetParameters().Length >= 3);

        if (createAppHostMethod == null)
        {
            throw new PEPackerException("Could not find CreateAppHost method in HostWriter.");
        }

        var parameters = createAppHostMethod.GetParameters();
        var args = new object?[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var paramName = param.Name?.ToLowerInvariant() ?? "";
            var paramType = param.ParameterType;

            // Every name match is guarded by the parameter type. Without that guard the
            // precedence of `a || b && c` on the third branch was the only thing keeping a bool
            // parameter whose name contains "app" from being handed a string — it happened to
            // bind correctly, which is not the same as being correct.
            if (paramType == typeof(string) && (IsAppHostSourceParameter(paramName) || i == 0))
            {
                args[i] = apphostSourcePath;
            }
            else if (paramType == typeof(string)
                && (paramName.Contains("destination", StringComparison.Ordinal) || i == 1))
            {
                args[i] = apphostDestPath;
            }
            else if (paramType == typeof(string)
                && (paramName.Contains("binary", StringComparison.Ordinal)
                    || (paramName.Contains("app", StringComparison.Ordinal)
                        && !paramName.Contains("apphost", StringComparison.Ordinal))))
            {
                args[i] = appBinaryName;
            }
            else if (paramType == typeof(bool))
            {
                args[i] = false;
            }
            else if (paramType == typeof(string))
            {
                args[i] = null;
            }
            else if (Nullable.GetUnderlyingType(paramType) != null)
            {
                args[i] = null;
            }
            else if (param.HasDefaultValue)
            {
                args[i] = param.DefaultValue;
            }
            else
            {
                args[i] = paramType.IsValueType ? Activator.CreateInstance(paramType) : null;
            }
        }

        try
        {
            createAppHostMethod.Invoke(null, args);
        }
        catch (Exception ex)
        {
            var cause = Unwrap(ex);
            throw new PEPackerException(
                $"HostWriter.CreateAppHost failed for '{apphostSourcePath}': {cause.Message}", cause);
        }
    }

    /// <summary>
    /// Identifies the parameter that takes the apphost template path.
    /// </summary>
    /// <param name="lowerCaseParameterName">The parameter's name, lower-cased.</param>
    /// <remarks>
    /// Matching bare "source" is not safe here. HostModel's <c>CreateAppHost</c> also takes
    /// <c>assemblyToCopyResorcesFrom</c> — the typo is theirs — and the obvious corrected
    /// spelling, <c>assemblyToCopyResourcesFrom</c>, contains "source". A rename that fixes their
    /// typo would silently start passing the apphost template as the resource donor, so the
    /// resource-shaped names are excluded explicitly and the specific
    /// <c>appHostSource…</c> spelling is matched first.
    /// </remarks>
    private static bool IsAppHostSourceParameter(string lowerCaseParameterName)
    {
        if (lowerCaseParameterName.Contains("apphostsource", StringComparison.Ordinal))
        {
            return true;
        }

        if (lowerCaseParameterName.Contains("resource", StringComparison.Ordinal)
            || lowerCaseParameterName.Contains("resorce", StringComparison.Ordinal))
        {
            return false;
        }

        return lowerCaseParameterName.Contains("source", StringComparison.Ordinal);
    }
}
