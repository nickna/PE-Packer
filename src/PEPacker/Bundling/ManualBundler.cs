using System.Buffers.Binary;
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
/// Native AOT application, where it cannot be loaded at all. Apphost templates for supported
/// Windows and Linux RIDs are embedded in PEPacker; an explicit template or an installed
/// <c>Microsoft.NETCore.App.Host.&lt;rid&gt;</c> pack supplies other/private builds.
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
        RequireEntryAssembly(request.EntryAssemblyPath);

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
            MoveIntoPlace(tempPath, request.OutputPath, request.Overwrite);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }

        SetExecutePermission(request.OutputPath);

        return new BundleResult(request.OutputPath, BundleTechnique.ManualBundler);
    }

    // The (dllPath, exePath, assemblyName) convenience overload is the default interface method
    // on IBundler. It was duplicated here byte-for-byte, which is one more place for the defaults
    // it applies to drift.

    /// <summary>
    /// Rejects an entry assembly that is not there, rather than letting the read fail deep in
    /// the bundle writer.
    /// </summary>
    /// <remarks>
    /// Every other input — the apphost template, each additional assembly — is checked before
    /// any work happens and reported as a <see cref="PEPackerException"/>. A missing entry
    /// assembly instead surfaced as a raw <see cref="FileNotFoundException"/> from the streaming
    /// copy, after the output temp file had already been created.
    /// </remarks>
    private static void RequireEntryAssembly(string entryAssemblyPath)
    {
        if (string.IsNullOrEmpty(entryAssemblyPath) || !File.Exists(entryAssemblyPath))
        {
            throw new PEPackerException(
                $"BundleRequest.EntryAssemblyPath '{entryAssemblyPath}' does not exist.");
        }
    }

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

        // Explicitly little-endian, like every other field above: the bundle format is
        // little-endian regardless of the machine writing it, and BitConverter is not.
        stream.Position = headerOffsetIndex;
        Span<byte> manifestOffsetBytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(manifestOffsetBytes, manifestOffset);
        stream.Write(manifestOffsetBytes);
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
        byte[] apphostBytes;
        string apphostDescription;

        if (apphostPath is not null && !File.Exists(apphostPath))
        {
            throw new PEPackerException(
                $"BundleRequest.AppHostTemplatePath '{apphostPath}' does not exist.");
        }

        if (apphostPath is not null)
        {
            apphostBytes = File.ReadAllBytes(apphostPath);
            apphostDescription = apphostPath;
        }
        else if (EmbeddedAppHostProvider.TryRead(rid, out var embeddedAppHost))
        {
            apphostBytes = embeddedAppHost!;
            apphostDescription = $"embedded apphost for '{rid}'";
        }
        else
        {
            apphostPath = FindAppHostTemplateWithVersion(rid).Path;
            if (apphostPath is null)
            {
                throw new PEPackerException(
                    $"Could not resolve an apphost template for '{rid}'. No embedded template " +
                    "is shipped for that RID, no Microsoft.NETCore.App.Host pack was found " +
                    "under the dotnet root, and BundleRequest.AppHostTemplatePath was not set.");
            }

            apphostBytes = File.ReadAllBytes(apphostPath);
            apphostDescription = apphostPath;
        }

        var dllPathIndex = FindSequence(apphostBytes, DllPathPlaceholder);
        if (dllPathIndex < 0)
        {
            throw new PEPackerException(
                $"Could not find the DLL path placeholder in {apphostDescription}.");
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
                $"Could not find the bundle header placeholder in {apphostDescription}.");
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
        var dotnetRoot = DotNetRoot.Find();
        if (dotnetRoot == null) return (null, null);

        var packsDir = Path.Combine(dotnetRoot, "packs");
        var hostPackPattern = $"Microsoft.NETCore.App.Host.{rid}";

        if (!Directory.Exists(packsDir)) return (null, null);

        var hostPackDirs = Directory.GetDirectories(packsDir)
            .Where(d => Path.GetFileName(d).StartsWith(hostPackPattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hostPackDirs.Count == 0) return (null, null);

        // Find highest available version (prefer newer SDKs). Parsed, never compared as a
        // string: "10.0.9" sorts above "10.0.10" as text.
        string? bestPath = null;
        VersionUtil.Parsed? bestVersion = null;

        foreach (var packDir in hostPackDirs)
        {
            foreach (var versionDir in Directory.GetDirectories(packDir))
            {
                if (!VersionUtil.TryParse(Path.GetFileName(versionDir), out var version))
                {
                    continue;
                }

                if (bestVersion is not null && version <= bestVersion.Value)
                {
                    continue;
                }

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

        return (bestPath, bestVersion?.Version);
    }

    /// <summary>
    /// The runtime identifier for the machine this is running on.
    /// </summary>
    /// <exception cref="PEPackerException">
    /// The current OS or architecture is not one that maps to a RID PEPacker can bundle for.
    /// </exception>
    internal static string GetCurrentRuntimeIdentifier() =>
        BuildRuntimeIdentifier(CurrentOSMoniker(), RuntimeInformation.OSArchitecture);

    /// <summary>
    /// The RID OS portion for the running platform, or <see langword="null"/> when it is not one
    /// of the three PEPacker knows how to name.
    /// </summary>
    private static string? CurrentOSMoniker()
    {
        if (OperatingSystem.IsWindows()) return "win";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "osx";
        return null;
    }

    /// <summary>
    /// Joins an OS moniker and an architecture into a runtime identifier, refusing anything it
    /// cannot name.
    /// </summary>
    /// <param name="osMoniker">RID OS portion such as <c>win</c>, or null if unrecognised.</param>
    /// <param name="architecture">Process/OS architecture to name.</param>
    /// <remarks>
    /// This used to answer <c>x64</c> for any unknown architecture and <c>win-{arch}</c> for any
    /// unknown OS. Both are confidently wrong: the inferred RID selects the apphost template, so
    /// a guess produces an executable for the wrong machine, or a template-not-found error naming
    /// a platform the caller is not on. Failing closed makes the caller set
    /// <see cref="BundleRequest.RuntimeIdentifier"/>, which is the only way to get it right.
    /// </remarks>
    internal static string BuildRuntimeIdentifier(string? osMoniker, Architecture architecture)
    {
        if (osMoniker is null)
        {
            throw new PEPackerException(
                $"Cannot infer a runtime identifier: '{RuntimeInformation.OSDescription}' is not " +
                "Windows, Linux or macOS. Set BundleRequest.RuntimeIdentifier explicitly.");
        }

        var arch = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => throw new PEPackerException(
                $"Cannot infer a runtime identifier: architecture '{architecture}' has no known " +
                "RID suffix. Set BundleRequest.RuntimeIdentifier explicitly.")
        };

        return $"{osMoniker}-{arch}";
    }

    /// <summary>
    /// Moves a freshly written file onto the destination, honouring
    /// <see cref="BundleRequest.Overwrite"/> at the moment of the move.
    /// </summary>
    /// <param name="source">The staged file, which is consumed.</param>
    /// <param name="destination">Where it should end up.</param>
    /// <param name="overwrite">Whether an existing destination may be replaced.</param>
    /// <remarks>
    /// The up-front <c>Overwrite</c> check happens before the bundle is built, so on its own it
    /// only narrows the window: the previous delete-then-move replaced a file that appeared in
    /// the meantime even when the caller had said not to. Letting the filesystem enforce it
    /// closes that.
    /// </remarks>
    internal static void MoveIntoPlace(string source, string destination, bool overwrite)
    {
        if (overwrite)
        {
            File.Move(source, destination, overwrite: true);
            return;
        }

        try
        {
            File.Move(source, destination);
        }
        catch (IOException ex) when (File.Exists(destination))
        {
            throw new PEPackerException(
                $"'{destination}' already exists and BundleRequest.Overwrite is false.", ex);
        }
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
