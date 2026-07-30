# PE-Packer and SharpTS AOT Support Review

_Reviewed July 28, 2026_

## Recommendation

Native AOT is worth pursuing, but as a new publishing backend—not another `BundlerMode`. The existing strategy/facade design is reasonable; the implementations and request model need hardening first.

There are two separate meanings of “AOT support”:

- **PEPacker inside a Native AOT application:** mostly plausible for the metadata and manual-bundling paths. AOT analysis currently builds clean. The SDK bundler path is incompatible because it dynamically loads `Microsoft.NET.HostModel` with `Assembly.LoadFrom` in [`SdkBundlerDetector.cs`](src/PEPacker/Bundling/SdkBundlerDetector.cs), while Native AOT does not support dynamic assembly loading.
- **Producing Native AOT SharpTS programs:** feasible for a useful subset, but requires an SDK publish phase and SharpTS runtime changes. Native AOT forbids runtime code generation, so the SharpTS compiler itself cannot be AOT-published while retaining its `Reflection.Emit` compiler. The normal SharpTS compiler should produce the native program. [Microsoft’s Native AOT guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/) confirms these restrictions.

## A Practical AOT Design

Introduce an `INativeAotPublisher` or `IExecutablePublisher`, separate from `IBundler`.

The pipeline would be:

1. SharpTS emits its normal managed DLL, automatically using reference-compatible assembly identities.
2. Emit a stable, C#-addressable entry point such as `SharpTS.Generated.EntryPoint.Run(string[] args)`. The current `$Program.Main` in [`ILCompiler.cs`](../SharpTS/Compilation/ILCompiler.cs) is public, but `$Program` is awkward for a strongly typed bootstrap.
3. Generate a temporary SDK driver project that statically references the generated DLL and its known dependencies.
4. Run `dotnet publish -c Release -r <RID> -p:PublishAot=true`.
5. Treat trimming/AOT warnings as errors and return structured diagnostics and all produced artifacts.
6. Test and publish separately for each supported OS/RID; Native AOT output is platform- and architecture-specific.

A conservative first version could root the generated assembly with `TrimmerRootAssembly`. Longer term, replace reflection dispatch with direct calls or generated `DynamicallyAccessedMembers` contracts, following the official [trim-warning guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/fixing-warnings).

AOT mode should initially reject or explicitly downgrade features that late-bind into `SharpTS.dll`: `eval`, `vm`, Proxy, portions of Intl/DNS, workers/cluster, `child_process.fork`, and dynamic .NET event binding. SharpTS already tracks these dependencies in [`Program.cs`](../SharpTS/Program.cs), which is an excellent foundation for AOT compatibility diagnostics. Some features can later move into a small AOT-compatible runtime package; `eval` and similar runtime-compilation features are fundamentally incompatible.

Estimated scope:

- Feasibility spike with hello-world, async, classes, and modules: roughly 2–3 days.
- Useful BCL-only MVP for Windows/Linux x64: roughly 2–4 weeks.
- Broad interop and multi-RID support: ongoing work; full feature parity is not realistic because some features are inherently dynamic.

If the goal is primarily faster startup, consider a ReadyToRun publish backend first. It has substantially fewer compatibility constraints.

## Architectural Findings

The strategy pattern holds up, but the assembly rewriter does not yet match its advertised generality.

### 1. The Rewriter Is Safe Only for a Constrained SharpTS Metadata Shape

It rebuilds the PE from scratch but copies only a subset of metadata. Properties, events, method semantics, P/Invoke/module references, class layout, resources, exported types, declarative security, parameter constants/marshalling, and several other tables are omitted. Module-scoped type references are silently changed to nil in [`AssemblyReferenceRewriter.Types.cs`](src/PEPacker/AssemblyReferenceRewriter.Types.cs).

It also always uses `CreateExecutableHeader`, even when rewriting a library, and loses source PE characteristics, CorFlags, subsystem, debug directory, resources, and signing data in [`AssemblyReferenceRewriter.cs`](src/PEPacker/AssemblyReferenceRewriter.cs).

Recommendation: either:

- Explicitly narrow the API to “assemblies emitted by SharpTS/PersistedAssemblyBuilder,” validate the supported table set, and fail on anything else; or
- Implement a complete metadata and PE copier before claiming arbitrary ECMA-335 support.

The README’s “all ECMA-335 metadata tables” claim is currently too broad.

### 2. The IL Decoder Has Important Untested Cases

`switch` is marked variable-length but never actually skipped, causing subsequent operand bytes to be interpreted as opcodes in [`AssemblyReferenceRewriter.IL.cs`](src/PEPacker/AssemblyReferenceRewriter.IL.cs). Token-bearing `ldelem`/`stelem` forms are also absent from the token list. `calli` signatures can be mapped before standalone signatures have received their final rows.

Use a table-driven operand decoder—preferably the metadata library’s opcode operand classification—and pre-create all standalone-signature mappings before copying method bodies.

### 3. SharpTS-Specific Policy Leaks into a Generic Package

The hard-coded removal of the `SharpTS` reference in [`AssemblyReferenceRewriter.Assembly.cs`](src/PEPacker/AssemblyReferenceRewriter.Assembly.cs) makes the generic-looking rewriter application-specific.

Make reference removal/redirection an injected policy and have unknown or unmapped handles fail rather than silently retaining old row numbers.

### 4. Bundling Needs a Richer Request Model

The current `IBundler` accepts only one DLL, an output path, and a name in [`IBundler.cs`](src/PEPacker/Bundling/IBundler.cs). That cannot model dependencies, `.deps.json`, target RID, framework version, apphost source, symbols, native assets, compression, cancellation, or AOT.

Introduce a request object containing at least:

- Entry assembly and all deployment assets
- Output path and overwrite policy
- RID, TFM, and framework minimum version
- Runtime/deps configuration paths
- Apphost template
- Symbols/compression/self-contained options
- Cancellation, progress, and diagnostics

The current bundle is framework-dependent—it still requires an installed .NET runtime. Single-file and self-contained are distinct deployment choices in the [official single-file documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview).

### 5. Manual Bundling Has Portability Risks

The runtime configuration is derived from `Environment.Version` in [`ManualBundler.cs`](src/PEPacker/Bundling/ManualBundler.cs), potentially requiring the build machine’s exact patch version. It should come from an explicit target framework/minimum version.

The implementation only targets the current RID, omits dependency assets, buffers the entire output in memory, and has no atomic-output behavior. On macOS it also lacks the Mach-O header adjustment and ad-hoc signing performed by the [official HostModel bundler](https://github.com/dotnet/runtime/blob/main/src/installer/managed/Microsoft.NET.HostModel/Bundle/Bundler.cs). Guard the manual implementation as Windows/Linux-only until macOS is implemented and tested.

The “works without the SDK” wording is also misleading because it still searches installed host packs.

## Priority Order

1. Fix the IL decoder and add fail-fast validation for unsupported metadata.
2. Preserve PE/header semantics and either complete or explicitly constrain the rewriter.
3. Replace `IBundler`’s positional API with a deployment request; add dependencies, RID, and correct runtime configuration.
4. Add cross-platform end-to-end tests for both bundlers.
5. Run the Native AOT feasibility spike using a stable SharpTS entry point.
6. Add an AOT feature-compatibility matrix and structured diagnostics.
7. Split the SDK reflection adapter from an `IsAotCompatible` core package.

## Testing Recommendations

The test suite currently passes—11/11—and PEPacker builds cleanly with the AOT compatibility analyzers enabled. However, only one test meaningfully exercises rewriting.

Before expanding the feature surface, add fixtures covering:

- `switch`, `calli`, and token-bearing `ldelem`/`stelem`
- Properties, events, and method semantics
- Generic-parameter and parameter attributes
- P/Invoke and module references
- Managed resources and exported types
- Explicit/sequential layout and RVA data
- Nested and forwarded types
- Strong-name signing, PE characteristics, and PDB preservation
- Varargs, function pointers, and custom modifiers
- Exception filters and large exception-handler sections

Run ILVerify and execute the rewritten assembly for every fixture. Add Windows, Linux, and macOS x64/Arm64 smoke tests that create, inspect, and execute bundles using each supported implementation.

## Conclusion

The concept is sound and the SharpTS integration has good seams, especially its existing feature tracking. The main risk is that a narrowly successful post-processor currently presents itself as a general PE round-tripper.

Tightening that contract and test matrix will make both current behavior and an eventual AOT backend much safer.
