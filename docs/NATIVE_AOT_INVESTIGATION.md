# NativeAOT Investigation — findings

Branch: `investigate/native-aot`
Date: 2026-06-08
Host: macOS 15 (Darwin 25.5.0), Apple Silicon (arm64), .NET SDK 10.0.300

Update: 2026-06-13 — Release builds now default `EnableScripting=false`, so
`Excise.App` excludes `Services/ScriptingService.cs` and the
`Microsoft.CodeAnalysis.CSharp.Scripting` package unless a builder explicitly
opts back in with `-p:EnableScripting=true`. Debug/test builds continue to keep
the scripting surface enabled.

Update: 2026-07-04 — Native AOT is now a tracked release lane under milestone
`v2.26.0 - Native AOT Release Lane` (#590-#595). Use
`scripts/release-smoke.sh --quick --only=aot` or `scripts/run-aot-smoke.sh`
for current evidence. The current gate publishes/packages AOT, captures warning
output, splits `.dSYM`/`.pdb` symbols from the user-facing macOS artifact, and
optionally runs packaged GUI smoke with `scripts/run-aot-smoke.sh --gui-smoke`.

> **Status: historical investigation plus current release-lane pointer.** The
> measurements below explain the original decision. Current release readiness is
> determined by #590-#595 and the generated `aot-smoke.json` evidence, not by
> these older measurements alone.

## TL;DR

The long-standing assumption (recorded in `Excise.App.csproj` and tied to issues
**#342 / #343**) that NativeAOT is *blocked* by the Roslyn scripting engine and
ReactiveUI reflection **does not hold as a hard block**.

With **zero source changes**, on this branch:

- `dotnet publish -p:PublishAot=true` **compiled the full GUI** to a 32 MB native
  arm64 Mach-O binary — **0 errors**, 22 analysis warnings.
- The AOT binary **launches and runs**: main window created, ReactiveUI
  configured on the Avalonia scheduler, settings JSON read/written, no runtime
  exceptions in a smoke test.
- Roslyn was **trimmed away automatically** — it has no production call site, so
  the trimmer dropped all of `Microsoft.CodeAnalysis.*` (~29 MB).

This was a smoke test, not a functional sign-off. The warnings below are real
correctness risks that must be closed before AOT could ship.

## Measured improvement (this machine)

| Metric | Baseline (Release R2R, self-contained) | NativeAOT | Delta |
|---|---|---|---|
| Distributable size | 206 MB | ~51 MB (binary 32 MB + Skia/HarfBuzz/AvaloniaNative native libs) | **−75%** |
| Cold start → window (warm OS cache, avg of 3) | ~495 ms (451–539, JIT variance) | ~356 ms (354–358, near-zero variance) | **~30% faster, far more consistent** |
| RSS after window shown | ~228 MB | ~152 MB | **−33%** |

Notes:
- Trimming *alone* (no AOT) already takes 206 MB → **87 MB** and removes Roslyn.
- The 92 MB `Excise.App.dSYM` produced by the AOT build is debug symbols and is
  **not** part of the distributable.
- First-ever R2R launch measured 26.7 s — that is one-time Gatekeeper
  verification of the unsigned binary, not startup cost; discarded.
- Startup measured by polling `System Events` for the app's first window.

### Reality check on "as quick as possible"

AOT's wins here are **cold-start latency, memory, and download size** — all real
and user-visible. It does **not** materially speed up the heavy work (page
render is SkiaSharp native; redaction is tight C# loops). Those already get
steady-state optimization from the Release config's ReadyToRun + TieredPGO, and
AOT trades peak throughput (no tiered re-JIT) for instant startup. If the goal is
"launches instantly and is lean," AOT delivers; if the goal is "renders faster,"
the existing R2R build is already near-optimal.

## What the two "blockers" actually are

### Roslyn (`Microsoft.CodeAnalysis.CSharp.Scripting`, ~29 MB)

Powers a `.csx` C# scripting/automation surface (`ScriptingService.cs`,
`MainWindowViewModel.Scripting.cs`). **Zero production call sites** — it is only
exercised by tests (3 test files + 10 automation scripts driving end-to-end
redaction). Because it's unreachable from the GUI entry point, the trimmer
already removes it. It cannot be AOT-compiled (its job is compiling C# at
runtime), so the first path forward was to **remove it from the shipped app by
default** while keeping the developer/test automation surface opt-in. That step
is now implemented with the `EnableScripting` MSBuild property:

- Debug/test builds default to `EnableScripting=true`.
- Release builds default to `EnableScripting=false`.
- Release builders can opt in with `-p:EnableScripting=true`.

### ReactiveUI

Used shallowly: 6 `ReactiveObject`s, 57 `RaiseAndSetIfChanged`, 51
`ReactiveCommand`, and exactly **one** `WhenAnyValue`
(`SaveRedactedVersionDialogViewModel.cs:67`). None of the reflection-heavy
patterns (`ToProperty`/OAPH, Interactions, `WhenActivated`,
`ReactiveWindow<T>`, DynamicData) are used. It compiled and ran under AOT as-is;
the single `WhenAnyValue` is the only AOT-fragile site (IL2026/IL3050).

## Warnings to close before shipping AOT (impact of code changes)

The original June 2026 probe produced 22 warnings in **three small buckets**:

1. **System.Text.Json reflection (IL2026 + IL3050)** — 4 sites: `WindowSettings`
   (load/save), `RecentFilesService` (load/save), `AboutWindowViewModel`
   (manifest). Reflection-based (de)serialization is unsupported under AOT and
   can throw `NotSupportedException` at runtime (it happened to survive the smoke
   test because the types are simple and rooted — fragile, not safe).
   **Status:** fixed. The app now uses `Excise.App/Services/ExciseJsonContext.cs`
   for persisted window settings, recent files, the license manifest, and
   responsiveness reports.

2. **ReactiveUI `WhenAnyValue` (IL2026/IL3050)** — 1 site.
   **Status:** fixed in the product code path by replacing the fragile
   expression-based `WhenAnyValue` usage with explicit can-execute state.

3. **Third-party assembly warnings (IL2104/IL3053)** — current remaining buckets
   are `ReactiveUI.Avalonia`, `FluentAvalonia`, `Avalonia.Controls.DataGrid`,
   and `CSJ2K`. These are the real release-readiness risks: the gate records the
   warning budget, and full functional QA under AOT must cover open PDF, search,
   redact/save, preferences, About/license surfaces, DataGrid-backed views, and
   image/codecs paths.

## Suggested staged path

1. Keep the ReadyToRun build as the default/fallback release artifact.
2. Run `scripts/release-smoke.sh --quick --only=aot` to publish/package the AOT
   lane and record warning/size/symbol evidence.
3. Run `scripts/run-aot-smoke.sh --gui-smoke` on an interactive macOS runner
   before shipping an AOT app bundle.
4. Close #591-#595 only when the publish profile, GUI smoke, warning budget,
   symbol split, and RID rollout decisions are validated.

## Reproduce

```bash
git checkout investigate/native-aot
# baseline R2R
dotnet publish Excise.App/Excise.App.csproj -c Release -r osx-arm64 --self-contained true -o artifacts/macos-publish
# trim probe
dotnet publish Excise.App/Excise.App.csproj -c Release -r osx-arm64 --self-contained true -p:PublishTrimmed=true -o artifacts/trim-probe
# AOT probe
dotnet publish Excise.App/Excise.App.csproj -c Release -r osx-arm64 -p:PublishAot=true -o artifacts/aot-probe
```
