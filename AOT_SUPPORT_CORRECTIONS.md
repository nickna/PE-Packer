# Verification of AOT_SUPPORT_PLAN.md — results and corrections

_2026-07-29. Every source-checkable claim in `AOT_SUPPORT_PLAN.md` was
re-verified against this repo at `master` (`7da4b45`) and, for the cross-repo
claims, against SharpTS at `1835677f`. Companion document:
`docs/plans/native-aot.md` in the SharpTS repo (the consolidated two-SKU
plan)._

## Verdict

**All 11 source-checkable claims are CONFIRMED verbatim**, at the exact lines
cited:

1. Both `GenerateRuntimeConfigJson(Version _)` overloads discard the parameter,
   read `Environment.Version`, emit `Major.Minor.Build`, no `rollForward`
   (`ManualBundler.cs:211-228`, `SdkBundler.cs:413-430`).
2. The rewriter ctor eagerly scans `refAssemblyPath` via `MetadataLoadContext`
   with `GetTypes()`/`GetForwardedTypes()` (`AssemblyReferenceRewriter.cs:91-108,113-188`).
3. Only `System.Private.CoreLib`-scoped types are redirected
   (`AssemblyReferenceRewriter.Assembly.cs:71`, `...Types.cs:27`); others copy
   verbatim.
4. `Assembly.cs:121` hardcodes skipping the `"SharpTS"` AssemblyRef;
   `Types.cs:36` is a bare `_assemblyRefMap[oldAsmRef]` indexer →
   `KeyNotFoundException` for a SharpTS-scoped TypeRef.
5. `FindAppHostTemplateWithVersion()` requires an installed
   `Microsoft.NETCore.App.Host.<rid>` pack (`ManualBundler.cs:233-279`).
6. `SdkBundlerDetector.DetectSdk` catches the `Assembly.LoadFrom` failure
   (`SdkBundlerDetector.cs:40-55`); `Auto` falls through to `ManualBundler`;
   `GetBundler(Sdk)` (`BundlerFactory.cs:68-72`) misattributes the AOT case to
   a missing SDK.
7. The bundle manifest zeroes the deps.json slot and embeds exactly one
   assembly + one runtimeconfig (`ManualBundler.cs:126-153`).
8. `PEPacker.csproj:12` references `System.Reflection.MetadataLoadContext`
   **9.0.0** from a `net10.0` project.
9. The IL3050 site is `SdkBundler.cs:311`
   (`typeof(List<>).MakeGenericType(fileSpecType)`).
10. No Mach-O / codesigning handling exists anywhere in `Bundling/`
    (`SdkBundler.cs:278` passes `macosCodesign: false` to the reflected SDK
    bundler); `AOT_SUPPORT_REVIEW.md` findings 3/5 and priority 7 say what the
    plan attributes to them.
11. `MetadataRoundTripTests.cs:400` passes
    `RuntimeEnvironment.GetRuntimeDirectory()` as `refAssemblyPath` — the one
    usage pattern that is actively wrong under AOT (returns the publish dir,
    not `""`).

The SharpTS-side claim was also confirmed: SharpTS has an MLC-based reference
loader at `Compilation/AssemblyReferenceLoader.cs:12,43`, supporting the plan's
re-diagnosis of `-r` (the wall is MLC-types-into-`TypeBuilder`,
`ILCompiler.cs:426-431` — a pre-existing JIT-era limitation that AOT makes
unrecoverable, not `Assembly.LoadFrom` per se).

## Corrections and additions

### 1. Version skew — resolve before landing Phase A

This checkout is **1.0.0** (`Directory.Build.props:4`), but SharpTS pins
`NickNa.PEPacker` **1.0.3** (`Directory.Packages.props:22`) and its history
records a 1.0.3 upgrade commit. Either this working copy is behind the
published package or releases were cut elsewhere. Re-confirm the cited line
numbers against whatever source actually produced 1.0.3 before landing fixes —
commit `77cde73` ("Correct stale claims about rewriter scope and bundler
requirements") shows this area is moving.

### 2. The referenced SharpTS plan has verified corrections that affect shared framing

`AOT_SUPPORT_PLAN.md` cites `native-aot-variant.md` in several places; that
document was itself verified and corrected (full list in SharpTS
`docs/plans/native-aot.md`). The ones relevant here:

- The SharpTS ILCompiler pipeline is **10 phases (12 for the module path)**,
  not 8 — the "unexplored phases" risk shared by both plans is larger than
  stated.
- SharpTS's `MakeGenericType` exposure is **~118 sites** (60 bypassing the
  TypeProvider chokepoint), not ~21+13 — relevant to open question 5 (whether
  ILC defaults survive SharpTS's larger closure).
- `Examples/test-examples.ps1` invokes `dotnet run --`, not a binary; the
  joint smoke job needs a `-SharpTSExe` parameter added on the SharpTS side
  first. The "same shape as the Examples smoke job" reference in Phase D
  inherits this prerequisite.
- SharpTS's own DLL-path runtimeconfig is **not** affected by defect 1: 
  `Program.cs:1024` hardcodes `10.0.0`, which default-rolls-forward correctly.
  Defect 1 is PE-Packer-only — and is a live JIT bug in every bundle produced
  today, which is the argument for landing it now regardless of AOT.

### 3. Cross-repo ordering constraints (now recorded in the SharpTS plan)

- **`ReferenceAction` (Phase B) must land before SharpTS Phase 3 item 8.**
  SharpTS intends to embed SharpTS.dll and restore 14 soft-dependency features
  whose emitted programs genuinely reference SharpTS — the exact AssemblyRef
  `Assembly.cs:121` erases, currently dying via the bare indexer at
  `Types.cs:36`. The plan's defect 5 is correct; this note fixes its priority:
  it is on the SharpTS critical path, not merely hygiene.
- **Open question 1 (multi-assembly bundle vs zeroed deps.json) is promoted
  into the joint gate probe** SharpTS runs before committing to its Phase 2 —
  alongside SharpTS's phases-2–9 question and this plan's own open question 5
  (PE-Packer inside SharpTS's `TrimMode=partial` closure). One throwaway
  branch, ~4–5 days, answers all three.

### 4. Confirmation of the "Decided" premise

The two-SKU decision recorded in the Recommendation section is reflected as
the governing decision in SharpTS `docs/plans/native-aot.md`. Consequences for
this repo stand as written: no "AOT mode" API (runtime capability detection +
`BundlerMode` suffice), and both hosts must stay green — the SDK bundler keeps
working under JIT for the managed SKU while being feature-switched out of the
native one, with the switch set in the consumer's publish, not in
`PEPacker.csproj`.
