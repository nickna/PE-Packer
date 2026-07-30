# PE Packer

[![NuGet](https://img.shields.io/nuget/v/NickNa.PEPacker.svg)](https://www.nuget.org/packages/NickNa.PEPacker)

A .NET library for post-processing compiled assemblies: rewriting PE metadata and creating single-file executables.

## Why does this exist?

If you use `System.Reflection.Emit` to generate .NET assemblies at runtime (via `TypeBuilder`, `PersistedAssemblyBuilder`, etc.), the types you emit end up referencing `System.Private.CoreLib` — the internal runtime implementation assembly. That's fine for running locally, but those assemblies won't load correctly on other machines or runtimes because `System.Private.CoreLib` is an implementation detail, not a stable contract.

The fix is to rewrite those references to point at the official SDK reference assemblies (`System.Runtime`, `System.Collections`, `System.Threading`, etc.) — the public surface that .NET guarantees across versions. That's what `AssemblyReferenceRewriter` does. It reads a compiled PE, rebuilds the entire metadata image with corrected assembly references, patches all IL tokens, and writes a new valid PE. No decompilation, no re-compilation — just metadata surgery.

**Why not just use `MetadataLoadContext` types directly?** Because `MetadataLoadContext` types are inspection-only. You can't pass them to `TypeBuilder.DefineType()` for interface implementation or base classes. The workaround is to compile against runtime types (which `TypeBuilder` accepts), then post-process the output to fix the references.

The library also includes **single-file bundling** — the ability to package a managed DLL into a self-contained executable using the .NET apphost, either through the official SDK bundler (`Microsoft.NET.HostModel`) or a built-in byte patcher that produces the same bundle format without taking a dependency on it.

## Features

- **Assembly Reference Rewriting** — Rewrites `System.Private.CoreLib` references to SDK reference assemblies (`System.Runtime`, `System.Collections`, etc.). Handles generics, nested types, method specs, properties, events, P/Invoke, custom attributes and IL token patching. Refuses input it cannot reproduce faithfully rather than emitting a lossy assembly — see [Scope and limitations](#scope-and-limitations).
- **Single-File Bundling** — Creates single-file .NET executables. Automatically selects the SDK bundler when available, falls back to the built-in bundler. Windows and Linux apphost templates are embedded, so built-in bundling does not require an installed SDK.
- **App Host Generation** — Generates standalone executable wrappers around .NET DLLs with proper runtime configuration.

## Requirements

- **.NET 10** — the package targets `net10.0`.
- The built-in bundler needs no installed SDK for `win-x64`, `win-x86`, `win-arm64`, `linux-x64`, `linux-arm`, or `linux-arm64`; those apphost templates ship in the package. The SDK bundler requires an installed .NET SDK. Both output a framework-dependent executable, so the target machine still needs a compatible .NET runtime.

## Installation

```bash
dotnet add package NickNa.PEPacker
```

## Usage

### Assembly Reference Rewriting

```csharp
using PEPacker;

// sourceAssembly: a compiled DLL with System.Private.CoreLib references
// refAssemblyPath: path to SDK ref assemblies, e.g.:
//   C:\Program Files\dotnet\packs\Microsoft.NETCore.App.Ref\10.0.0\ref\net10.0
using var sourceStream = File.OpenRead("compiled.dll");
using var rewriter = new AssemblyReferenceRewriter(sourceStream, refAssemblyPath);
rewriter.Rewrite();

using var output = File.Create("rewritten.dll");
rewriter.Save(output);
```

`Rewrite()` throws `PEPackerException` if the source uses metadata the rewriter does not reproduce. The message names each offending construct and how many rows it has, so the failure points at the feature rather than a table number.

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

Native AOT applications should compile out the SDK bundler, which relies on dynamic assembly
loading and cannot run there. Add this application-level item to the AOT project's `.csproj`:

```xml
<ItemGroup>
  <RuntimeHostConfigurationOption Include="PEPacker.EnableSdkBundler"
                                  Value="false"
                                  Trim="true" />
</ItemGroup>
```

The switch defaults to enabled, so managed applications keep SDK detection and
`BundlerMode.Auto` behavior unchanged. Setting it to false makes an explicit
`BundlerMode.Sdk` request fail with a diagnostic instead of silently changing modes.

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
