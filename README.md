# PE Packer

[![NuGet](https://img.shields.io/nuget/v/NickNa.PEPacker.svg)](https://www.nuget.org/packages/NickNa.PEPacker)

A .NET library for post-processing compiled assemblies: rewriting PE metadata and creating single-file executables.

## Why does this exist?

If you use `System.Reflection.Emit` to generate .NET assemblies at runtime (via `TypeBuilder`, `PersistedAssemblyBuilder`, etc.), the types you emit end up referencing `System.Private.CoreLib` — the internal runtime implementation assembly. That's fine for running locally, but those assemblies won't load correctly on other machines or runtimes because `System.Private.CoreLib` is an implementation detail, not a stable contract.

The fix is to rewrite those references to point at the official SDK reference assemblies (`System.Runtime`, `System.Collections`, `System.Threading`, etc.) — the public surface that .NET guarantees across versions. That's what `AssemblyReferenceRewriter` does. It reads a compiled PE, rebuilds the entire metadata table with corrected assembly references, patches all IL tokens, and writes a new valid PE. No decompilation, no re-compilation — just metadata surgery.

**Why not just use `MetadataLoadContext` types directly?** Because `MetadataLoadContext` types are inspection-only. You can't pass them to `TypeBuilder.DefineType()` for interface implementation or base classes. The workaround is to compile against runtime types (which `TypeBuilder` accepts), then post-process the output to fix the references.

The library also includes **single-file bundling** — the ability to package a managed DLL into a self-contained executable using the .NET apphost. It supports both the official SDK bundler (via `Microsoft.NET.HostModel`) and a built-in manual PE patcher that works without the SDK installed.

## Features

- **Assembly Reference Rewriting** — Rewrites `System.Private.CoreLib` references to SDK reference assemblies (`System.Runtime`, `System.Collections`, etc.). Handles generics, nested types, method specs, custom attributes, and all ECMA-335 metadata tables.
- **Single-File Bundling** — Creates single-file .NET executables. Automatically selects the SDK bundler when available, falls back to a built-in byte-patching bundler.
- **App Host Generation** — Generates standalone executable wrappers around .NET DLLs with proper runtime configuration.

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
// Force the built-in bundler (no SDK required)
var bundler = BundlerFactory.GetBundler(BundlerMode.BuiltIn);
var result = bundler.CreateSingleFileExecutable("myapp.dll", "myapp.exe", "myapp");
```

## License

See [LICENSE](LICENSE) for details.
