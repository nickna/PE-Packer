# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

The solution file is `PE-Packer.slnx` (the new XML solution format — pass it explicitly to `dotnet`). Targets **net10.0**; the SDK must be 10.0.x.

```bash
dotnet build PE-Packer.slnx --configuration Release
dotnet test PE-Packer.slnx --configuration Release          # all tests
dotnet pack src/PEPacker/PEPacker.csproj --configuration Release   # NuGet package (NickNa.PEPacker)
```

Run a single test / class / trait with xUnit's filter syntax:

```bash
dotnet test PE-Packer.slnx --filter "FullyQualifiedName~AssemblyReferenceRewriterILTests"
dotnet test PE-Packer.slnx --filter "DisplayName~Rewrite_PreservesEntryPoint"
```

### Native AOT smoke host

`tests/PEPacker.AotSmoke` drives the full pipeline from inside a published native binary. The
xUnit suite can never do this — it loads emitted assemblies in-process, so it must stay managed,
which makes the whole "works under JIT, breaks under AOT" class invisible to it.

```bash
dotnet publish tests/PEPacker.AotSmoke/PEPacker.AotSmoke.csproj -c Release -r <rid>
./tests/PEPacker.AotSmoke/bin/Release/net10.0/<rid>/publish/PEPacker.AotSmoke
```

It prints one line per check and exits non-zero if a required step failed. On Windows,
`vswhere.exe` must be on `PATH` or ILC's link step fails with `MSB3073`; prepend
`C:\Program Files (x86)\Microsoft Visual Studio\Installer`. On Linux it needs `clang` and
`zlib1g-dev`.

### Regenerating the embedded reference index

`src/PEPacker/Resources/refindex-net10.0.bin` is the precomputed framework type map behind
`EmbeddedReferenceAssemblyIndex` (31.8 KB, 4884 types, 167 assemblies). Regenerate it from a
reference pack — not a shared framework, which forwards to `System.Private.CoreLib` instead of
defining types:

```bash
dotnet run --project tools/PEPacker.RefIndexGen -- \
  "<dotnet-root>/packs/Microsoft.NETCore.App.Ref/<version>/ref/net10.0" \
  src/PEPacker/Resources/refindex-net10.0.bin
```

`Write` is byte-for-byte reproducible for the same input, so regenerating against the same pack
produces no diff. The tool verifies its own output round-trips identically before exiting.

CI (`.github/workflows/ci.yml`) runs build + test on push/PR to `master`. Tagging `v*` triggers `publish.yml`, which packs and pushes to NuGet — the tag suffix becomes the package version (`v1.2.3` → `1.2.3`), overriding the `Version` in `Directory.Build.props`.

## Architecture

Two independent capabilities live in one library (namespace `PEPacker`):

### 1. Assembly reference rewriting — `AssemblyReferenceRewriter`

The core and the hard part. Rewrites `System.Private.CoreLib` references in a compiled PE to the official SDK reference assemblies (`System.Runtime`, `System.Collections`, …). It does **not** decompile/recompile — it reads the source metadata with `MetadataReader` and rebuilds an entirely new metadata image with `MetadataBuilder`, patching every token along the way. Motivating use case: assemblies produced via `Reflection.Emit` / `PersistedAssemblyBuilder` that reference the runtime's internal CoreLib and therefore won't load portably.

The class is `partial`, split by concern across `AssemblyReferenceRewriter.*.cs`:
- `.cs` — fields, ctor, the `Rewrite()` phase pipeline, `Save()`, PE-header reproduction.
- `.Validation.cs` — `ValidateSupportedMetadata()`; `SupportedTables` is an **allow-list** of ECMA-335 tables the rewriter reproduces. Anything outside it is rejected (fail-closed) rather than silently dropped. **When you add support for a new table, add it here too.**
- `.Assembly.cs`, `.Types.cs`, `.Members.cs`, `.Signatures.cs`, `.IL.cs`, `.Helpers.cs` — copy logic per table group, signature blob rewriting, and IL body/token patching.

Key invariants to preserve when editing:
- **`Rewrite()` is an ordered phase pipeline** (see the numbered comments). Order is load-bearing: rows are copied before the tables that reference them, definition handles are *predicted* (row numbers reserved) before mutually-dependent tables are emitted, and standalone signatures are copied before IL so `calli` tokens map correctly. Don't reorder phases casually.
- **Handle remapping is fail-closed.** `MapEntityHandle` (in `.Helpers.cs`) throws on an unmapped handle rather than falling back to the source handle. A silent fallback previously kept stale row numbers and was the source of a whole class of bugs — keep it strict.
- **Metadata-sorted tables** (`Constant`, `FieldMarshal`, `GenericParam`) are sorted by a parent coded index that spans multiple tables, so copy order ≠ emission order. Rows are gathered into lists during copy and emitted in sort order by the `EmitSorted*` methods.
- `ILOperandTable.cs` maps each IL opcode to its operand length and whether the operand is a metadata token needing remapping — the driver for IL patching.

### 2. Single-file bundling — `PEPacker.Bundling` + `AppHostGenerator`

Packs a managed DLL into a self-contained `.exe` using the .NET apphost. `AppHostGenerator` is a thin facade; the real work is behind `IBundler`:
- `SdkBundler` — reflects into the SDK's `Microsoft.NET.HostModel.dll` `Bundler` type (detected/loaded by `SdkBundlerDetector`).
- `ManualBundler` — byte-patches the apphost template directly, avoiding a dependency on `Microsoft.NET.HostModel`. Relies on known placeholder byte sequences (bundle-header SHA and DLL-path SHA) baked into the apphost. Note it still needs an installed SDK: `FindAppHostTemplateWithVersion()` locates the apphost in the `Microsoft.NETCore.App.Host.<rid>` pack, and both bundlers call it.
- `FallbackBundler` — tries primary, falls back to secondary on failure.
- `BundlerFactory` — picks SDK-with-fallback when available, else built-in; caches the instance. `BundlerMode` (`Auto`/`Sdk`/`BuiltIn`) forces a choice.

## Testing approach

The rewriter is verified two independent ways (both in `tests/PEPacker.Tests/Infrastructure/`), because "the output still loads" has repeatedly hidden real corruption:
- **`MetadataDiffer`** — per-table, per-row comparison of before/after, asserting nothing changed beyond the intended retargeting. Catches omitted/mis-ordered rows as a *class*.
- **`ILVerifyHarness`** — runs `Microsoft.ILVerification` over the output to prove it's well-formed by an independent implementation (e.g. correct MaxStack), which the differ can't check since byte-identical IL can still be invalid.

`PEPacker.Tests` has `InternalsVisibleTo` access, so tests can reach internals like `SupportedTables`.

### Native AOT: what is settled

PE-Packer works inside a Native AOT host — verified end to end on win-arm64 and linux-x64
(the CI `aot-smoke` job). Facts worth not rediscovering:

- **`Assembly.LoadFrom` fails under AOT regardless of what is installed.** `SdkBundlerDetector`
  catches it and reports unavailable, so `BundlerFactory` selects `ManualBundler`. Measured with
  four SDKs present: it found `Microsoft.NET.HostModel.dll` on disk and still could not load it.
  "Install the SDK" is never the right advice for that failure.
- **`RuntimeEnvironment.GetRuntimeDirectory()` returns the application's own directory** under
  AOT, on both Windows and Linux — not the empty string, so nothing looks wrong. Never use it to
  locate reference assemblies. Use `EmbeddedReferenceAssemblyIndex` or an explicit path.
- **`Environment.Version` reports the ILCompiler runtime pack** the tool was built against. It is
  a build-machine artifact, which is why `RuntimeConfig` never emits a patch component.
- **The analyzer surface is ratcheted at zero.** CI fails on any new `IL####` warning. Suppress
  only with `[UnconditionalSuppressMessage]` and a justification that says why it is inapplicable.

The remaining SDK dependency is the apphost template (issue #14). `BundleRequest.AppHostTemplatePath`
is the escape hatch, and both the smoke host and the bundling tests use it.

**Recurring bug class:** framework and pack directory names sorted as strings put `9.0.17` and
`10.0.9` above `10.0.10`. This has been fixed three separate times (the original runtimeconfig
pinning, the smoke host, and a test helper). Parse to `Version` before comparing.

## Conventions

`Directory.Build.props` applies repo-wide: `Nullable` enabled, `ImplicitUsings` enabled, `LangVersion latest`, and a single shared `Version`. Throw `PEPackerException` for library-level failures.

`AssemblyIdentity` overrides record equality on purpose: `ImmutableArray<T>` compares by reference,
so the generated equality reported identical identities as unequal. Keep the by-value override.
