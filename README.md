# PE Packer

PE metadata rewriting and single-file executable bundling for .NET assemblies.

## Features

- **Assembly Reference Rewriting** — Rewrites assembly references in compiled .NET assemblies to target SDK reference assemblies instead of implementation assemblies (e.g., `System.Private.CoreLib` → `System.Runtime`).
- **Single-File Bundling** — Creates single-file .NET executables using either the official SDK bundler or a built-in manual PE patcher.
- **App Host Generation** — Generates standalone executable wrappers around .NET DLLs.

## Installation

```bash
dotnet add package NickNa.PEPacker
```

## Usage

### Assembly Reference Rewriting

```csharp
using PEPacker;

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

## License

See [LICENSE](LICENSE) for details.
