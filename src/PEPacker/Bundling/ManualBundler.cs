using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PEPacker.Bundling;

/// <summary>
/// Creates single-file executables using manual byte-patching of the apphost template.
/// </summary>
/// <remarks>
/// This avoids the SDK's <c>Microsoft.NET.HostModel.dll</c>, which <see cref="SdkBundler"/>
/// reflects into, so it stays usable when that library is missing — including inside a
/// Native AOT application, where it cannot be loaded at all. It does not remove the need for
/// an SDK installation unless the caller supplies
/// <see cref="BundleRequest.AppHostTemplatePath"/>: otherwise the template comes from the
/// <c>Microsoft.NETCore.App.Host.&lt;rid&gt;</c> pack under the dotnet root.
/// </remarks>
public class ManualBundler : IBundler
{
    // Bundle header placeholder (40 bytes total):
    // - First 8 bytes: header offset (zeros for non-bundle, patched with actual offset)
    // - Next 32 bytes: SHA-256 signature of ".net core bundle"
    private static readonly byte[] BundleHeaderPlaceholder = [
        // 8 bytes for header offset (initially zeros)
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        // 32 bytes signature
        0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38,
        0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
        0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18,
        0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae
    ];

    // DLL path placeholder (SHA-256 of "foobar") - used to locate where to write the DLL name
    private static readonly byte[] DllPathPlaceholder =
        Encoding.UTF8.GetBytes("c3ab8ff13720e8ad9047dd39466b3c8974e592c2fa383d4a3960714caef0c4f2");

    /// <summary>
    /// Assemblies are memory-mapped from the bundle, so each must start on a page boundary.
    /// </summary>
    /// <remarks>
    /// Every assembly, not just the first. Only the main one used to be aligned, which was
    /// invisible while exactly one was ever embedded.
    /// </remarks>
    private const int AssemblyAlignment = 4096;

    /// <summary>Bundle file type for a managed assembly.</summary>
    private const byte FileTypeAssembly = 1;

    /// <summary>Bundle file type for runtimeconfig.json.</summary>
    private const byte FileTypeRuntimeConfigJson = 4;

    /// <inheritdoc/>
    public BundleTechnique Technique => BundleTechnique.ManualBundler;

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(BundleRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.CancellationToken.ThrowIfCancellationRequested();

        var rid = request.RuntimeIdentifier ?? GetCurrentRuntimeIdentifier();
        GuardUnsupportedTarget(rid);

        if (!request.Overwrite && File.Exists(request.OutputPath))
        {
            throw new PEPackerException(
                $"'{request.OutputPath}' already exists and BundleRequest.Overwrite is false.");
        }

        var apphostBytes = LoadAndPatchAppHost(request, rid, out var headerOffsetIndex);

        // Entry assembly first, then any extras. The host's bundle probe matches the exact
        // relative path "<AssemblySimpleName>.dll" at the bundle root, so a name that does not
        // match the assembly's identity is invisible to it and fails when the runtime first
        // needs a type from it. Validating here turns a broken executable into an error.
        var embedded = new List<(string Path, string BundlePath)>
        {
            (request.EntryAssemblyPath, $"{request.AssemblyName}.dll")
        };

        foreach (var additional in request.AdditionalAssemblies)
        {
            embedded.Add((additional, RequireBundleNameMatchesIdentity(additional)));
        }

        var runtimeConfigBytes = Encoding.UTF8.GetBytes(
            RuntimeConfig.Generate(request.FrameworkVersion, request.RollForward));

        var outputDir = Path.GetDirectoryName(Path.GetFullPath(request.OutputPath));
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        // Written to a sibling temp file and moved into place, so a failure part-way through
        // cannot leave a truncated executable at the destination. Streaming also avoids
        // holding the whole bundle — apphost plus every assembly — in memory.
        var tempPath = request.OutputPath + ".tmp" + Guid.NewGuid().ToString("N")[..8];

        try
        {
            WriteBundle(request, tempPath, apphostBytes, headerOffsetIndex, embedded, runtimeConfigBytes);

            if (File.Exists(request.OutputPath))
            {
                File.Delete(request.OutputPath);
            }

            File.Move(tempPath, request.OutputPath);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        SetExecutePermission(request.OutputPath);

        return new BundleResult(request.OutputPath, BundleTechnique.ManualBundler);
    }

    /// <inheritdoc/>
    public BundleResult CreateSingleFileExecutable(string dllPath, string exePath, string assemblyName) =>
        CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = dllPath,
            OutputPath = exePath,
            AssemblyName = assemblyName
        });

    /// <summary>
    /// Writes <c>[apphost][file data...][manifest]</c>, then patches the manifest offset back
    /// into the apphost's placeholder.
    /// </summary>
    private static void WriteBundle(
        BundleRequest request,
        string tempPath,
        byte[] apphostBytes,
        int headerOffsetIndex,
        List<(string Path, string BundlePath)> embedded,
        byte[] runtimeConfigBytes)
    {
        using var stream = new FileStream(tempPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var contentHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        stream.Write(apphostBytes);

        var entries = new List<(long Offset, long Size, byte Type, string BundlePath)>();

        foreach (var (path, bundlePath) in embedded)
        {
            request.CancellationToken.ThrowIfCancellationRequested();
            PadTo(stream, AssemblyAlignment);

            var offset = stream.Position;
            var size = CopyInto(stream, path, contentHash);
            entries.Add((offset, size, FileTypeAssembly, bundlePath));
        }

        var configOffset = stream.Position;
        stream.Write(runtimeConfigBytes);
        contentHash.AppendData(runtimeConfigBytes);
        entries.Add((configOffset, runtimeConfigBytes.Length, FileTypeRuntimeConfigJson,
            $"{request.AssemblyName}.runtimeconfig.json"));

        var manifestOffset = stream.Position;

        // Derived from every embedded byte. Hashing only the entry assembly and the config
        // would let two bundles that differ solely in their extra assemblies collide on one
        // extraction-cache key.
        var bundleId = BundleIdFrom(contentHash.GetHashAndReset());

        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            // Bundle header, version 6.0 (.NET 6+).
            writer.Write((uint)6);
            writer.Write((uint)0);
            writer.Write(entries.Count);
            writer.Write(bundleId);

            // deps.json location. Left absent deliberately: measured on .NET 10.0.10, bundled
            // app assemblies resolve through the host's bundle probe, which does not consult a
            // dependency manifest.
            writer.Write((long)0);
            writer.Write((long)0);

            writer.Write(configOffset);
            writer.Write((long)runtimeConfigBytes.Length);

            // Flags (0 = none)
            writer.Write((ulong)0);

            foreach (var (offset, size, type, bundlePath) in entries)
            {
                writer.Write(offset);
                writer.Write(size);
                writer.Write((long)0);   // compressed size, 0 = stored uncompressed
                writer.Write(type);
                writer.Write(bundlePath);
            }
        }

        stream.Position = headerOffsetIndex;
        stream.Write(BitConverter.GetBytes(manifestOffset));
        stream.Flush();
    }

    /// <summary>
    /// Copies a file into the bundle, hashing it on the way through, and returns its length.
    /// </summary>
    private static long CopyInto(Stream destination, string path, IncrementalHash hash)
    {
        using var source = File.OpenRead(path);
        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            destination.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            total += read;
        }

        return total;
    }

    /// <summary>
    /// Pads with zeros up to the next multiple of <paramref name="alignment"/>.
    /// </summary>
    private static void PadTo(Stream stream, int alignment)
    {
        var misalignment = stream.Position % alignment;
        if (misalignment == 0)
        {
            return;
        }

        Span<byte> padding = stackalloc byte[(int)(alignment - misalignment)];
        padding.Clear();
        stream.Write(padding);
    }

    /// <summary>
    /// Reads the apphost template and patches in the entry assembly's file name, returning the
    /// bytes and the offset of the bundle-header placeholder within them.
    /// </summary>
    private static byte[] LoadAndPatchAppHost(BundleRequest request, string rid, out int headerOffsetIndex)
    {
        var apphostPath = request.AppHostTemplatePath;

        if (apphostPath is not null && !File.Exists(apphostPath))
        {
            throw new PEPackerException(
                $"BundleRequest.AppHostTemplatePath '{apphostPath}' does not exist.");
        }

        if (apphostPath is null)
        {
            apphostPath = FindAppHostTemplateWithVersion(rid).Path
                ?? throw new PEPackerException(
                    $"Could not find an apphost template for '{rid}'. Ensure the .NET SDK is " +
                    $"installed and includes the Microsoft.NETCore.App.Host.{rid} pack, or set " +
                    "BundleRequest.AppHostTemplatePath explicitly.");
        }

        var apphostBytes = File.ReadAllBytes(apphostPath);

        var dllPathIndex = FindSequence(apphostBytes, DllPathPlaceholder);
        if (dllPathIndex < 0)
        {
            throw new PEPackerException(
                $"Could not find the DLL path placeholder in apphost template '{apphostPath}'.");
        }

        var dllNameBytes = Encoding.UTF8.GetBytes($"{request.AssemblyName}.dll");
        if (dllNameBytes.Length >= 1024)
        {
            throw new PEPackerException(
                $"Assembly name '{request.AssemblyName}' does not fit the apphost's 1024-byte path field.");
        }

        Array.Clear(apphostBytes, dllPathIndex, 1024);
        Array.Copy(dllNameBytes, 0, apphostBytes, dllPathIndex, dllNameBytes.Length);

        headerOffsetIndex = FindSequence(apphostBytes, BundleHeaderPlaceholder);
        if (headerOffsetIndex < 0)
        {
            throw new PEPackerException(
                $"Could not find the bundle header placeholder in apphost template '{apphostPath}'.");
        }

        return apphostBytes;
    }

    /// <summary>
    /// Refuses targets this bundler cannot produce a working executable for.
    /// </summary>
    /// <remarks>
    /// macOS needs the Mach-O load-command adjustment and ad-hoc code signature that the
    /// official HostModel bundler applies. Neither is implemented here, and arm64 macOS
    /// refuses to execute an unsigned binary, so a patched apphost would be killed at launch
    /// rather than merely being unusual. Failing here beats shipping that.
    /// </remarks>
    private static void GuardUnsupportedTarget(string rid)
    {
        if (rid.StartsWith("osx", StringComparison.OrdinalIgnoreCase))
        {
            throw new PEPackerException(
                $"The built-in bundler cannot target '{rid}'. macOS executables need Mach-O " +
                "header adjustment and an ad-hoc code signature, which it does not implement, " +
                "and arm64 macOS refuses to run an unsigned binary. Use BundlerMode.Sdk on " +
                "macOS, which delegates to the SDK's own bundler.");
        }
    }

    /// <summary>
    /// Checks that a file's name matches the assembly's simple name, and returns the bundle
    /// path to embed it under.
    /// </summary>
    /// <remarks>
    /// The host's bundle probe looks up <c>&lt;SimpleName&gt;.dll</c> at the bundle root. Measured:
    /// embedding the same assembly as <c>Renamed.dll</c>, or under a subdirectory, produces an
    /// executable that fails with <c>FileNotFoundException</c> the moment the runtime needs a
    /// type from it — with nothing wrong at bundling time to hint at why.
    /// </remarks>
    private static string RequireBundleNameMatchesIdentity(string assemblyPath)
    {
        if (!File.Exists(assemblyPath))
        {
            throw new PEPackerException(
                $"Additional assembly '{assemblyPath}' does not exist.");
        }

        string simpleName;
        try
        {
            using var peReader = new PEReader(File.OpenRead(assemblyPath));
            var reader = peReader.GetMetadataReader();

            if (!reader.IsAssembly)
            {
                throw new PEPackerException(
                    $"Additional assembly '{assemblyPath}' has no assembly manifest, so the host " +
                    "cannot resolve it from the bundle. Only assemblies can be embedded this way.");
            }

            simpleName = reader.GetString(reader.GetAssemblyDefinition().Name);
        }
        catch (BadImageFormatException ex)
        {
            throw new PEPackerException(
                $"Additional assembly '{assemblyPath}' is not a managed assembly.", ex);
        }

        var fileName = Path.GetFileNameWithoutExtension(assemblyPath);
        if (!string.Equals(fileName, simpleName, StringComparison.Ordinal))
        {
            throw new PEPackerException(
                $"Additional assembly '{assemblyPath}' has assembly name '{simpleName}' but file " +
                $"name '{fileName}'. The host resolves bundled assemblies by their simple name, so " +
                $"the file must be named '{simpleName}.dll' or it will not be found at run time.");
        }

        return $"{simpleName}.dll";
    }

    /// <summary>
    /// Reduces a content hash to a 12-character path-safe bundle identifier.
    /// </summary>
    private static string BundleIdFrom(byte[] hash)
    {
        var base64 = Convert.ToBase64String(hash);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=')[..12];
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // The temp file is already being abandoned; a failure to remove it must not
            // replace the exception that got us here.
        }
    }

    private static int FindSequence(byte[] array, byte[] sequence)
    {
        for (int i = 0; i <= array.Length - sequence.Length; i++)
        {
            bool found = true;
            for (int j = 0; j < sequence.Length; j++)
            {
                if (array[i + j] != sequence[j])
                {
                    found = false;
                    break;
                }
            }
            if (found) return i;
        }
        return -1;
    }

    /// <summary>
    /// Finds the apphost template for the current platform.
    /// </summary>
    internal static (string? Path, Version? Version) FindAppHostTemplateWithVersion() =>
        FindAppHostTemplateWithVersion(GetCurrentRuntimeIdentifier());

    /// <summary>
    /// Finds the apphost template for a specific runtime identifier, and the version of the
    /// host pack it came from.
    /// </summary>
    internal static (string? Path, Version? Version) FindAppHostTemplateWithVersion(string rid)
    {
        var dotnetRoot = GetDotNetRoot();
        if (dotnetRoot == null) return (null, null);

        var packsDir = Path.Combine(dotnetRoot, "packs");
        var hostPackPattern = $"Microsoft.NETCore.App.Host.{rid}";

        if (!Directory.Exists(packsDir)) return (null, null);

        var hostPackDirs = Directory.GetDirectories(packsDir)
            .Where(d => Path.GetFileName(d).StartsWith(hostPackPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hostPackDirs.Count == 0) return (null, null);

        // Find highest available version (prefer newer SDKs)
        string? bestPath = null;
        Version? bestVersion = null;

        foreach (var packDir in hostPackDirs)
        {
            foreach (var versionDir in Directory.GetDirectories(packDir))
            {
                var versionStr = Path.GetFileName(versionDir);
                var dashIndex = versionStr.IndexOf('-');
                var cleanVersion = dashIndex > 0 ? versionStr[..dashIndex] : versionStr;

                if (Version.TryParse(cleanVersion, out var version))
                {
                    if (bestVersion == null || version > bestVersion)
                    {
                        // The template's own extension follows the target, not the host.
                        var exeName = rid.StartsWith("win", StringComparison.OrdinalIgnoreCase)
                            ? "apphost.exe"
                            : "apphost";
                        var apphostPath = Path.Combine(versionDir, "runtimes", rid, "native", exeName);
                        if (File.Exists(apphostPath))
                        {
                            bestVersion = version;
                            bestPath = apphostPath;
                        }
                    }
                }
            }
        }

        return (bestPath, bestVersion);
    }

    private static string? GetDotNetRoot()
    {
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
        {
            return dotnetRoot;
        }

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var path = Path.Combine(programFiles, "dotnet");
            if (Directory.Exists(path)) return path;
        }
        else
        {
            var homeDotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
            var paths = new[] { "/usr/share/dotnet", "/usr/local/share/dotnet", "/opt/dotnet", homeDotnet };
            foreach (var path in paths)
            {
                if (Directory.Exists(path)) return path;
            }
        }

        return null;
    }

    internal static string GetCurrentRuntimeIdentifier()
    {
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => "x64"
        };

        if (OperatingSystem.IsWindows()) return $"win-{arch}";
        if (OperatingSystem.IsLinux()) return $"linux-{arch}";
        if (OperatingSystem.IsMacOS()) return $"osx-{arch}";

        return $"win-{arch}";
    }

    /// <summary>
    /// Sets execute permission on the file for Unix systems.
    /// On Windows, this is a no-op.
    /// </summary>
    internal static void SetExecutePermission(string filePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // On Unix, set the execute bit (owner, group, and others can execute)
        // Using UnixFileMode which is available on .NET 6+
        var currentMode = File.GetUnixFileMode(filePath);
        var newMode = currentMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(filePath, newMode);
    }
}
