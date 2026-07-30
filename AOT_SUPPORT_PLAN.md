# What PE-Packer needs for the SharpTS Native AOT variant

_Written July 29, 2026, against `D:\native-aot-variant.md` (SharpTS AOT feasibility plan)._

Measurements tagged **[M]** were taken by publishing a real Native AOT probe that
references `PEPacker.csproj` and drives the full pipeline from inside the native
process. **[I]** means inferred from code without an experiment.

Probe environment: Windows ARM64, SDK 10.0.400-preview.0.26322.102, ILCompiler
runtime pack 10.0.9, `-r win-arm64`, default trim settings, 4.16 MB native exe.
Scripts are in the session scratchpad (`aotprobe/`, `refdirprobe/`).

## The headline finding

**PE-Packer already works under Native AOT.** Both capabilities — metadata
rewriting and single-file bundling — ran to completion inside a native binary with
`RuntimeFeature.IsDynamicCodeSupported == false`, and the executable they produced
ran and printed correct output. **[M]**

The probe emitted an assembly with `PersistedAssemblyBuilder`, rewrote its
`System.Private.CoreLib` references, bundled the result into a single-file exe, and
executed it:

```
=== 3. Reflection.Emit: PersistedAssemblyBuilder under AOT ===
  OK  emit ProbeApp.dll (entry point, CoreLib refs): 2560 bytes
  OK  fixture references: System.Console, System.Private.CoreLib
=== 4. AssemblyReferenceRewriter under AOT (MetadataLoadContext is the risk) ===
  OK  rewrite via shared framework dir: 2560 bytes
  OK  rewritten references: System.Collections, System.Console, System.Runtime
=== 5. ManualBundler under AOT: produce a single-file exe ===
  OK  AppHostGenerator.CreateSingleFileExecutable: built-in bundler -> 142129 bytes
=== 6. Run the produced exe ===
  OK  execute ProbeApp.exe: exit=0 stdout=[PROBE_APP_RAN counter=42] stderr=[]
```

Two things that looked like they should have broken did not:

- **`System.Reflection.MetadataLoadContext` works under Native AOT.** **[M]** The
  rewriter's `BuildTypeToAssemblyMapping` (`AssemblyReferenceRewriter.cs:113-188`)
  loads 185 framework assemblies through `MetadataLoadContext` and calls
  `GetTypes()` and `GetForwardedTypes()` on each. This is inspection-only
  reflection implemented over `System.Reflection.Metadata`, so it never asks the
  runtime to load anything. It emits assembly-level `IL2104`/`IL3053` rollups and
  keeps working.
- **The SDK bundler's `Assembly.LoadFrom` degrades gracefully rather than
  crashing.** **[M]** `SdkBundlerDetector.DetectSdk` already wraps the load in
  `try`/`catch` (`SdkBundlerDetector.cs:40-55`), so under AOT it returns
  `IsAvailable: false` even though it found the DLL on disk, and
  `BundlerFactory.GetBundler(Auto)` selects `ManualBundler` — which then works.

So there is no port and no rewrite here. What follows is a list of five real
defects, all of which are small, and none of which are architectural.

## Corrections to the SharpTS plan

`native-aot-variant.md:176` says:

> `--target exe` (PEPacker) | n/a | **broken** | `SdkBundler` does
> `Assembly.LoadFrom(Microsoft.NET.HostModel)`; even `--bundler builtin` needs an
> SDK-only apphost template

Both halves need correcting, and the conclusion should flip:

1. The `Assembly.LoadFrom` is **not** a blocker. It is caught, detection reports
   unavailable, and `Auto` falls through to the built-in bundler. **[M]** The only
   user-visible fallout is that `--bundler sdk` reports the wrong reason (see
   defect 4).
2. `--bundler builtin` needing an apphost template **is** real, but it is a
   *missing-SDK* constraint, not an *AOT* constraint. With the SDK present,
   `--target exe` works from a native binary end to end. **[M]** With
   `DOTNET_ROOT` pointed at an empty directory it fails cleanly:
   `PEPackerException: Could not find apphost template. Ensure the .NET SDK is
   installed.` **[M]**

So the row should read **"works under AOT when an SDK is installed; fails with a
clean error otherwise."** That matters for scoping, because the doc's own reading-1
goal ("the toolchain needs no .NET") already concedes at line 60 that a DLL emitted
on a bare machine cannot *run* there. A bundled exe is a framework-dependent
managed bundle, so it inherits exactly that concession. Making `--target exe` work
with no SDK installed is worth doing for uniformity — a toolchain that is
"SDK-free except for one flag" is a support burden — but it does not unlock a
scenario the plan does not already write off.

### `-r foo.dll` is lost for a different reason than the plan gives

`native-aot-variant.md:173` marks `-r foo.dll` and sharpts.json `references`
**permanently lost**, attributing it to `Assembly.LoadFrom`/`LoadFile`/`Load(byte[])`
throwing `PlatformNotSupportedException`. The status is right; the diagnosis is not,
and the difference decides whether it is worth revisiting later.

Reading a third-party assembly's metadata does not require loading it into the
runtime. `MetadataLoadContext` does exactly that, it works under Native AOT
**[M]**, and SharpTS already has an MLC-based reference loader
(`Compilation/AssemblyReferenceLoader.cs:12,43`). `DotNetReferences.Resolve` is
already the no-loading path, used by the language server.

The actual wall is one layer deeper, and `Compilation/ILCompiler.cs:426-431` states
it outright: compilation *always* uses `TypeProvider.Runtime` because
"MetadataLoadContext types cannot be used with `TypeBuilder.DefineType()` for
interface implementation". So the emit path needs runtime `Type` objects, obtaining
those needs the assembly loaded in-process, and `DotNetReferences.Load:103` does
that with `Assembly.LoadFrom`. AOT forbids it.

That is the same limitation the plan already records as its own open question 5
("fails on plain JIT today"), and it is the exact reason this rewriter exists — see
this file's own class remarks at `AssemblyReferenceRewriter.cs:13-17`. So `-r` is
not lost *to AOT*; it is blocked by a JIT-era limitation that AOT happens to make
unavoidable. Suggested rewording: "blocked by the MLC-types-into-`TypeBuilder`
limitation, which AOT makes unrecoverable."

One clarification worth stating in the plan, because it is easy to assume
otherwise: a native binary cannot load a third-party DLL **even on a machine with
the full SDK installed**. Measured — the probe ran with four SDKs and seven
runtimes present, located `Microsoft.NET.HostModel.dll` on disk, and the load still
failed. **[M]** Dynamic assembly loading is absent as a property of the compilation
model, not as a consequence of a missing install, so "install the runtime" is never
the remedy.

### Other additions

Also worth adding to the plan's open-questions table: the AOT probe confirms
`PersistedAssemblyBuilder` + `GenerateMetadata` + `ManagedPEBuilder.Serialize` on
**win-arm64 with `-r win-arm64`**, and that the emitted DLL still references
`System.Private.CoreLib` under AOT — which is what makes the rewriter necessary in
the AOT SKU too, not optional. **[M]**

## The five things that actually need to change

Ranked by risk, not by effort.

### 1. The bundled `runtimeconfig.json` pins a build-machine version — `S`, do first

`ManualBundler.GenerateRuntimeConfigJson` (`ManualBundler.cs:211-228`) and
`SdkBundler.GenerateRuntimeConfigJson` (`SdkBundler.cs:413-430`) both derive the
target framework version from `Environment.Version` and emit
`Major.Minor.Build` with no `rollForward` policy.

Under Native AOT `Environment.Version` reports the version of the **ILCompiler
runtime pack that built the tool**, which has nothing to do with what is installed
anywhere. On the probe machine it reported `10.0.9` while the newest installed
runtime was `10.0.10`, and the produced bundle carried: **[M]**

```json
"runtimeOptions": { "tfm": "net10.0",
  "framework": { "name": "Microsoft.NETCore.App", "version": "10.0.9" } }
```

Under JIT this is merely wrong-ish (it pins the *running* patch). Under AOT it
freezes a number at SharpTS-release time and stamps it into every bundle every user
produces. Default roll-forward covers *higher* patches, so this fails only when the
target has an older patch than the build machine's ILC pack — a silent-until-
customer-machine failure (`You must install .NET to run this application`).

**Fix:** take the framework version from the caller (see the request object in
"Proposed shape"), default to `<major>.<minor>.0` rather than the running patch,
and emit an explicit `"rollForward": "latestMinor"`. Note both methods already
accept and discard a `Version` parameter (`GenerateRuntimeConfigJson(Version _)`),
so the plumbing is half there.

### 2. The rewriter cannot work without framework assemblies on disk — `M`

`AssemblyReferenceRewriter`'s constructor takes a `refAssemblyPath` and eagerly
scans it (`AssemblyReferenceRewriter.cs:116`). On a machine with no .NET there is
no such directory, and the AOT SKU has no way to synthesize one.

The failure is **loud, not silent** — worth confirming, because a silently empty
type map would have produced a corrupt assembly. **[M]**

| `refAssemblyPath` | Result |
|---|---|
| empty directory | `FileNotFoundException: Could not find assembly 'System.Runtime…'` (from the `MetadataLoadContext` ctor) |
| AOT publish dir (native exe, no managed DLLs) | same |
| missing directory | `DirectoryNotFoundException` |

Two problems, though:

- Neither exception is a `PEPackerException`, and neither message says "pass a
  reference-assembly directory" or "this build has no framework assemblies to
  read." Both leak out of the constructor.
- **`RuntimeEnvironment.GetRuntimeDirectory()` — which is what PE-Packer's own
  tests pass (`MetadataRoundTripTests.cs:400`) — returns the application's own
  publish directory under AOT, not `""`.** **[M]** So the usage the test suite
  models silently becomes "scan a folder with no managed assemblies in it," landing
  in row 2 of that table. (For contrast, `typeof(object).Assembly.Location` is
  `""` under AOT, as the SharpTS doc reports. **[M]**)

**Fix:** make reference resolution an injected abstraction rather than a directory
path, with two implementations — today's directory scan, and a **precomputed index
embedded in PE-Packer**.

The embedded index can be small. The rewriter only ever looks up types whose
resolution scope is `System.Private.CoreLib`
(`AssemblyReferenceRewriter.Assembly.cs:71`,
`AssemblyReferenceRewriter.Types.cs:27`), so the index needs to cover the CoreLib
public surface mapped to its owning facade, plus the identities (version + public
key token) of those facades — not the public surface of all 167 ref assemblies.
Non-CoreLib references are copied verbatim from the source and need no index at
all. Estimated a few thousand entries, tens of KB compressed; the full ref pack is
37 MB and is not a candidate for embedding. **[I — needs measuring]**

### 3. The apphost template requires an installed SDK — `M`

`ManualBundler.FindAppHostTemplateWithVersion()` (`ManualBundler.cs:233-279`) reads
`<dotnet-root>/packs/Microsoft.NETCore.App.Host.<rid>/<ver>/runtimes/<rid>/native/apphost`.
No SDK, no template, clean failure. **[M]**

**Fix:** resolve the template through a chain — explicit path from the caller →
embedded resource for the target RID → installed host pack → an error that names
all three. Embedding is cheap: **[M]**

| RID | apphost | (`singlefilehost`, for contrast) |
|---|---:|---:|
| win-x64 | 151 KB | 9.4 MB |
| win-arm64 | 132 KB | 9.9 MB |
| win-x86 | 120 KB | 8.1 MB |
| osx-x64 | 120 KB | 10.9 MB |
| linux-x64 | 76 KB | 11.4 MB |

Six RIDs is roughly 700 KB before compression, and the templates come from
`Microsoft.NETCore.App.Host.<rid>` NuGet packages, so a `PackageReference` +
`EmbeddedResource` at PE-Packer build time is all that is required. Gate the
embedded set behind an MSBuild property so consumers who do not want the payload
can opt out.

**macOS caveat:** embedding the osx apphosts is necessary but not sufficient.
`ManualBundler` does not perform the Mach-O header adjustment or ad-hoc codesigning
that the official HostModel bundler does, and arm64 macOS refuses unsigned
binaries. `AOT_SUPPORT_REVIEW.md` already flags this. Keep the built-in bundler
guarded to Windows and Linux until that is implemented, and say so in the error
rather than producing a binary the OS will kill.

### 4. `--bundler sdk` reports the wrong reason under AOT — `S`

Measured under AOT with the SDK fully installed: **[M]**

```
FAIL  BundlerFactory.GetBundler(Sdk): PEPackerException: SDK bundler is not
      available on this system. Ensure the .NET SDK is installed, or use
      BundlerMode.BuiltIn for the built-in bundler.
```

The SDK *is* installed; `DetectionResult.HostModelPath` even points at
`Microsoft.NET.HostModel.dll`. The real cause is that Native AOT cannot load it.
Telling the user to install something they already have is the kind of diagnostic
that costs an afternoon.

**Fix:** branch on `RuntimeFeature.IsDynamicCodeSupported` in
`BundlerFactory.GetBundler(BundlerMode)` (`BundlerFactory.cs:68-72`) and say "the
SDK bundler requires dynamic assembly loading and is unavailable in a Native AOT
build; the built-in bundler is used instead." Distinguish it from the genuinely
missing-SDK case, which is already distinguishable via `HostModelPath == null`.

### 5. The hardcoded `SharpTS` reference drop collides with the AOT plan — `S`

`AssemblyReferenceRewriter.Assembly.cs:121` skips copying the `SharpTS`
`AssemblyRef`:

```csharp
if (name is "System.Private.CoreLib" or "SharpTS")
    continue;
```

`CopyTypeReferences` then resolves non-CoreLib scopes through a bare dictionary
indexer (`AssemblyReferenceRewriter.Types.cs:36`, `_assemblyRefMap[oldAsmRef]`), so
an input that genuinely references a SharpTS type throws `KeyNotFoundException` —
not a `PEPackerException`, with no indication of which type or why. **[I — from the
indexer; the key is never added for a skipped reference]**

This is latent today and becomes live under the AOT plan. `native-aot-variant.md`
Phase 3 item 8 embeds `SharpTS.dll` and extracts it to `AppContext.BaseDirectory`
specifically to restore 14 soft-dep features (eval, Proxy, Intl, vm, workers…),
and line 171 notes Proxy and Intl "need SharpTS.dll" in the compile backend. The
rewriter is hardcoded to erase the reference to exactly that assembly.

**Fix:** make reference handling an injected policy — keep / drop / retarget per
assembly name — so SharpTS chooses the behavior that matches its deployment model
instead of PE-Packer assuming one. `AOT_SUPPORT_REVIEW.md` finding 3 raises the
same point on general-purpose-package grounds; the AOT plan turns it into a
correctness issue. Whichever way the policy defaults, replace that indexer with a
`TryGetValue` and a `PEPackerException` naming the type and the dropped reference.

## Warning hygiene, and the Phase 0 ratchet

Full analyzer surface, measured with
`-p:IsAotCompatible=true -p:EnableAotAnalyzer=true -p:EnableTrimAnalyzer=true
-p:EnableSingleFileAnalyzer=true -p:TrimmerSingleWarn=false`: **13 warnings, build
succeeds.** **[M]**

| Where | Count | Codes |
|---|---:|---|
| `SdkBundler.cs` | 9 | IL2026 ×2, IL2070, IL2072, IL2075, IL2080 ×2, **IL3050** |
| `SdkBundlerDetector.cs` | 2 | IL2026 ×2 |
| `AssemblyReferenceRewriter.cs` | 2 | IL2026 (`GetTypes`, `GetForwardedTypes`) |

Exactly one warning is a hard AOT incompatibility — `IL3050` at
`SdkBundler.cs:311`, `typeof(List<>).MakeGenericType(fileSpecType)` — and it is in
the path that is already dead under AOT.

This is close enough to clean to be worth ratcheting, which also matters for the
SharpTS side: PE-Packer is named in `native-aot-variant.md:370` as one of the
assemblies whose per-callsite warning count inside the `IL2104`/`IL3053` rollups
was unknown and additive to the 2730 baseline. It is 13, and it can be 0.

1. **Feature-switch the SDK bundler.** Annotate the detector with
   `[FeatureSwitchDefinition]` and ship a `RuntimeHostConfigurationOption` with
   `Trim="true"` so ILC dead-codes `SdkBundler` entirely. That removes 11 of 13
   warnings and the `Microsoft.NET.HostModel` reflection surface from the native
   image, and it is strictly better than the "split the package" option in
   `AOT_SUPPORT_REVIEW.md` priority 7 because it keeps one package.
2. **Suppress the two rewriter warnings with justification.** They are false in
   substance: `MetadataLoadContext` reads types out of files on disk, so trimming
   the host's closure cannot affect them. `[UnconditionalSuppressMessage]` with
   that reasoning — not `NoWarn`.
3. **Then set `<IsAotCompatible>true</IsAotCompatible>`** in `PEPacker.csproj` and
   fail CI on any new warning. Cheap, and it prevents an ordinary PR from silently
   reintroducing a blocker.

### Drop the `MetadataLoadContext` dependency

`PEPacker.csproj:12` references `System.Reflection.MetadataLoadContext` **9.0.0**
from a `net10.0` project — a version mismatch worth fixing on its own — and that
assembly is what produces the `IL2104` + `IL3053` rollups in every AOT consumer.
**[M]**

It is used in exactly one method for exactly three things: each assembly's
identity, its public types, and its forwarded types. All three are directly
available from `MetadataReader`, which PE-Packer already depends on
(`AssemblyDefinition`, `TypeDefinitions` filtered by `TypeAttributes.Public`,
`ExportedTypes` filtered by `IsForwarder`). The only real work is computing the
public key token by hand — SHA-1 of the public key, last eight bytes reversed —
since `AssemblyName.GetPublicKeyToken()` goes away with the dependency.

Roughly 40–60 lines to remove a dependency, two warnings, a version mismatch, and
some native image size. It also composes with defect 2: the same `MetadataReader`
walk is what generates the embedded index at build time.

## Proposed shape

Both capabilities need the same treatment: replace positional parameters with a
request object, and replace an assumption with an injected policy.

```csharp
public sealed record BundleRequest
{
    public required string EntryAssemblyPath { get; init; }
    public required string OutputPath { get; init; }
    public required string AssemblyName { get; init; }
    public IReadOnlyList<string> AdditionalAssemblies { get; init; } = [];   // defect 5
    public string? RuntimeIdentifier { get; init; }        // null = current
    public Version? FrameworkVersion { get; init; }        // null = current; NOT Environment.Version under AOT
    public RollForward RollForward { get; init; } = RollForward.LatestMinor;  // defect 1
    public string? AppHostTemplatePath { get; init; }      // defect 3
    public bool Overwrite { get; init; } = true;
    public CancellationToken CancellationToken { get; init; }
}
```

```csharp
// defect 2: the rewriter asks an index, not a directory
public interface IReferenceAssemblyIndex
{
    bool TryResolveType(string fullTypeName, out AssemblyIdentity owner);
    bool TryGetIdentity(string simpleName, out AssemblyIdentity identity);
}
// DirectoryReferenceAssemblyIndex(path)  — today's behaviour, MetadataReader-based
// EmbeddedReferenceAssemblyIndex(tfm)    — works with no framework files on disk

// defect 5: SharpTS-specific policy stops being PE-Packer's business
public enum ReferenceAction { Keep, Drop, RetargetToFacades }
```

`AdditionalAssemblies` is where the deps.json gap lands. The built-in bundler
currently writes a manifest with the deps.json slot zeroed
(`ManualBundler.cs:132-133`) and embeds exactly one assembly, so a compiled program
that needs `SharpTS.dll` beside it cannot be bundled at all. Whether a
multi-assembly bundle works without a deps.json — host policy falls back to
directory probing in some configurations — is untested here and is an open
question, not an assumption.

## Phased plan

**Phase A — safe under AOT, no API change (~2–3 days).** Defects 1 and 4, plus the
constructor-diagnostics half of defect 2 (wrap the `MetadataLoadContext` failure in
a `PEPackerException` that names the directory and explains what it needed) and the
`TryGetValue` half of defect 5. Then warning hygiene items 1–3 and
`IsAotCompatible=true` in CI. All of it is behavior-preserving on JIT and lands
independently of whether the SharpTS AOT SKU ever ships — which is the same
argument `native-aot-variant.md` makes for its own Phase 0 and Phase 1.

**Phase B — the request object and the injected policies (~1 week.)** `BundleRequest`,
`IReferenceAssemblyIndex` with the directory implementation, `ReferenceAction`.
Keep the current positional overloads delegating to the new ones so SharpTS can
migrate on its own schedule. Drop `MetadataLoadContext` here, since
`DirectoryReferenceAssemblyIndex` is the natural place for the `MetadataReader`
rewrite.

**Phase C — no SDK required (~1 week.)** Embedded apphost templates per RID behind
an opt-out property, the embedded reference index, and target-RID selection so a
win-arm64 build machine can emit a linux-x64 bundle. Gate the built-in bundler to
Windows and Linux with an explicit error on macOS.

**Phase D — tests.** The critical gap is that none of this is covered. The suite
has good differential machinery (`MetadataDiffer`, `ILVerifyHarness`) but
`MetadataRoundTripTests.cs:400` passes `RuntimeEnvironment.GetRuntimeDirectory()`,
which is the one thing that is actively wrong under AOT. Add: an
`IReferenceAssemblyIndex` fixture that does not touch the filesystem (so the
rewriter is testable with no SDK), assertions on the generated runtimeconfig
version and roll-forward policy, and a subprocess smoke job that publishes a small
native host and drives rewrite-plus-bundle-plus-execute. The probe in this
investigation is that job in embryo.

Note the `Examples/` smoke job in `native-aot-variant.md`'s testing section is
**not** directly reusable as the harness: `Examples/test-examples.ps1` invokes
`dotnet run --` rather than a built binary, so driving a published native
executable needs a `-SharpTSExe` parameter added on the SharpTS side first. The
probe from this investigation has no such dependency — it drives PE-Packer
directly — so lifting it is the faster route.

## Open questions

| # | Unknown | Risk | Cheapest experiment |
|---|---|---|---|
| 1 | Does a multi-assembly bundle work with the deps.json slot zeroed, or must the built-in bundler learn to generate one? Decides whether `--target exe` can ship SharpTS.dll alongside the compiled program at all | **highest** for the AOT SKU's feature parity | bundle two assemblies by hand, run it, then repeat with a generated deps.json. Now folded into the joint cross-repo gate probe with questions 5 and SharpTS's own phase question — ~4–5 days for all three, not half a day for this alone |
| 2 | Size of the embedded CoreLib-surface index once generated | low, but it sets the opt-out default | generate it from the 10.0.10 ref pack and measure (~2 h) |
| 3 | Only win-arm64 was probed. Linux and macOS untested; macOS additionally needs Mach-O + ad-hoc signing that `ManualBundler` does not implement | medium; macOS is the likely hard stop | re-run the probe with `-r linux-x64`, then `-r osx-arm64` (~1 h each) |
| 4 | Whether `PersistedAssemblyBuilder` output from a native host is byte-identical to its JIT output. The probe asserted the rewritten refs and the runtime behavior, not the bytes | low | run the existing `MetadataDiffer` over a native-emitted fixture (~2 h) |
| 5 | Whether ILC's default trim settings stay sufficient once SharpTS's much larger closure is present. The probe was clean at defaults, but `native-aot-variant.md:119` needs `TrimMode=partial` + `IlcTrimMetadata=false` for SharpTS's own name-based lookups — and SharpTS's `MakeGenericType` exposure is ~118 sites, 60 of them bypassing the TypeProvider chokepoint, not the ~21+13 originally stated, so that closure is larger and less funnelled than assumed | low for PE-Packer, worth confirming in situ | fold PE-Packer into the joint gate probe alongside question 1 rather than testing separately |

## Recommendation

Land **Phase A now**, regardless of what SharpTS decides about AOT. Defect 1 is a
live bug on JIT too — every bundle PE-Packer produces today pins the build
machine's exact patch version with no roll-forward — and the diagnostics work in
defects 2, 4 and 5 costs a day and turns three confusing failures into three clear
ones.

**Decided: SharpTS is going the two-SKU route** — a native AOT build for straight
TypeScript, a managed build for anything needing `-r`, `@DotNetType` or `dotnet:`.
That makes Phase C committed rather than contingent, since the native SKU is
precisely the configuration where an SDK may be absent. B and C are roughly two
weeks against a 6–10 week SharpTS estimate whose own critical path is the
`NodeRegistry` source generator and its unexplored ILCompiler phases — of which
there are **10, or 12 for the module path**, not the 8 originally stated, so that
shared risk is larger than either plan first assumed. The sequencing is still
comfortable.

Schedule #12 (`ReferenceAction`) first within Phase B. It was filed as design
hygiene but is a cross-repo ordering constraint: it must land before SharpTS
Phase 3 item 8, whose emitted programs genuinely reference `SharpTS` — the exact
`AssemblyRef` that `Assembly.cs:121` erases.

Defect 1 is also PE-Packer's alone. SharpTS's own runtimeconfig hardcodes `10.0.0`
and rolls forward correctly (`Program.cs:1024`), so it is the model to copy rather
than a shared problem to defer.

Two consequences for this repo, both already reflected in the issues:

- **No "AOT mode" API is needed.** PE-Packer already detects capability at runtime
  and degrades correctly, and `BundlerMode` already exists as a manual override.
  The SKU split happens upstream at SharpTS publish time; PE-Packer adapts to
  whichever host it is loaded into.
- **Both hosts have to stay green.** The SDK bundler must keep working under JIT
  for the managed SKU while being trimmed out of the native one. That constrains
  the feature switch (it belongs in the consumer's publish, not in
  `PEPacker.csproj`) and it doubles the test matrix.

The thing to internalize on this side: **PE-Packer is not a blocker for the AOT
variant.** The SharpTS plan lists `--target exe` as broken; it is not. What
PE-Packer actually has is one wrong version number, three bad error messages, an
apphost it cannot find without an SDK, and a hardcoded assembly name that the AOT
deployment model will collide with.
