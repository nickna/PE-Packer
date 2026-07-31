using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PEPacker;
using PEPacker.Bundling;

namespace PEPacker.AotSmoke;

/// <summary>
/// Drives PEPacker end to end from inside a Native AOT binary and fails the build if any
/// required step breaks.
/// </summary>
/// <remarks>
/// <para>
/// The xUnit suite cannot cover this. It loads emitted assemblies in-process, so it must stay
/// managed, which means the whole class of "works under JIT, breaks under AOT" problems is
/// invisible to it. Two examples this catches that unit tests structurally cannot:
/// <c>Assembly.LoadFrom</c> failing so the SDK bundler must degrade to the built-in one, and
/// <c>RuntimeEnvironment.GetRuntimeDirectory()</c> returning the application's own directory
/// rather than a framework directory.
/// </para>
/// <para>
/// It also reports which bundler was selected and where the apphost came from, so a run that
/// covered less than expected says so instead of passing quietly.
/// </para>
/// </remarks>
internal static class Program
{
    private static int _failures;
    private static string _work = string.Empty;

    private static int Main()
    {
        _work = Path.Combine(Path.GetTempPath(), "pepacker_aotsmoke_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_work);

        try
        {
            Section("environment");
            var runningAot = !RuntimeFeature.IsDynamicCodeSupported;
            Report("running without dynamic code (Native AOT)", runningAot,
                runningAot ? "yes" : "NO — this run is not exercising AOT");
            Info("runtime identifier", RuntimeInformation.RuntimeIdentifier);
            Info("framework", RuntimeInformation.FrameworkDescription);
            Info("Environment.Version", Environment.Version.ToString());
            Info("typeof(object).Assembly.Location", $"[{typeof(object).Assembly.Location}]");
            Info("RuntimeEnvironment.GetRuntimeDirectory()", RuntimeEnvironment.GetRuntimeDirectory());

            Section("bundler selection degrades without dynamic code");
            Report("SDK bundler feature compiled out", !SdkBundlerDetector.IsSdkBundlerEnabled,
                SdkBundlerDetector.IsSdkBundlerEnabled ? "enabled" : "disabled");
            var technique = BundlerFactory.GetPreferredTechnique();
            Report("built-in bundler selected under AOT", technique == BundleTechnique.ManualBundler,
                technique.ToString());

            Section("rewriter: directory-backed index");
            var frameworkDirectory = FindFrameworkDirectory();
            Info("framework directory", frameworkDirectory ?? "<not found>");
            RewriteWithDirectoryIndex(frameworkDirectory);

            Section("rewriter: in-memory index (no filesystem)");
            RewriteWithInMemoryIndex();

            Section("rewriter: embedded index (no framework on disk)");
            RewriteWithEmbeddedIndex();

            Section("bundling from embedded apphost (no SDK)");
            Bundle();

            Console.WriteLine();
            Console.WriteLine(_failures == 0
                ? "AOT smoke: all required steps passed."
                : $"AOT smoke: {_failures} required step(s) FAILED.");

            return _failures == 0 ? 0 : 1;
        }
        finally
        {
            try { Directory.Delete(_work, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Rewrites a fixture against a real framework directory, which is the path a caller on a
    /// machine with .NET installed takes.
    /// </summary>
    private static void RewriteWithDirectoryIndex(string? frameworkDirectory)
    {
        if (frameworkDirectory is null)
        {
            Report("rewrite via directory index", false, "no framework directory found");
            return;
        }

        try
        {
            var fixture = EmitFixture("SmokeApp");
            var index = new DirectoryReferenceAssemblyIndex(frameworkDirectory);
            Info("indexed", $"{index.TypeCount} types, {index.AssemblyCount} assemblies");

            var rewritten = Rewrite(fixture, index);
            var references = AssemblyReferenceNames(rewritten);

            Report("CoreLib reference removed", !references.Contains("System.Private.CoreLib"),
                string.Join(", ", references));
            Report("System.Runtime reference added", references.Contains("System.Runtime"),
                string.Join(", ", references));
        }
        catch (Exception ex)
        {
            Report("rewrite via directory index", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The path that matters for a tool shipping without a framework on disk.
    /// </summary>
    private static void RewriteWithInMemoryIndex()
    {
        try
        {
            var fixture = EmitFixture("SmokeAppMem");
            var rewritten = Rewrite(fixture, new InMemoryIndex());
            var references = AssemblyReferenceNames(rewritten);

            Report("rewrite with no filesystem access", !references.Contains("System.Private.CoreLib"),
                string.Join(", ", references));
        }
        catch (Exception ex)
        {
            Report("rewrite with no filesystem access", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// The precomputed index shipped in the package, which is what a tool with no framework on
    /// disk actually uses.
    /// </summary>
    private static void RewriteWithEmbeddedIndex()
    {
        try
        {
            var index = EmbeddedReferenceAssemblyIndex.Default;
            Info("embedded", $"{index.TypeCount} types, {index.AssemblyCount} assemblies, "
                + $"tfm {EmbeddedReferenceAssemblyIndex.EmbeddedTargetFramework}");

            var fixture = EmitFixture("SmokeAppEmbedded");
            var rewritten = Rewrite(fixture, index);
            var references = AssemblyReferenceNames(rewritten);

            Report("rewrite via the embedded index", !references.Contains("System.Private.CoreLib"),
                string.Join(", ", references));
        }
        catch (Exception ex)
        {
            Report("rewrite via the embedded index", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Bundles one and then two assemblies, running each result.
    /// </summary>
    private static void Bundle()
    {
        var exeSuffix = OperatingSystem.IsWindows() ? ".exe" : string.Empty;

        // Single assembly, which is all the bundler could express before BundleRequest.
        try
        {
            var app = EmitFixture("SoloApp");
            var exe = Path.Combine(_work, "solo", "SoloApp" + exeSuffix);

            var result = BundleWithDotNetRootHidden(new BundleRequest
            {
                EntryAssemblyPath = app,
                OutputPath = exe,
                AssemblyName = "SoloApp",
            });

            Info("single-assembly bundle", $"{result.TechniqueDescription}, {new FileInfo(exe).Length} bytes");

            var (code, stdout, stderr) = Run(exe);
            Report("single-assembly bundle runs", code == 0 && stdout.Contains("SMOKE-OK"),
                $"exit={code} stdout=[{stdout.Trim()}] stderr=[{First(stderr)}]");
        }
        catch (Exception ex)
        {
            Report("single-assembly bundle", false, $"{ex.GetType().Name}: {ex.Message}");
        }

        // Two assemblies, resolved through the host's bundle probe with no deps.json.
        try
        {
            var library = EmitLibrary("SmokeLib");
            var app = EmitFixture("DuoApp", loadAssemblyByName: "SmokeLib");
            var exe = Path.Combine(_work, "duo", "DuoApp" + exeSuffix);

            BundleWithDotNetRootHidden(new BundleRequest
            {
                EntryAssemblyPath = app,
                OutputPath = exe,
                AssemblyName = "DuoApp",
                AdditionalAssemblies = [library],
            });

            var (code, stdout, stderr) = Run(exe);
            Report("two-assembly bundle runs", code == 0 && stdout.Contains("SmokeLib"),
                $"exit={code} stdout=[{stdout.Trim()}] stderr=[{First(stderr)}]");
        }
        catch (Exception ex)
        {
            Report("two-assembly bundle", false, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Bundles while an existing, empty DOTNET_ROOT hides every installed host pack. The
    /// generated child runs only after the original value is restored. This proves the
    /// template came from PEPacker's resource rather than merely omitting an explicit path
    /// and accidentally finding the runner's SDK.
    /// </summary>
    private static BundleResult BundleWithDotNetRootHidden(BundleRequest request)
    {
        string emptyRoot = Path.Combine(_work, "empty-dotnet-root");
        Directory.CreateDirectory(emptyRoot);
        string? previous = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        Environment.SetEnvironmentVariable("DOTNET_ROOT", emptyRoot);
        try
        {
            return AppHostGenerator.CreateSingleFileExecutable(request, BundlerMode.BuiltIn);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DOTNET_ROOT", previous);
        }
    }

    /// <summary>
    /// Finds a shared framework directory to index.
    /// </summary>
    /// <remarks>
    /// Deliberately not <c>RuntimeEnvironment.GetRuntimeDirectory()</c>: under Native AOT that
    /// returns this application's own directory, which holds no framework assemblies. That is
    /// the trap the rewriter's diagnostics call out, and this smoke test would fall into it.
    /// </remarks>
    private static string? FindFrameworkDirectory()
    {
        foreach (var root in DotNetRoots())
        {
            var shared = Path.Combine(root, "shared", "Microsoft.NETCore.App");
            if (!Directory.Exists(shared)) continue;

            // Sort by parsed version, not by name: "9.0.17" sorts above "10.0.10" as a string,
            // which silently indexed the older framework. VersionUtil (reachable via
            // InternalsVisibleTo) is the one shared parser for this recurring bug.
            var best = Directory.GetDirectories(shared)
                .Where(d => File.Exists(Path.Combine(d, "System.Runtime.dll")))
                .Select(d => (
                    Dir: d,
                    Version: VersionUtil.TryParse(Path.GetFileName(d), out var parsed)
                        ? parsed
                        : (VersionUtil.Parsed?)null))
                .Where(x => x.Version is not null)
                .OrderByDescending(x => x.Version)
                .Select(x => x.Dir)
                .FirstOrDefault();

            if (best is not null) return best;
        }

        return null;
    }

    private static IEnumerable<string> DotNetRoots()
    {
        var fromEnv = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(fromEnv)) yield return fromEnv;

        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
        }
        else
        {
            yield return "/usr/share/dotnet";
            yield return "/usr/lib/dotnet";
            yield return "/usr/local/share/dotnet";
            yield return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet");
        }
    }

    private static byte[] Rewrite(string sourcePath, IReferenceAssemblyIndex index)
    {
        using var rewriter = new AssemblyReferenceRewriter(File.OpenRead(sourcePath), index);
        rewriter.Rewrite();
        using var output = new MemoryStream();
        rewriter.Save(output);
        return output.ToArray();
    }

    private static List<string> AssemblyReferenceNames(byte[] image)
    {
        using var pe = new PEReader(new MemoryStream(image));
        var reader = pe.GetMetadataReader();
        return reader.AssemblyReferences
            .Select(h => reader.GetString(reader.GetAssemblyReference(h).Name))
            .ToList();
    }

    /// <summary>
    /// Emits an executable that prints <c>SMOKE-OK</c>, optionally after resolving another
    /// assembly by simple name so a bundle's extra entries are actually exercised.
    /// </summary>
    [UnconditionalSuppressMessage("AssemblyLoadTrimming", "IL2026:RequiresUnreferencedCode",
        Justification =
            "GetMethod(\"Load\") cannot be narrowed to one overload by the analyzer, so it flags " +
            "the Assembly.Load(byte[]) overloads. Neither is invoked here: the resolved " +
            "MethodInfo is only used as a metadata token in emitted IL, which executes in the " +
            "bundled child process on CoreCLR rather than in this AOT process.")]
    private static string EmitFixture(string name, string? loadAssemblyByName = null)
    {
        var path = Path.Combine(_work, $"{name}.dll");

        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = ab.DefineDynamicModule($"{name}.dll");
        var type = module.DefineType($"{name}.Program", TypeAttributes.Public | TypeAttributes.Class);
        var counter = type.DefineField("Counter", typeof(int), FieldAttributes.Public | FieldAttributes.Static);

        var main = type.DefineMethod("Main", MethodAttributes.Public | MethodAttributes.Static,
            typeof(int), [typeof(string[])]);
        var il = main.GetILGenerator();

        // A generic instantiation, so more than one facade has to be retargeted.
        var dictionary = il.DeclareLocal(typeof(Dictionary<string, int>));
        il.Emit(OpCodes.Newobj, typeof(Dictionary<string, int>).GetConstructor(Type.EmptyTypes)!);
        il.Emit(OpCodes.Stloc, dictionary);
        il.Emit(OpCodes.Ldloc, dictionary);
        il.Emit(OpCodes.Ldstr, "n");
        il.Emit(OpCodes.Ldc_I4, 41);
        il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, int>).GetMethod("Add", [typeof(string), typeof(int)])!);
        il.Emit(OpCodes.Ldloc, dictionary);
        il.Emit(OpCodes.Ldstr, "n");
        il.Emit(OpCodes.Callvirt, typeof(Dictionary<string, int>).GetMethod("get_Item", [typeof(string)])!);
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Stsfld, counter);

        if (loadAssemblyByName is not null)
        {
            // Resolves through the host's bundle probe, so this fails unless the extra assembly
            // is embedded under its own simple name.
            il.Emit(OpCodes.Ldstr, loadAssemblyByName);
            il.Emit(OpCodes.Call, typeof(Assembly).GetMethod("Load", [typeof(string)])!);
            il.Emit(OpCodes.Callvirt, typeof(Assembly).GetMethod("GetName", Type.EmptyTypes)!);
            il.Emit(OpCodes.Callvirt, typeof(AssemblyName).GetMethod("get_Name")!);
            il.Emit(OpCodes.Call, typeof(Console).GetMethod("WriteLine", [typeof(string)])!);
        }

        il.Emit(OpCodes.Ldstr, "SMOKE-OK counter=");
        il.Emit(OpCodes.Ldsfld, counter);
        il.Emit(OpCodes.Box, typeof(int));
        il.Emit(OpCodes.Call, typeof(string).GetMethod("Concat", [typeof(object), typeof(object)])!);
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

    private static string EmitLibrary(string name)
    {
        var path = Path.Combine(_work, $"{name}.dll");

        var ab = new PersistedAssemblyBuilder(new AssemblyName(name), typeof(object).Assembly);
        var module = ab.DefineDynamicModule($"{name}.dll");
        var type = module.DefineType($"{name}.Greeter", TypeAttributes.Public | TypeAttributes.Class);
        var method = type.DefineMethod("Message", MethodAttributes.Public | MethodAttributes.Static,
            typeof(string), []);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldstr, "HELLO-FROM-LIB");
        il.Emit(OpCodes.Ret);
        type.CreateType();

        ab.Save(path);
        return path;
    }

    private static (int ExitCode, string StdOut, string StdErr) Run(string exePath)
    {
        var psi = new ProcessStartInfo(exePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
        };

        using var process = Process.Start(psi)!;

        // Both streams are drained concurrently: reading stdout to end before touching
        // stderr deadlocks once the child fills the untouched pipe's buffer.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            // The old code discarded this bool, so a hung child surfaced later as an
            // InvalidOperationException from ExitCode. The throw lands in the caller's
            // catch block and is reported as a named failure.
            try { process.Kill(entireProcessTree: true); } catch { /* already exited */ }
            throw new TimeoutException(
                $"'{exePath}' did not exit within 120 seconds and was killed.");
        }

        return (process.ExitCode, stdout.GetAwaiter().GetResult(), stderr.GetAwaiter().GetResult());
    }

    private static string First(string text)
    {
        var line = text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? string.Empty;
        return line.Length > 160 ? line[..160] + "..." : line;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"== {title} ==");
    }

    private static void Info(string label, string value) =>
        Console.WriteLine($"   ..  {label}: {value}");

    private static void Report(string label, bool ok, string detail)
    {
        if (!ok) _failures++;
        Console.WriteLine($"   {(ok ? "ok" : "FAIL")}  {label}: {detail}");
    }

    /// <summary>
    /// A framework index with nothing behind it, covering the case a tool shipping without a
    /// framework on disk depends on.
    /// </summary>
    private sealed class InMemoryIndex : IReferenceAssemblyIndex
    {
        private static readonly AssemblyIdentity SystemRuntime = Facade("System.Runtime");
        private static readonly AssemblyIdentity SystemCollections = Facade("System.Collections");
        private static readonly AssemblyIdentity SystemConsole = Facade("System.Console");
        private static readonly AssemblyIdentity SystemRuntimeExtensions = Facade("System.Runtime.Extensions");

        private static readonly Dictionary<string, AssemblyIdentity> Owners = new(StringComparer.Ordinal)
        {
            ["System.Object"] = SystemRuntime,
            ["System.String"] = SystemRuntime,
            ["System.Int32"] = SystemRuntime,
            ["System.Console"] = SystemConsole,
            ["System.Collections.Generic.Dictionary`2"] = SystemCollections,
        };

        private static readonly Dictionary<string, AssemblyIdentity> Identities = new(StringComparer.Ordinal)
        {
            [SystemRuntime.Name] = SystemRuntime,
            [SystemCollections.Name] = SystemCollections,
            [SystemConsole.Name] = SystemConsole,
            [SystemRuntimeExtensions.Name] = SystemRuntimeExtensions,
        };

        private static AssemblyIdentity Facade(string name) => new(
            name,
            new Version(10, 0, 0, 0),
            string.Empty,
            [0xb0, 0x3f, 0x5f, 0x7f, 0x11, 0xd5, 0x0a, 0x3a],
            AssemblyFlags.Retargetable);

        public bool TryResolveType(string fullTypeName, [NotNullWhen(true)] out AssemblyIdentity? owner) =>
            Owners.TryGetValue(fullTypeName, out owner);

        public bool TryGetIdentity(string simpleName, [NotNullWhen(true)] out AssemblyIdentity? identity) =>
            Identities.TryGetValue(simpleName, out identity);
    }
}
