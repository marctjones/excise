// Excise.Rendering.Tests runs with 4-way xUnit collection parallelism
// (xunit.runner.json: parallelizeTestCollections=true, maxParallelThreads=4).
//
// This is DELIBERATE and empirically validated — do not silently revert to
// serial. See #731.
//
// Why it is safe here but NOT in Excise.App.Tests (which is serialized for the
// #363 native-font-manager crash): SkiaSharp's font subsystem reaches a
// *process-wide* native font manager that corrupts/crashes the host under
// concurrent typeface creation. In Excise.Rendering that hazard is fully
// contained — every typeface-acquisition path (SKTypeface.FromData,
// FromFamilyName, and the SKTypeface.Default fallbacks) is serialized behind the
// process-wide `_typefaceLoadLock` in SkiaRenderer (see SkiaRenderer.cs:259 and
// SkiaRenderer.Text.cs:431/1310/1321). These tests drive SkiaRenderer directly
// and never touch Avalonia's headless font stack, so — unlike App.Tests, whose
// crash comes from Avalonia's *own* native font paths that the renderer lock
// does not cover — there is no unguarded concurrent native-font access.
//
// Empirical proof (#731, 10-core dev box, external reference tools present so the
// text-measurement-heavy differential suites actually run): full suite serial =
// 243s, 4-way = 162s (33% faster), identical outcomes (3553 passed / 2 skipped,
// zero failures, zero native crash/OOM), across the same 4-way concurrency that
// reliably killed App.Tests at ~640 tests. CI runs this suite with those tools
// ABSENT (differential tests skip), i.e. strictly lower font-op density than the
// validated local run, so local-with-tools is the stress case.
//
// >>> If this suite ever dies with a native "Test host process crashed" (blank
// >>> blame, abrupt native death), REVERT this file + the xunit.runner.json
// >>> parallelism FIRST — that is the #363 signature and the most likely cause.
//
// The ±1 discovery wobble seen between runs is unrelated: it is pre-existing
// nondeterminism in external-tool/fixture-gated [Fact(Timeout=...)] tests
// (e.g. Type3_PopplerFixture_MatchesLivePdftocairo,
// RenderPage_PdfjsS2_JpxSoftMasksClearImageBackgrounds) whose skip/run decision
// depends on external availability, not a parallelism coverage loss — serial
// runs drop cases too. Tracked as a note on #731.
