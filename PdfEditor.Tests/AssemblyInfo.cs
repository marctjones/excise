using Xunit;

// Serialize the entire PdfEditor.Tests assembly — DO NOT re-enable parallelism.
//
// The headless GUI tests render with REAL Skia (TestAppBuilder sets
// UseHeadlessDrawing=false). SkiaSharp's font subsystem reaches a *process-wide*
// native font manager that corrupts/crashes the host when multiple managed
// threads create typefaces / measure text concurrently (see the
// `_typefaceLoadLock` note in SkiaRenderer — the per-renderer lock only guards
// typeface *acquisition*, not every native font path). Under xunit's default
// 4-way collection parallelism this was the root cause of the chronic #363
// "Test host process crashed" failures (blame blank; ~640 tests then a native
// death in ~18s). Serializing eliminates the concurrent native-font access.
//
// NOTE: a xunit.runner.json previously tried to set this but was never copied to
// the output directory, so it had no effect. This compiled-in attribute is the
// authoritative setting (mirrors Pdfe.Cli.Tests, which serialized for an
// analogous reason).
[assembly: CollectionBehavior(DisableTestParallelization = true)]
