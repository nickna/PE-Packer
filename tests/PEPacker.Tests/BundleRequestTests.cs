using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using PEPacker.Bundling;
using Xunit;

namespace PEPacker.Tests;

/// <summary>
/// Covers bundling through <see cref="BundleRequest"/>, and in particular embedding more than
/// one assembly — which the bundler could not express before.
/// </summary>
/// <remarks>
/// The multi-assembly behaviour rests on a measured fact: bundled app assemblies resolve
/// through the host's bundle probe, which matches the exact relative path
/// <c>&lt;SimpleName&gt;.dll</c> at the bundle root and does not consult a <c>.deps.json</c>.
/// Subdirectories and renames fail silently at run time, so the bundler validates names.
/// </remarks>
public class BundleRequestTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("pepacker_bundle_").FullName;

    /// <summary>Executable suffix for the running platform.</summary>
    private static string ExeSuffix => OperatingSystem.IsWindows() ? ".exe" : string.Empty;

    public void Dispose()
    {
        try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// The capability this adds: an entry assembly plus a library it genuinely calls into,
    /// bundled together and executed. The call is resolved while the JIT prepares Main, so a
    /// missing library fails before any output rather than being silently tolerated.
    /// </summary>
    [Fact]
    public void MultiAssemblyBundle_Executes_WithNoDepsJson()
    {
        var libPath = EmitLibrary("ProbeLib");
        var appPath = EmitAppCalling(libPath, "ProbeApp");
        var exePath = Path.Combine(_work, "out", "ProbeApp" + ExeSuffix);

        var result = AppHostGenerator.CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = appPath,
            OutputPath = exePath,
            AssemblyName = "ProbeApp",
            AdditionalAssemblies = [libPath],
        }, BundlerMode.BuiltIn);

        Assert.Equal(exePath, result.OutputPath);

        // Run from a directory holding only the exe, so nothing can resolve from disk.
        var (exitCode, stdout, stderr) = Run(exePath);
        Assert.Equal(0, exitCode);
        Assert.Contains("HELLO-FROM-LIB", stdout);
        Assert.Empty(stderr.Trim());
    }

    /// <summary>
    /// The negative control for the test above: without the library the same bundle must fail,
    /// otherwise a passing multi-assembly test would prove nothing.
    /// </summary>
    [Fact]
    public void Bundle_OmittingARequiredAssembly_FailsAtRunTime()
    {
        var apphost = RequireAppHost();

        var libPath = EmitLibrary("ProbeLib");
        var appPath = EmitAppCalling(libPath, "ProbeApp");
        var exePath = Path.Combine(_work, "control", "ProbeApp" + ExeSuffix);

        AppHostGenerator.CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = appPath,
            OutputPath = exePath,
            AssemblyName = "ProbeApp",
            AppHostTemplatePath = apphost,
            // AdditionalAssemblies deliberately empty.
        }, BundlerMode.BuiltIn);

        var (exitCode, _, _) = Run(exePath);
        Assert.NotEqual(0, exitCode);
    }

    /// <summary>
    /// A file whose name does not match its assembly identity is invisible to the bundle probe,
    /// so it must be rejected rather than producing an executable that breaks later.
    /// </summary>
    [Fact]
    public void AdditionalAssembly_WithMismatchedFileName_IsRejected()
    {
        var libPath = EmitLibrary("ProbeLib");
        var renamed = Path.Combine(_work, "Renamed.dll");
        File.Copy(libPath, renamed);

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = Path.Combine(_work, "x.exe"),
                AssemblyName = "ProbeLib",
                AdditionalAssemblies = [renamed],
            }));

        Assert.Contains("ProbeLib", ex.Message);
        Assert.Contains("Renamed", ex.Message);
    }

    [Fact]
    public void AdditionalAssembly_ThatIsNotManaged_IsRejected()
    {
        var libPath = EmitLibrary("ProbeLib");
        var junk = Path.Combine(_work, "Junk.dll");
        File.WriteAllBytes(junk, [0x00, 0x01, 0x02, 0x03]);

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = Path.Combine(_work, "x.exe"),
                AssemblyName = "ProbeLib",
                AdditionalAssemblies = [junk],
            }));

        Assert.Contains("Junk", ex.Message);
    }

    [Fact]
    public void Overwrite_False_RefusesAnExistingOutput()
    {
        var libPath = EmitLibrary("ProbeLib");
        var exePath = Path.Combine(_work, "exists.exe");
        File.WriteAllText(exePath, "occupied");

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = exePath,
                AssemblyName = "ProbeLib",
                Overwrite = false,
            }));

        Assert.Contains("Overwrite", ex.Message);
        Assert.Equal("occupied", File.ReadAllText(exePath));
    }

    /// <summary>
    /// The built-in bundler produces a binary macOS will refuse to run, so it must say so
    /// rather than emitting one.
    /// </summary>
    [Fact]
    public void BuiltInBundler_TargetingMacOS_RefusesWithAReason()
    {
        var libPath = EmitLibrary("ProbeLib");

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = Path.Combine(_work, "mac.out"),
                AssemblyName = "ProbeLib",
                RuntimeIdentifier = "osx-arm64",
            }));

        Assert.Contains("osx-arm64", ex.Message);
        Assert.Contains("signature", ex.Message);
    }

    [Fact]
    public void UnknownRuntimeIdentifier_ReportsTheRid()
    {
        var libPath = EmitLibrary("ProbeLib");

        var ex = Assert.Throws<PEPackerException>(() => new ManualBundler().CreateSingleFileExecutable(
            new BundleRequest
            {
                EntryAssemblyPath = libPath,
                OutputPath = Path.Combine(_work, "weird.exe"),
                AssemblyName = "ProbeLib",
                RuntimeIdentifier = "plan9-sparc",
            }));

        Assert.Contains("plan9-sparc", ex.Message);
    }

    [Fact]
    public void FrameworkVersionAndRollForward_ReachTheBundledRuntimeConfig()
    {
        var apphost = RequireAppHost();

        var libPath = EmitLibrary("ProbeLib");
        var exePath = Path.Combine(_work, "cfg", "ProbeLib" + ExeSuffix);

        new ManualBundler().CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = libPath,
            OutputPath = exePath,
            AssemblyName = "ProbeLib",
            FrameworkVersion = new Version(9, 3, 7),
            RollForward = RollForwardPolicy.LatestPatch,
            AppHostTemplatePath = apphost,
        });

        var text = System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(exePath));
        Assert.Contains("\"version\": \"9.3.0\"", text);
        Assert.Contains("\"rollForward\": \"latestPatch\"", text);
        Assert.Contains("\"tfm\": \"net9.3\"", text);
    }

    /// <summary>
    /// Every embedded assembly must start on a page boundary, since they are memory-mapped from
    /// the bundle. Only the first used to be aligned, which was invisible while exactly one was
    /// ever embedded.
    /// </summary>
    [Fact]
    public void EveryEmbeddedAssembly_IsPageAligned()
    {
        var apphost = RequireAppHost();

        var libPath = EmitLibrary("ProbeLib");
        var appPath = EmitAppCalling(libPath, "ProbeApp");
        var exePath = Path.Combine(_work, "align", "ProbeApp" + ExeSuffix);

        new ManualBundler().CreateSingleFileExecutable(new BundleRequest
        {
            EntryAssemblyPath = appPath,
            OutputPath = exePath,
            AssemblyName = "ProbeApp",
            AdditionalAssemblies = [libPath],
            AppHostTemplatePath = apphost,
        });

        var bytes = File.ReadAllBytes(exePath);
        var appLength = new FileInfo(appPath).Length;
        var libLength = new FileInfo(libPath).Length;

        foreach (var (name, length) in new[] { ("ProbeApp", appLength), ("ProbeLib", libLength) })
        {
            var offset = FindAlignedOccurrence(bytes, File.ReadAllBytes(name == "ProbeApp" ? appPath : libPath));
            Assert.True(offset >= 0, $"{name} was not found at a 4096-aligned offset in the bundle");
            Assert.Equal(0, offset % 4096);
            Assert.True(length > 0);
        }
    }

    /// <summary>
    /// Finds <paramref name="needle"/> in <paramref name="haystack"/> at a 4096-aligned offset.
    /// </summary>
    private static long FindAlignedOccurrence(byte[] haystack, byte[] needle)
    {
        for (long offset = 0; offset + needle.Length <= haystack.Length; offset += 4096)
        {
            if (haystack.AsSpan((int)offset, needle.Length).SequenceEqual(needle))
            {
                return offset;
            }
        }
        return -1;
    }

    /// <summary>
    /// Returns an apphost template, or skips the calling test.
    /// </summary>
    /// <remarks>
    /// Previously this returned a bool and callers quietly returned early, so a test named
    /// <c>MultiAssemblyBundle_Executes_...</c> reported <em>Passed</em> in an environment where it
    /// had bundled nothing. That is worse than having no test: it reads as coverage. Skipping
    /// makes the gap visible in the run summary, and the NuGet-cache fallback means it is rarely
    /// needed — the pack is often restored without being installed under the dotnet root.
    /// </remarks>
    private static string RequireAppHost()
    {
        var fromPack = AppHostGenerator.FindAppHostTemplate();
        if (fromPack is not null)
        {
            return fromPack;
        }

        var fromCache = FindAppHostInNuGetCache();
        if (fromCache is not null)
        {
            return fromCache;
        }

        // Deliberately a failure rather than a quiet pass. An apphost is present in every
        // environment this suite is expected to run in -- verified on Windows, on the ubuntu CI
        // runner, and on linux-arm64 -- so its absence means bundling is not being covered, and
        // that should be visible rather than inferred from a suspiciously fast "Passed".
        Assert.Fail(
            $"No apphost template for '{RuntimeInformation.RuntimeIdentifier}' under the dotnet " +
            "root or in the NuGet cache, so bundling cannot be exercised. Install the .NET SDK, " +
            $"or restore the Microsoft.NETCore.App.Host.{RuntimeInformation.RuntimeIdentifier} " +
            "package so it lands in the NuGet cache.");

        throw new InvalidOperationException("unreachable: Assert.Fail always throws");
    }

    /// <summary>
    /// Looks for a restored-but-not-installed host pack.
    /// </summary>
    private static string? FindAppHostInNuGetCache()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var root = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        var packageDir = Path.Combine(root, $"microsoft.netcore.app.host.{rid}");

        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        var name = OperatingSystem.IsWindows() ? "apphost.exe" : "apphost";
        return Directory.GetDirectories(packageDir)
            .Select(v => Path.Combine(v, "runtimes", rid, "native", name))
            .Where(File.Exists)
            .OrderByDescending(p => p, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private (int ExitCode, string StdOut, string StdErr) Run(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60_000);
        return (process.ExitCode, stdout, stderr);
    }

    /// <summary>
    /// Emits a library exposing <c>Greeter.Message()</c>.
    /// </summary>
    private string EmitLibrary(string name)
    {
        var path = Path.Combine(_work, $"{name}.dll");

        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = ab.DefineDynamicModule($"{name}.dll");
        var type = module.DefineType($"{name}.Greeter", TypeAttributes.Public | TypeAttributes.Class);
        var method = type.DefineMethod("Message", MethodAttributes.Public | MethodAttributes.Static, typeof(string), []);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "HELLO-FROM-LIB");
        il.Emit(OpCodes.Ret);
        type.CreateType();

        // PersistedAssemblyBuilder.Save writes a loadable library image directly. Hand-building
        // the PE header for a DLL produced something the runtime rejected with
        // BadImageFormatException, and getting that right is not what these tests are about.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ab.Save(path);
        return path;
    }

    /// <summary>
    /// Emits an executable whose entry point resolves the library by simple name, which is
    /// exactly the lookup the host's bundle probe serves.
    /// </summary>
    /// <remarks>
    /// Resolving by name rather than emitting a direct call keeps the entry assembly referencing
    /// nothing but CoreLib, so the fixture needs no cross-assembly emit. It still fails loudly
    /// when the library is absent from the bundle, which the companion negative-control test
    /// confirms.
    /// </remarks>
    private string EmitAppCalling(string libraryPath, string name)
    {
        _ = libraryPath;

        var path = Path.Combine(_work, $"{name}.dll");

        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = ab.DefineDynamicModule($"{name}.dll");
        var type = module.DefineType($"{name}.Program", TypeAttributes.Public | TypeAttributes.Class);
        var main = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(string[])]);

        var il = main.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "HELLO-FROM-LIB from ");
        il.Emit(OpCodes.Ldstr, "ProbeLib");
        il.Emit(OpCodes.Call, typeof(Assembly).GetMethod("Load", [typeof(string)])!);
        il.Emit(OpCodes.Callvirt, typeof(Assembly).GetMethod("GetName", Type.EmptyTypes)!);
        il.Emit(OpCodes.Callvirt, typeof(AssemblyName).GetMethod("get_Name")!);
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(string), typeof(string)])!);
        il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(string)])!);
        il.Emit(OpCodes.Ldc_I4_0);
        il.Emit(OpCodes.Ret);
        type.CreateType();

        var metadata = ab.GenerateMetadata(out var ilStream, out var fieldData);
        var peBuilder = new ManagedPEBuilder(
            new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage,
                subsystem: Subsystem.WindowsCui),
            new MetadataRootBuilder(metadata),
            ilStream,
            fieldData,
            entryPoint: MetadataTokens.MethodDefinitionHandle(main.MetadataToken & 0x00FFFFFF));

        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        blob.WriteContentTo(file);
        return path;
    }
}
