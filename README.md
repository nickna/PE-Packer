# PE Packer

[![NuGet](https://img.shields.io/nuget/v/NickNa.PEPacker.svg)](https://www.nuget.org/packages/NickNa.PEPacker)

A .NET library for post-processing compiled assemblies: rewriting PE metadata so runtime-emitted assemblies load portably, and packaging managed DLLs into single-file executables — with no required dependency on an installed SDK, and full support for running inside Native AOT applications.

## Why does this exist?

If you generate .NET assemblies at runtime with `System.Reflection.Emit` (`TypeBuilder`, `PersistedAssemblyBuilder`, etc.), every type you emit ends up referencing `System.Private.CoreLib` — the runtime's internal implementation assembly. That works on the machine that emitted it, but `System.Private.CoreLib` is an implementation detail, not a stable contract: the assembly won't load reliably on other machines, other runtime versions, or other runtimes.

The fix is to retarget those references to the official SDK reference assemblies (`System.Runtime`, `System.Collections`, `System.Threading`, …) — the public surface .NET guarantees across versions. That's what `AssemblyReferenceRewriter` does: it reads the compiled PE, rebuilds the entire metadata image with corrected assembly references, patches every IL token, and writes a new valid PE. No decompilation, no recompilation — just metadata surgery.

Once you have a portable DLL, you usually want to ship it as something a user can double-click. The second half of the library — **single-file bundling** — packs a managed DLL into a self-contained `.exe`/executable using the .NET apphost bundle format, without requiring an installed SDK on the machine doing the packing.

### Why not the Microsoft libraries?

Each piece of this problem has an official library that gets close but doesn't solve it:

- **`MetadataLoadContext`** would let you compile against reference assemblies from the start — but its types are inspection-only. You can't pass them to `TypeBuilder.DefineType()` as a base class or interface. So emitting against runtime types is unavoidable, which means the CoreLib references have to be fixed *after* emission. No Microsoft library does that rewriting; `AssemblyReferenceRewriter` is built on `System.Reflection.Metadata` (`MetadataReader`/`MetadataBuilder`), which provides the primitives but not the rewriter.
- **`Microsoft.NET.HostModel`** (the SDK's own bundler) works, but it has to be located inside an installed SDK and loaded dynamically at runtime. That makes it unusable from a Native AOT application — `Assembly.LoadFrom` fails there no matter what is installed — and fragile anywhere the SDK layout isn't guaranteed. PE Packer uses it when it's available and provides a built-in bundler that produces the same bundle format by patching the apphost directly, with the apphost templates for six Windows/Linux RIDs embedded in the package.

### Who is this for?

Anything that emits assemblies at runtime and needs to ship them. The motivating consumer is [SharpTS](https://github.com/nickna/SharpTS), a TypeScript compiler for .NET: it compiles TypeScript to IL via `PersistedAssemblyBuilder`, then uses PE Packer to make the emitted assemblies portable and package them as standalone executables — including when SharpTS itself is published as a Native AOT binary. The same shape applies to other compilers, DSLs, code generators, and plugin systems built on `Reflection.Emit`.

## Features

- **Assembly Reference Rewriting** — Rewrites `System.Private.CoreLib` references to SDK reference assemblies (`System.Runtime`, `System.Collections`, etc.). Handles generics, nested types, method specs, properties, events, P/Invoke, custom attributes and IL token patching. Refuses input it cannot reproduce faithfully rather than emitting a lossy assembly — see [Scope and limitations](#scope-and-limitations).
- **Single-File Bundling** — Creates single-file .NET executables. Automatically selects the SDK bundler when available, falls back to the built-in bundler. Windows and Linux apphost templates are embedded, so built-in bundling does not require an installed SDK.
- **App Host Generation** — Generates standalone executable wrappers around .NET DLLs with proper runtime configuration.
- **Native AOT support** — The library runs inside Native AOT-published applications, verified end to end on linux-x64 in CI and manually on win-arm64. A feature switch lets AOT consumers compile out the SDK-bundler path entirely.

## Requirements

- **.NET 10** — the package targets `net10.0`.
- The built-in bundler needs no installed SDK for `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm`, or `linux-arm64`; those apphost templates ship in the package. The SDK bundler requires an installed .NET SDK. Both output a framework-dependent executable, so the target machine still needs a compatible .NET runtime.

## Installation

```bash
dotnet add package NickNa.PEPacker
```

## Usage

### Assembly Reference Rewriting

The recommended constructor takes an `IReferenceAssemblyIndex`. The package ships one — `EmbeddedReferenceAssemblyIndex` — with the net10.0 framework type map precompiled into `PEPacker.dll`, so rewriting needs no SDK, no reference pack, and nothing else on disk. This is also the AOT-safe path: it's what makes the rewriter work from inside a Native AOT binary on a machine with no .NET installed.

```csharp
using PEPacker;

// sourceAssembly: a compiled DLL with System.Private.CoreLib references
using var sourceStream = File.OpenRead("compiled.dll");
using var rewriter = new AssemblyReferenceRewriter(
    sourceStream,
    EmbeddedReferenceAssemblyIndex.Default,        // precomputed net10.0 index, nothing needed on disk
    ReferencePolicy.RetargetCoreLibOnly);          // see "Reference policy" below
rewriter.Rewrite();

using var output = File.Create("rewritten.dll");
rewriter.Save(output);
```

`EmbeddedReferenceAssemblyIndex.Default` covers `net10.0` (the value of `EmbeddedReferenceAssemblyIndex.EmbeddedTargetFramework`). `EmbeddedReferenceAssemblyIndex.ForTargetFramework("net10.0")` returns the same index and throws a `PEPackerException` for any other TFM rather than silently emitting the wrong facade versions — facade versions are part of the data, so an index must match the framework the rewritten assembly targets. For a framework this package doesn't embed, generate your own index with `EmbeddedReferenceAssemblyIndex.Write` over a `DirectoryReferenceAssemblyIndex` built from that framework's reference pack, and load it back with the `EmbeddedReferenceAssemblyIndex(Stream)` constructor.

Alternatively, if you have a reference pack on disk (or want to rewrite against a custom one), pass its directory instead of an index:

```csharp
// refAssemblyPath: path to SDK ref assemblies, e.g.:
//   C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0
using var rewriter = new AssemblyReferenceRewriter(sourceStream, refAssemblyPath);
```

This overload scans the directory at construction, so it requires the assemblies to actually be there — under Native AOT prefer the embedded index, and never locate the directory via `RuntimeEnvironment.GetRuntimeDirectory()` (under AOT it returns the application's own directory, not a framework).

`Rewrite()` throws `PEPackerException` if the source uses metadata the rewriter does not reproduce. The message names each offending construct and how many rows it has, so the failure points at the feature rather than a table number.

#### Reference policy

The rewriter consults a policy — a `Func<string, ReferenceAction>` keyed by assembly simple name — to decide what happens to each assembly reference in the source: `Keep` it verbatim, `Drop` it, or `RetargetToFacades` (resolve its types against the SDK facades, which is the whole point for `System.Private.CoreLib`).

Constructors that don't take a policy use `ReferencePolicy.Default`, which retargets `System.Private.CoreLib`, **silently drops any reference named `SharpTS`**, and keeps everything else. The SharpTS entry is compatibility, not design: it preserves the behavior of releases up to 1.0.4 for [SharpTS](https://github.com/nickna/SharpTS), which uses that reference as its marker for whether a rewrite is needed — stripping it is the point of the pass there. Unless you are SharpTS, pass `ReferencePolicy.RetargetCoreLibOnly` (retarget CoreLib, keep everything else) or your own delegate; `ReferencePolicy.DroppingReferences("MyEmitHelper", ...)` builds a policy that additionally drops named assemblies. A type reference scoped to a dropped assembly is reported as an error rather than silently nulled.

### Single-File Bundling

```csharp
using PEPacker;
using PEPacker.Bundling;

var result = AppHostGenerator.CreateSingleFileExecutable(
    managedDllPath: "myapp.dll",
    outputExePath: "myapp.exe",
    assemblyName: "myapp"
);

Console.WriteLine($"Bundled with {result.TechniqueDescription}");
```

You can also force a specific bundler:

```csharp
// Force the built-in bundler (does not load Microsoft.NET.HostModel)
var bundler = BundlerFactory.GetBundler(BundlerMode.BuiltIn);
var result = bundler.CreateSingleFileExecutable("myapp.dll", "myapp.exe", "myapp");
```

`BundlerMode.Sdk` requires `Microsoft.NET.HostModel.dll` and throws if it isn't present; `BundlerMode.Auto` (the default) tries the SDK bundler and falls back to the built-in one. `AppHostGenerator.GetPreferredTechnique()` reports which would be chosen without bundling anything.

For explicit target selection, multiple assemblies, or a private apphost template,
use `BundleRequest`:

```csharp
var result = AppHostGenerator.CreateSingleFileExecutable(
    new BundleRequest
    {
        EntryAssemblyPath = "myapp.dll",
        OutputPath = "myapp",
        AssemblyName = "myapp",
        AdditionalAssemblies = ["MyRuntime.dll"],
        RuntimeIdentifier = "linux-arm64",
        FrameworkVersion = new Version(10, 0)
    },
    BundlerMode.BuiltIn);
```

### Native AOT

PE Packer works when the *consuming application* is published with Native AOT — a native
compiler or CLI tool can rewrite and bundle assemblies without carrying a JIT runtime. This is
exercised end to end by a dedicated smoke host that runs the full pipeline from inside a
published native binary — on linux-x64 in CI, and verified manually on win-arm64.

The SDK bundler relies on dynamic assembly loading, which Native AOT cannot do, so AOT
applications should compile that path out with the feature switch:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="PEPacker.EnableSdkBundler"
                                  Value="false"
                                  Trim="true" />
</ItemGroup>
```

With the switch off, `BundlerFactory` goes straight to the built-in bundler, an explicit
`BundlerMode.Sdk` request fails with a diagnostic instead of silently changing modes, and the
trimmer removes the entire SDK-reflection path from the native image. The switch defaults to
enabled, so ordinary managed applications keep SDK detection and `BundlerMode.Auto` behavior
unchanged.

## Scope and limitations

### Rewriting

The rewriter targets assemblies emitted through `PersistedAssemblyBuilder`. It is **not** a general-purpose PE round-tripper, and it is deliberately fail-closed: every metadata table it reproduces is on an explicit allow-list, and a source using anything outside that list is rejected instead of being silently stripped. Rewriting refuses input that carries:

- embedded or linked managed resources, exported/forwarded types, or multi-file assembly manifests
- declarative security attributes
- edit-and-continue deltas, or uncompressed metadata (whose indirection tables break row-order assumptions)
- native Win32 resources

The rewritten image also drops the strong-name signature — output is never re-signed, so the flag is cleared rather than left claiming a signature that isn't there. Re-sign afterwards if you need one.

### Bundling

Both bundlers embed the main assembly, any `AdditionalAssemblies`, and a generated `runtimeconfig.json`. A `.deps.json` is not required for these bundled application assemblies. The result remains framework-dependent rather than self-contained: the target machine needs a compatible .NET runtime. The built-in bundler deliberately refuses macOS because it does not yet perform the required Mach-O adjustment and ad-hoc signing.

## Development

```bash
dotnet build PE-Packer.slnx --configuration Release
dotnet test PE-Packer.slnx --configuration Release
```

The rewriter is checked two independent ways. A metadata differ compares the assembly table-by-table and row-by-row before and after, asserting nothing changed beyond the retargeting the rewrite exists to perform; ILVerify then verifies the output is well-formed by an implementation that shares no code with the rewriter. Both matter — an image can round-trip byte-identical IL and still be invalid, and it can load fine while having quietly lost a table.

## License

See [LICENSE](LICENSE) for details.
