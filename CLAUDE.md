# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## ⚠️ CRITICAL: Knowledge Management Strategy

**READ THIS FIRST** - This project uses a strict four-tier content organization system:

| Content Type | Where It Goes | Why |
|--------------|---------------|-----|
| **Concepts, algorithms, theory** | 📚 **Wiki** | Educational, timeless reference |
| **Research, ideas, lab notes** | 💬 **Discussions** | Unstructured exploration, feedback |
| **Bugs, features, tasks** | 🎯 **Issues** | Actionable items with completion criteria |
| **Code documentation, setup** | 📄 **Markdown files** | Version-controlled, code-specific |

**DO NOT** create markdown files for educational content - use the Wiki!

**Example**:
- ❌ Bad: Create `TESTING_GUIDE.md` with tool explanations → Should be Wiki page
- ✅ Good: Create `Testing-and-Development-Tools` Wiki page
- ❌ Bad: Create `FEATURE_IDEAS.md` → Should be Discussion
- ✅ Good: Create GitHub Discussion for ideas, convert to Issues when actionable

See [Knowledge Management Strategy](#knowledge-management-strategy) section below for full details.

---

## Project Overview

This is a cross-platform PDF editor built with **C# + .NET 10 + Avalonia UI** (MVVM architecture). The application runs on Windows, Linux, and macOS, providing PDF viewing, page manipulation, and content-level redaction capabilities. As of v2.0 the PDF stack is pure-.NET and excise-owned (Excise.Core parser/writer, Excise.Rendering SkiaSharp renderer, Excise.Ocr); the legacy PdfPig/PDFsharp/PDFtoImage dependencies have been removed.

**Key Features:**
- Open/view PDFs with zoom and pan controls
- Add, remove, and rotate pages
- Text selection and copy
- Search with highlighting
- Content-level redaction (removes text/graphics from PDF structure, not just visual covering)
- Clipboard history showing redacted text
- Page thumbnails sidebar
- Keyboard shortcuts
- All dependencies use permissive licenses (MIT, Apache 2.0, BSD-3)

## ⚠️ CRITICAL: Redaction Code Requirements

**READ BEFORE MODIFYING ANY REDACTION CODE**

This project implements **TRUE glyph-level removal** for PDF redaction. This is a security-critical feature.

### ABSOLUTE RULES

1. **NEVER replace glyph removal with visual-only redaction** (just drawing black boxes)
2. **NEVER simplify by removing content stream parsing/rebuilding**
3. **ALWAYS maintain the full pipeline**: parse → filter → rebuild → replace → draw
4. **ALWAYS run redaction tests** after any changes: `dotnet test --filter "FullyQualifiedName~Redaction"`

### What Glyph Removal Means

- Text glyphs are **REMOVED** from PDF content stream
- Text extraction tools (pdftotext, PdfPig) **cannot find** the text
- Black box is visual confirmation only (secondary)

### Critical Files - DO NOT SIMPLIFY

```
Excise.Core/Text/Segmentation/GlyphRemover.cs            ← orchestrates glyph-level removal
Excise.Core/Text/Segmentation/LetterFinder.cs            ← text-based letter matching (issue #90)
Excise.Core/Text/Segmentation/OperationReconstructor.cs  ← rebuilds BT/Tf/Tj blocks without removed glyphs
Excise.Core/Content/ContentStreamParser.cs               ← parses content-stream operators
Excise.Core/Content/ContentStreamWriter.cs               ← serializes operators back to bytes
Excise.App/Services/RedactionService.cs                 ← GUI orchestration; mirrors the rewrite onto the page
```

### Required Test Assertions

⚠️ **The assertion below is NOT sufficient on its own. It has passed on leaking
documents three separate times.**

```csharp
// NECESSARY, BUT BLIND. Reads only the CONTENT STREAM.
var textAfter = PdfTestHelpers.ExtractAllText(redactedPdf);
textAfter.Should().NotContain("REDACTED_TEXT",
    "Text must be REMOVED from PDF structure, not just hidden");
```

**Why this is not enough.** `ExtractAllText` reads the content stream. A PDF
restates the same text in carriers it cannot see, and each one has already
shipped a green suite over a leaking file:

| Leak | Where the text survived | What our assertion said |
|------|------------------------|-------------------------|
| #636 | `/ActualText`, `/Alt` in the structure tree | ✅ clean |
| #608 | XMP `/Metadata`, outline titles, annotation `/Contents` | ✅ clean |
| #637 | A page our own **extractor cannot read** (IRS 1040 p47: excise sees 471 chars, mutool sees 3192) | ✅ clean |

The third is the general case, and the rule to remember:

*(The #637 p47 anecdote no longer reproduces — excise now extracts 3233 chars
there vs. mutool's 3192 — but the general failure mode is not fixed, it's now
**measured**: #645's corpus-wide gate (332 pages / 13 fixtures, including the
checked-in CJK Type0 fixture that names #645's second blind spot).

Re-run 2026-08-13: **aggregate coverage 100.0%**, worst page floor 0.923,
nothing below 0.90 — deliberately not ASCII-folded, so it can't silently
cancel out CJK/accented-text loss on both sides.

Until 2026-08-13 this section documented aggregate 98.7% with blindness
"concentrated in dense multi-column government instruction booklets"
(irs-1040-instructions: 47 of 126 pages below 0.99, worst 0.774). That
attribution to layout complexity was WRONG: the cause was Td/TD/T*/'/"
applying line-step offsets without composing through the text matrix
(§9.4.2), in both text-state machines — there is one since #992, see "One walk,
many sinks". Under scaled/flipped matrices, letters
stacked or marched off-page and the CropBox filter silently dropped them —
the same defect that made redaction destroy 5-36% of a document per term
(#942). See the Limitations entry below for the full account, and
`TextMatrixLineSteppingTests` for the pins.

⚠️ **The gate passing means "no worse than the checked-in floors", NOT "good
enough".** Floors were set at whatever the behaviour was. Do not read a green
run as an absence of blindness.

An earlier version of this note claimed 102.6% aggregate and *over*-extraction
(>1.0) on the Type0/CID pages, attributed to a marked-content `/Artifact` leak
(#649, since closed). That is no longer what the gate reports — the number is
now below 1.0. See `tests/extraction-parity/baseline.json` and
`scripts/check-extraction-parity.sh`. Anecdote → measurement is the point of
#645: don't restate a specific number here without re-running the gate.)

> **Redaction completeness is bounded by extraction coverage. excise cannot redact
> what excise cannot read — and it will report success anyway.**

So a redaction test MUST also assert at least one of:

```csharp
// 1. CARRIER-AGNOSTIC — search the SAVED BYTES, INCLUDING INSIDE COMPRESSED
//    STREAMS. If the secret is anywhere in the file, in any carrier, this fails.
var saved = SaveToBytes(redactedPdf);
SavedPdfLeakScanner.FindTerm(saved, "REDACTED_TEXT").Should().BeEmpty();

// ⚠️ This block used to prescribe the RAW form:
//        (Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved))
//            .Should().NotContain("REDACTED_TEXT");
//    That is BLIND to anything inside a /FlateDecode stream, and excise
//    compresses on save. It declared #1040's leaking output clean — 0 ASCII
//    hits, 0 UTF-16BE hits, while mutool read the name straight out of the
//    file. Measured (SavedPdfLeakScannerTests): a structure element's
//    /ActualText — the carrier #636 was filed for — is packed into a
//    Flate-compressed /ObjStm and is invisible to the raw scan.
//    Note the writer deliberately keeps /Title, /Author, /Subject, /Keywords,
//    /Creator, /Producer and /Contents OUT of object streams so they stay
//    greppable (ContainsDocumentCarrierText) — but that list is not the same
//    list the scrubber handles, so do not rely on it. Use the scanner (#1049).

// 2. INDEPENDENT EXTRACTOR — a tool that is not excise.
MutoolTextExtractor.ExtractPage(path, page).Should().NotContain("REDACTED_TEXT");

// 3. INDEPENDENT RENDERER — an ink differential over the redacted region.
//    Text can be gone from every text carrier and still be VISIBLE (vector
//    paths, raster pixels). Extraction cannot see ink; a renderer can.
InkFractionIn(after, box).Should().BeLessThan(0.001);   // was > 0.02 before
```

**The principle, learned the hard way:**

> **A tool must not be its own oracle for the property it exists to guarantee.**
> excise confirming that excise removed the text proves only that its bugs are
> self-consistent.

Working examples of all three:
- `Excise.Core.Tests/Text/Segmentation/StructureTreeRedactionLeakTests.cs` (saved bytes)
- `Excise.Rendering.Tests/Differential/RedactionReferenceVerificationTests.cs` (independent extractor + ink differential)
- `Excise.Rendering.Tests/Differential/RedactionRoundTripTests.cs` (corpus, both ways)

**See `REDACTION_AI_GUIDELINES.md` for complete documentation.**

## Build and Run Commands

### Basic Development

```bash
# Restore packages (required after cloning or adding dependencies)
cd Excise.App
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run

# Run in release mode
dotnet run -c Release
```

### Testing

```bash
# Run all tests
cd Excise.App.Tests
dotnet test

# Run tests with detailed output
dotnet test --logger "console;verbosity=detailed"

# Run specific test
dotnet test --filter "FullyQualifiedName~RedactSimpleText"

# Run only integration tests
dotnet test --filter "FullyQualifiedName~Integration"
```

### Publishing

```bash
# Linux standalone executable
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Windows standalone executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# macOS standalone executable
dotnet publish -c Release -r osx-x64 --self-contained true -p:PublishSingleFile=true
```

Published executables are in `bin/Release/net10.0/{runtime}/publish/`

### Build Scripts

```bash
# Use provided build scripts
./build.sh          # Linux/macOS
./build.bat         # Windows
```

### Release Documentation Gate

Before tagging or describing a release, run `scripts/check-doc-claim-freshness.sh` and
follow `docs/RELEASE_CHECKLIST.md`. Feature, security, and workflow changes
must keep implementation, tests, UI text, release notes, GitHub issues, and
user-facing docs in sync in the same pass.

## Architecture

### MVVM Pattern

The codebase follows strict MVVM separation:

**View Layer** (`Views/`):
- `MainWindow.axaml` - XAML UI definition
- `MainWindow.axaml.cs` - Code-behind (minimal, only event handlers)

**ViewModel Layer** (`ViewModels/`):
- `MainWindowViewModel.cs` - Application state, commands, and business logic orchestration
- Uses ReactiveUI for property change notifications and command binding

**Model Layer** (`Models/`):
- `PageThumbnail.cs` - Data structures

**Service Layer** (`Services/`):
- `PdfDocumentService.cs` - PDF loading, saving, page add/remove
- `PdfRenderService.cs` - PDF-to-image rendering (uses Excise.Rendering / SkiaSharp)
- `RedactionService.cs` - Orchestrates content-level redaction
- `PdfTextExtractionService.cs` - Text extraction from PDFs
- `PdfSearchService.cs` - Search functionality
- `CoordinateConverter.cs` - Coordinate system conversions

### Data Flow

```
User Interaction (View)
    ↓ Command Binding
ViewModel (MainWindowViewModel)
    ↓ Calls Services
Service Layer (PdfDocumentService, RedactionService, etc.)
    ↓ Uses Libraries
PDF Libraries (Excise.Core for parsing/redaction/save, Excise.Rendering for Skia render, Excise.Ocr for OCR)
```

When modifying the UI, update the XAML and bind to ViewModel properties. Never put business logic in code-behind.

## ⚠️ One walk, many sinks (#992)

**There is ONE content-stream state machine: `Excise.Core/Content/ContentStreamWalker.cs`.
A new consumer adds a SINK. It never adds a parser.**

The walker owns the tokenizer, the graphics state stack including the §8.4.1
Table 52 text parameters that `q`/`Q` save and restore, §9.4.2 line stepping
composed through the text matrix, the §9.4.4 glyph advance, the glyph cell
transform, font resolution and the shared `GlyphUnicodeDecoder` cascade. It
walks the bytes once and hands each glyph to a sink through a struct callback.

There are two sinks:

| sink | what it makes of a glyph |
|---|---|
| `Content/ContentStreamParser.cs` | aggregates glyph cells into `ContentOperator.BoundingBox` + `TextContent` — the geometry redaction removes on |
| `Text/TextExtractor.cs` | emits a `Letter`, tagged with marked-content id and optional-content visibility |

**Why this is load-bearing and not tidiness.** The walk used to exist TWICE —
`ContentStreamParser` (2,062 lines) and `TextExtractor` (2,471) each tokenized
the same bytes, each tracked state, each computed advances — and four comments
asked people to keep the copies in sync by hand. Every RC1 defect lived in the
drift: §9.4.2 line stepping (#942/#899, which destroyed 5–36% of a document per
redacted term for months), the advance terms inside horizontal scaling (#734),
the glyph cell 12× too small in one machine (#833/#980), the array nesting bound
(#971), the hex-digit skip (#974), `sh`/`d0`/`d1` and inline images (#980), the
decode cascade at 3 steps vs 9 (#981), cancellation (#982), and the Table 52
text state in NEITHER (#983).

⚠️ **Do not re-add a second one, and do not "fix" a renderer or a new consumer
by teaching it its own text state.** That is how the fourth divergence starts.
`SkiaRenderer` is still a separate machine — it consumes the walker's operators
through `ContentStreamParser` in tokenizer-only mode (`TrackState = false`,
#598) and re-executes them under its own graphics/text state. It is the
remaining exception, tracked, not a precedent.

#986 closed the Table 52 half of that exception WITHOUT porting the renderer to
a sink: `q` now snapshots the text parameters (font+size, Tc, Tw, Tz, TL, Ts,
Tr) and `Q` restores them, copying the walker's own `TextStateSnapshot` split —
the resolved font travels with the name, the §9.4.1 text MATRIX deliberately
does not. A full port was evaluated and rejected for this change: `WalkedGlyph`
carries no typeface/GID/render-mode, the renderer's CTM lives in `SKCanvas`
while the walker's lives in its own state, and Type3 CharProcs, tiling
patterns, clipping text modes and soft masks would all need sink hooks that do
not exist. **That rejection is scoped to #986, not a licence to add renderer
state machinery.** The gate is
`Excise.Rendering.Tests/Differential/GraphicsStateTextParameterRenderingTests`
— mutool and pdftocairo, because a differential between excise's parsers and
excise's renderer could not see a defect they shared, and did not.

**The differential gate is gone, deliberately.** `ParserDifferentialTests` (58
tests, #980) existed to detect divergence between the two machines; with one
machine it has no subject. Measured rather than assumed: reverting §9.4.2
composition to the raw `e += tx` reddened 2 of its tests before the
consolidation and **zero** after, while `TextMatrixLineSteppingTests` — a
property gate, not a twin gate — caught it either way. What replaces it:

- `TextMatrixLineSteppingTests` — §9.4.2 against the spec.
- `GraphicsStateTextParameterTests` — Table 52 as a spec property (#983).
- `GraphicsStateTextParameterOracleTests` — the same, against mutool (#983).
- `ExtGStateFontTests` — Table 58 `/Font`, both sinks (#990).
- `scripts/check-extraction-parity.sh` and the redaction-collateral ratchets —
  what a user actually gets.

The rule those encode: **a gate that compares excise to excise cannot see a
defect excise holds consistently.** Compare against the spec, or against a tool
that is not excise.

## Redaction Engine Architecture

This is the most complex part of the codebase. As of v2.0 the redaction engine
lives in **`Excise.Core`** (pure .NET), not the GUI project. The authoritative
glyph-level pipeline is in `Excise.Core/Text/Segmentation/` (GlyphRemover,
LetterFinder, OperationReconstructor) and the content-stream machinery in
`Excise.Core/Content/` (ContentStreamParser, ContentStreamWriter). The GUI's
`Excise.App/Services/RedactionService.cs` only orchestrates and mirrors the
rewrite onto the rendered page — see the "Critical Files" box at the top of
this document for the canonical paths.

The component descriptions below are kept as a conceptual reference for the
parse → filter → rebuild → replace → draw flow; the class names map onto the
`Excise.Core` types above (e.g. ContentStreamBuilder → ContentStreamWriter).

### Components

1. **ContentStreamParser.cs** (~500 lines, high complexity)
   - Parses PDF content streams into structured operations
   - Tracks graphics state (transformations, colors) via state stack
   - Tracks text state (font, position, spacing)
   - Calculates bounding boxes for each operation
   - Returns list of `PdfOperation` objects

2. **PdfOperation.cs** (~200 lines)
   - Base class and derived types: `TextOperation`, `PathOperation`, `ImageOperation`, `StateOperation`, `TextStateOperation`
   - Each has `BoundingBox` property and `IntersectsWith(Rect)` method
   - Represents parsed PDF operators with position information

3. **TextBoundsCalculator.cs** (~150 lines, high complexity)
   - Calculates accurate text bounding boxes
   - Applies font size, character/word spacing, horizontal scaling
   - Applies text matrix and graphics transformation matrix
   - Handles PDF (bottom-left) to Avalonia (top-left) coordinate conversion

4. **ContentStreamBuilder.cs** (~150 lines)
   - Serializes `PdfOperation` objects back to PDF operator syntax
   - Handles proper escaping and formatting
   - Rebuilds content streams after filtering

5. **State Tracking**
   - `PdfGraphicsState.cs` - Transformation matrix, line width, colors, save/restore stack
   - `PdfTextState.cs` - Font, size, position, spacing, text matrix
   - `PdfMatrix` helper - 2D transformations

6. **RedactionService.cs** (~150 lines)
   - Main entry point for redaction
   - Orchestrates: parse → filter → rebuild → replace → draw black rectangle
   - Method: `RedactArea(PdfPage page, Rect area)`

### Redaction Flow

```
1. Parse content stream → List<PdfOperation> with bounding boxes
2. Filter operations → Remove those intersecting redaction area
3. Rebuild content stream → Serialize remaining operations to PDF syntax
4. Replace page content → Update PDF with new content stream
5. Draw black rectangle → Ensure visual coverage
```

### Coordinate Systems

**Critical**: PDF uses bottom-left origin, Avalonia uses top-left origin.

Conversion: `AvaloniaY = PageHeight - PdfY - RectHeight`

This conversion happens in `TextBoundsCalculator` and when drawing redaction rectangles.

### Supported PDF Operators

- **Text**: `Tj`, `TJ`, `'`, `"`
- **Text State**: `BT`, `ET`, `Tf`, `Td`, `TD`, `Tm`, `T*`, `TL`, `Tc`, `Tw`, `Tz`
- **Graphics State**: `q`, `Q`, `cm`
- **Paths**: `m`, `l`, `c`, `v`, `y`, `h`, `re`, `S`, `s`, `f`, `F`, `f*`, `B`, `B*`, `b`, `b*`
- **Images**: `Do`

## Key Dependencies

Located in `Excise.App/Excise.App.csproj`:

**UI Framework:**
- Avalonia 12.0.4 (cross-platform XAML UI)
- ReactiveUI 23.2.27 (MVVM framework)
- FluentAvaloniaUI 3.0.0-preview2 (Fluent theme/controls)

**PDF Stack (excise-owned, pure .NET):**
- Excise.Core - parser, writer, content streams, fonts, encryption, glyph-level redaction
- Excise.Rendering - SkiaSharp-based renderer (replaces PDFium)
- Excise.Ocr - shells out to system tesseract

**Supporting:**
- SkiaSharp 3.119.4 (MIT) - 2D graphics / rasterization
- BouncyCastle.Cryptography 2.6.2 (MIT) - crypto primitives for encryption

The legacy PdfPig / PDFsharp / PDFtoImage dependencies were removed in v2.0.
All remaining licenses are permissive (MIT/Apache 2.0/BSD-3), no copyleft restrictions.
SkiaSharp ships a native component but is MIT-licensed.

## Test Infrastructure

Located in `Excise.App.Tests/`:

**Framework**: xUnit 2.5.3 with FluentAssertions 6.12.0

**Test Count** (2026-07-13): ~7,600 across five suites — Excise.Core ~3,180,
Excise.Rendering ~3,420, Excise.App ~905, Excise.Cli 86, Excise.Avalonia 10.
Don't hard-code a number here; it goes stale. Run the suites.

⚠️ **`Excise.App.Tests` is SERIAL BY DESIGN** — `[assembly: CollectionBehavior(
DisableTestParallelization = true)]` in `AssemblyInfo.cs`. xunit's parallelism
races SkiaSharp's **process-wide native font manager** and crashes the test host
(#363). **Do not re-enable parallelism.** The natural instinct on seeing a
~17-minute serial suite is to parallelize it; that reintroduces a native crash
that took real effort to diagnose.

Because it is serial and long, it is also sensitive to CPU contention: running
other test projects alongside it can push the 144-page display sweep past its
wall-clock timeout and produce a **false red** (observed three times on
2026-07-13 — twice from concurrent runs, once from ~900MB of accumulated
`logs/` + `artifacts/` in the working copy). Run it alone. See #619.

**Utilities:**
- `Utilities/TestPdfGenerator.cs` - Creates test PDFs with known content
- `Utilities/PdfTestHelpers.cs` - PDF inspection and text extraction

**Test Categories:**
- `Integration/` - End-to-end redaction, coordinate conversion, batch processing
- `Unit/` - ViewModel, coordinate conversion, PDF operations
- `UI/` - Headless UI tests, ViewModel integration
- `Security/` - Content removal verification

**Key Test Files:**
- `GuiRedactionSimulationTests.cs` - Simulates exact GUI workflow to catch coordinate issues
- `CoordinateConverterTests.cs` - Validates coordinate math
- `ComprehensiveRedactionTests.cs` - Full redaction pipeline tests

**Running Tests:** See "Build and Run Commands" section above.

### Test Tiers — what to run before what (#646)

There is one defined answer to "what do I run before X?" —
`scripts/test-tier.sh {t0|t1|t2|t3}`. Before this, the choice in practice was
either *nothing* or the full ~28-minute release smoke, and both are wrong
most of the time.

| Tier | Cost | What | When |
|------|------|------|------|
| `t0` | ~30s | Build + Excise.Core/Cli/Avalonia tests + `check-doc-claim-freshness.sh` + `check-gate-asymmetry.sh` + `verify-true-redaction.sh` | Before every push. No excuse not to run it — `scripts/test-tier.sh --install-hook` installs it as `.git/hooks/pre-push`. |
| `t1` | ~10m | `t0` + the full redaction test suites + `Excise.Rendering.Tests` (deterministic) + `check-skip-budget.sh` | What CI blocks a PR on. `scripts/ci-test.sh` is now a thin wrapper around this. |
| `t2` | ~30m | `scripts/release-smoke.sh --release-tests` | Release candidate. |
| `t3` | — | `t2` plus the same on macOS and Windows (#647) | Before tagging a release. |

**Tier is selected by blast radius — who gets hurt if this is wrong — not by
convenience.** Pick the tier that matches what the change touches, not the
tier that's fastest to run.

**excise-specific rule: you are your own third party.** A local build you
redact a real document with is a binary whose failure hurts someone, and the
failure is silent — no crash, no error, the name is just still in the file.
The redaction gate is therefore non-negotiable at every tier that produces a
binary anyone could redact with, including a purely local build: `t0`
includes the near-free static redaction-architecture guard
(`verify-true-redaction.sh`); `t1`'s redaction test suites run unconditionally
and there is no flag to skip them.

### Restartable full runs — `scripts/run-full-suite.sh`

A tier that takes 30+ minutes will get interrupted, and restarting from zero
each time means it never finishes. On 2026-07-29 a kernel panic (`watchdog
timeout: no checkins from watchdogd in 91 seconds`, 17 swapfiles, LOW swap
space) killed five concurrent sessions mid-run — the machine went down at
18:07 and rebooted at 18:12:13.

```bash
caffeinate -i scripts/run-full-suite.sh --resume 2>&1 | tee -a logs/full-suite.log
scripts/run-full-suite.sh --status    # what's done / what's left
scripts/run-full-suite.sh --list      # the plan, runs nothing
```

Re-running after any interruption skips what already passed. `--resume` also
exists on `test-tier.sh` (all tiers) and `release-smoke.sh`; both default to
OFF so the pre-push hook and CI keep skipping nothing.

Three properties worth not breaking:

1. **Checkpoints fail toward re-running, never toward skipping.** A panic loses
   buffered writes, so a naive marker file can survive as zero-length metadata
   and read back as "passed" for a step that never ran. Markers are
   sync-then-atomic-rename and validated on read (non-empty + terminal
   `--CKPT-OK--` sentinel + recorded commit == HEAD). Anything torn, truncated,
   or stale re-runs. See `scripts/lib-runner.sh`.
2. **The redaction gates are never checkpointed.** They re-run on every
   invocation including resumes — a checkpoint that skipped them would be
   precisely the flag the rule above says does not exist
   (`RUNNER_NEVER_CHECKPOINT`).
3. **A step matching zero tests is a FAILURE, not a pass.** `dotnet test`
   exits 0 when a `--filter` matches nothing; checkpointing that would bake a
   vacuous green in permanently. The runner greps for `No test matches the
   given testcase filter` and fails the step. (This is not hypothetical — the
   first draft of the runner had exactly this bug.)
4. **A `--no-build` run must prove the binary is fresh.** Shared scripts call
   `scripts/assert-fresh.sh` before `dotnet test --no-build` and refuse when a
   source file is newer than the output DLL. For ad-hoc work, prefer
   `scripts/t.sh <project> [test args...]`; use `EXCISE_ALLOW_STALE_NO_BUILD=1`
   only when intentionally measuring an old binary.

Memory posture, and **what was measured rather than assumed**: GC-flag tuning
(`DOTNET_gcServer=0` + `GCConserveMemory` + a heap cap) was tried and made peak
RSS ~24% **worse** on `Excise.Core.Tests` — 552 MB vs 446 MB stock. testhost
already runs Workstation GC here, so `gcServer=0` is a no-op. Both knobs are
therefore **off by default** (`RUNNER_TUNE_GC=0`, `RUNNER_HEAP_CAP_GIB=0`) and
kept only so the measurement stays reproducible. Do not re-enable them on the
"Server GC reserves per-core heaps" theory — it was checked and it is not what
costs memory in this repo.

Peak RSS per testhost varies enormously by project, and an early note here
claimed it did not — that claim was made from a partial sample (Core and one
Rendering chunk) and was **wrong**. Measured over a full run:

| step | peak RSS (2026-07) | re-measured 2026-08-16 |
|---|---:|---:|
| `Excise.App.Tests` unchunked (one process) | **8536 MB** | **3862 MB** |
| `Excise.App.Tests` heaviest chunk | 6576 MB (chunk05) | 3323 MB (chunk06) |
| `Excise.Rendering.Tests` heaviest chunk | 2389 MB (chunk02) | 2006 MB (chunk04) |
| `Excise.Core.Tests.*` (all 17 chunks) | ≤ 450 MB | not re-run (checkpointed) |
| `corpus-scan-verapdf` (2694 pages) | — | 3446 MB |
| `corpus-scan-pdfjs` (685 pages) | — | 3780 MB |

The 2026-08-16 column is the first full `--everything` run
(`logs/full-suite_Debug_20260815_203507/resources.tsv`; App.Tests unchunked =
1329 tests, no filter, 8m38s — the same step, not a subset). App.Tests has more
than halved since #861's chunking and bitmap work, so the old numbers are kept
side by side rather than overwritten: the LESSON (a single `dotnet test` can
take a large fraction of a 24 GB machine, so do not run it alongside other heavy
work — this is also why it is serial by design and why CPU contention produces
false reds here) survives the improvement, and the biggest single consumer is
now a corpus scan, not App.Tests. Tracked as #861.

⚠️ Both columns are measurements with a date, not properties. Re-run the suite
and read `resources.tsv` before quoting either — the 2026-07 numbers were 2.2×
high within one month.

What bounds memory during a suite run is therefore structural:
exactly one dotnet process at a time (this runner is strictly serial),
short-lived testhosts via chunking so nothing accumulates over a 30-minute run,
and a guard that waits out `kern.memorystatus_vm_pressure_level` and aborts with
exit 75 if the data volume falls below `RUNNER_MIN_FREE_GIB` (macOS grows swap
there; starving it turns a memory spike into a panic rather than an OOM).

`release-smoke.sh --resume` gets checkpointing only, never the GC knobs: its
benchmark and perf-budget gates are allocation-anchored and would read any GC
change as a regression.

Chunking caveat: it changes which tests share a process, so it can hide or
manufacture cross-test contamination (a real one existed — a shared
`window.json` view-mode preference leaking between continuous-view tests, which
only reproduced in a full-suite run). The chunks are for fast resumable
feedback; the unchunked `app-tests-unchunked-evidence` step is what counts as
evidence. `check-skip-budget.sh` likewise needs whole-project runs and keeps
its own unchunked steps.

### The skip allowlist is environment-conditioned (#854)

`tests/skip-allowlist/*.txt` entries may declare what a test needs in order to
run, inside the justification:

```
Some.Test.Name   # needs the poppler corpus [requires: corpus:poppler]
```

`tool:NAME` (on PATH), `corpus:NAME` (`test-pdfs/NAME` non-empty), `env:NAME`.
All listed specs must be present.

This exists because the allowlist is calibrated for a corpus-**less** CI runner.
Most entries gate on a gitignored corpus or an optional tool, so on a
corpus-equipped dev machine those tests *run* — and the reverse check
("allow-listed skips are no longer skipping") fired on every local run, on all
three projects. `t1` and `run-full-suite.sh` inherited a guaranteed failure. A
gate that always fails locally is a gate people stop reading, and that is
precisely how six un-allow-listed skips reddened `test-linux` for 8+ consecutive
runs before anyone looked.

Two invariants, both pinned by `scripts/test-check-skip-budget.sh`:

- **The forward check is never relaxed.** A skip that is not allow-listed fails,
  always, conditioning or not.
- **Conditioning is not unconditional.** When a declared prerequisite is
  *absent*, the reverse check fires exactly as before. The selftest forces a
  spec absent (`SKIP_BUDGET_FORCE_ABSENT`) to prove this, because the CI-side
  branch cannot be reproduced on a machine that has every corpus — and must not
  be tested by moving 888 MB of fixtures around.

`--update` keeps conditioned entries whose prerequisites are satisfied. Without
that it would *delete* the entries CI depends on when run from a dev machine,
turning the flag from "won't add the skip you need" into "removes the ones you
had". An entry with no marker keeps the original unconditional behaviour, so
"unconditioned" stays the safe default when a test's gate is unclear.

## Common Development Workflows

### Adding a New PDF Operation Type

1. Add operator handling in `ContentStreamParser.ParseOperator()`
2. Create new operation class in `PdfOperation.cs` if needed
3. Implement bounding box calculation
4. Add serialization in `ContentStreamBuilder.SerializeOperation()`
5. Add unit tests

### Modifying UI

1. Update XAML in `Views/MainWindow.axaml`
2. Add properties/commands to `ViewModels/MainWindowViewModel.cs`
3. Use ReactiveUI's `[Reactive]` attribute for bindable properties
4. Commands use ReactiveUI's `ReactiveCommand`

### Adding a New Service

1. Create service class in `Services/`
2. Inject into `MainWindowViewModel` constructor
3. Call service methods from ViewModel commands
4. Add corresponding tests in `Excise.App.Tests/`

## Debugging Notes

### Redaction Not Working

1. Check console output - parser logs operations found/removed
2. Verify coordinate system (PDF vs Avalonia Y-axis)
3. Check bounding box calculations in `TextBoundsCalculator`
4. Enable verbose logging in `ContentStreamParser`

### PDF Rendering Issues

- Rendering goes through Excise.Rendering (SkiaSharp); SkiaSharp carries its own native component
- Check `PdfRenderService.cs` (GUI) and `Excise.Rendering/SkiaRenderer.cs` for rendering code

### Build Failures

- Run `dotnet restore` first
- Ensure .NET 10.0 SDK installed: `dotnet --version`
- Clear build artifacts: `dotnet clean`

### Build Warnings

**IMPORTANT**: Always maintain a clean build (0 warnings, 0 errors).

Common warnings and fixes:
- **CS8618** (Non-nullable property not initialized):
  - Add `= null!;` to properties initialized in constructor
  - Example: `public ReactiveCommand<Unit, Unit> SaveCommand { get; } = null!;`
  - See issue #29 for systematic fix

Never let warnings accumulate - fix them proactively when they appear.

## ⚠️ Common Pitfalls and Lessons Learned

This section documents recurring issues that have caused bugs. **Read this before modifying redaction code.**

### Pitfall 1: Position Mismatch Between Libraries (Issue #90)

**Problem**: ContentStreamParser calculates glyph positions that can differ from PdfPig's letter positions by 3-6 points. Code that assumes these match will fail.

**Symptom**: Letter matching fails, redaction doesn't find text, operations appear to be at wrong coordinates.

**Root Cause**: ContentStreamParser estimates positions using font metrics and transformation matrices. PdfPig extracts actual positions from the PDF. These are approximations vs ground truth.

**Solution**: When matching parsed operations to PdfPig letters:
- ❌ Don't rely solely on position proximity
- ✅ Use text content matching within a Y-band tolerance
- ✅ Trust PdfPig positions as ground truth
- ✅ Use parsed positions only as hints for disambiguation

```csharp
// BAD: Position-only matching
var closest = letters.OrderBy(l => Math.Abs(l.X - parsedX)).First();

// GOOD: Text matching with position as tiebreaker
var matchIndex = candidateText.IndexOf(operationText);
if (multipleMatches) pickClosestToExpectedPosition();
```

### Pitfall 2: PDF State Not Persisting Across Blocks (Issue #167)

**Problem**: PDF text blocks (BT...ET) require font state (Tf operator) before any text-showing operators (Tj, TJ). When blocks are removed during redaction, subsequent blocks may lose required state.

**Symptom**: "Could not find font" errors, corrupted PDFs, text rendering failures after redaction.

**Root Cause**: The first BT block may contain the Tf operator. If that block is removed, later blocks have Tj without Tf.

**Solution**: ContentStreamBuilder must track and inject state:
- ✅ Track last known font from Tf operators
- ✅ When entering BT block, mark that Tf is needed
- ✅ Before emitting Tj/TJ, inject Tf if not yet seen in this block
- ✅ Get font info from TextOperation metadata if available

```csharp
// In ContentStreamBuilder.Build():
if (inTextBlock && needTfInjection && IsTextShowingOperator(op))
{
    // Inject Tf before the text operator
    sb.Append($"{fontName} {fontSize} Tf\n");
    needTfInjection = false;
}
```

### Pitfall 3: Operations Without Timeouts (Issue #93)

**Problem**: PDF parsing can hang indefinitely on malformed PDFs. Operations without timeouts cause test hangs and poor user experience.

**Symptom**: Tests hang forever, automation scripts never complete, unresponsive UI.

**Solution**: Always use timeouts for PDF operations:
```csharp
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await Task.Run(() => LoadDocument(path), cts.Token);
}
catch (OperationCanceledException)
{
    throw new TimeoutException($"Operation timed out: {path}");
}
```

### Pitfall 4: Coordinate System Confusion

**Problem**: PDF uses bottom-left origin, Avalonia uses top-left. Mixing them causes redaction at wrong locations.

**Symptom**: Redaction appears at wrong Y position, text not removed, visual marker in wrong place.

**Solution**:
- ✅ Always document which coordinate system a variable uses
- ✅ Convert at system boundaries, not deep in code
- ✅ Name variables clearly: `pdfY` vs `screenY` vs `avaloniaY`

```csharp
// Convert PDF (bottom-left) to Avalonia (top-left)
double avaloniaY = pageHeight - pdfY - rectHeight;
```

### Pitfall 5: Testing Only Happy Path

**Problem**: Tests pass with simple PDFs but fail with real-world documents that have unusual fonts, encodings, or structures.

**Solution**:
- ✅ Test with real-world PDFs (birth certificates, government forms)
- ✅ Test with corpus PDFs (veraPDF test suite)
- ✅ Test sequential redactions (state accumulation bugs)
- ✅ Test edge cases: special characters ($, parentheses), Unicode, ligatures

## Important Implementation Details

### State Stack Handling

PDF uses `q` (save) and `Q` (restore) operators to manage graphics state. The parser maintains a state stack:

```csharp
case "q": // Save state
    _stateStack.Push(_currentState.Clone());
    break;
case "Q": // Restore state
    if (_stateStack.Count > 0)
        _currentState = _stateStack.Pop();
    break;
```

Always maintain state stack integrity when parsing.

### Text Matrix Transformations

Text position is calculated from text matrix + graphics transformation matrix:

```csharp
var transformedMatrix = textState.TextMatrix.Multiply(graphicsState.TransformationMatrix);
var position = transformedMatrix.Transform(new Point(0, 0));
```

This is critical for accurate text positioning.

### Content Stream Replacement

To replace page content after filtering:

```csharp
page.Contents.Elements.Clear();
page.Contents.CreateSingleContent(newContentBytes);
```

Never manually modify content stream bytes without parsing first.

## Performance Considerations

- Simple page (50 ops): ~10-20ms to redact
- Complex page (500+ ops): ~100-200ms to redact
- For multiple redactions on same page, parse once and filter multiple areas
- Memory usage: ~1-5MB per page during parsing

## Limitations

Verified against the code on 2026-07-13. Do not add a limitation here without
checking it is still true — several entries in this list were stale for months
and cost real planning time.

1. **Text extraction coverage bounds redaction completeness** (#637, gated by
   #645) — ⚠️ the most important entry in this file. Where excise's extractor
   cannot read text, `RedactText` cannot match it, does not remove it, **and
   reports success**. Corpus-wide measurement (332 pages / 13 fixtures —
   real-world government and court PDFs plus checked-in CJK/Type0 and
   scrambled-glyph-order edge cases, `scripts/check-extraction-parity.sh`,
   re-run 2026-08-13): **aggregate coverage 100.0%** of mutool's Unicode
   letter/digit count (counted per-script, not ASCII-folded, specifically so
   CJK/accented-text loss can't cancel out invisibly on both sides of the
   ratio). Worst page floor anywhere in the corpus: 0.923; nothing below 0.90.

   **The "concentrated blindness" this entry used to document is FIXED, and its
   cause is a lesson.** Until 2026-08-13 the aggregate was 98.7% with
   `irs-1040-instructions.pdf` at 47 of 126 pages below 0.99, ten below 0.90,
   worst 0.774 — attributed here to "dense multi-column government instruction
   booklets" as if layout complexity were the cause. The actual cause was seven
   lines of §9.4.2 arithmetic: Td/TD/T*/'/" applied their line-step offsets RAW
   instead of composing through the text matrix (`e += tx` instead of
   `e += tx·a + ty·c`), in BOTH text-state machines (TextExtractor and
   ContentStreamParser — since #992 there is only one, see "One walk, many
   sinks"). Under the ubiquitous `/F1 1 Tf` + scaled-`Tm` producer
   idiom, every line stacked onto the first in the letter model; under flipped
   matrices letters marched off-page, where the CropBox filter silently dropped
   them. Rendering stayed correct the whole time because ShowGlyph's §9.4.4
   advance was already composed properly — half the state machine honoured the
   matrix and half did not. The same defect was destroying 5-36% of a document
   per redacted term (#942): the stacked letters genuinely overlapped the match
   bbox, and GlyphRemover faithfully removed what the geometry told it.
   Pinned by `TextMatrixLineSteppingTests` and the corpus-wide
   `RedactionCollateralHarness` ratchet.

   ⚠️ **A green gate means "no worse than the checked-in floors", NOT "no
   blindness".** Floors were set at whatever the behaviour was.

   Both blind spots #645 was written to measure — the p47-style
   under-extraction and the CJK/Type0 "extracts as empty" case
   (`RealWorldSearchTests.CjkFixture_*`) — are clean on their fixtures.
   An earlier version of this entry reported 102.6% aggregate and
   *over*-extraction (coverage >1.0) on 83 Type0/CID pages, attributed to a
   marked-content `/Artifact` leak (#649, since closed). The gate no longer
   reports that; coverage on those pages is now below 1.0. (Checked that "CJK is
   clean" isn't `page.Text` vouching for itself: `RedactText` locates words
   via the search/word path, not `page.Text`, and
   `RealWorldSearchTests.CjkFixture_Search_FindsLatinWord` — previously a
   documented `SkipWhen(matches.Count == 0)` gap — now genuinely passes, so
   both paths agree.) Floors are checked in at
   `tests/extraction-parity/baseline.json` and ratchet on `--update`; a
   font-resolver change either improves the delta or the gate fails it. This
   makes the font-model work (#512–#515, #532) *redaction security*, not
   display polish — #513 must not start until this gate is green on its
   changes.
2. **Font Metrics**: approximation, not full font dictionaries (#512, #513).
3. **Encryption round-trips, with an upgrade-only caveat** (#639/#640/#643;
   umbrella #624) — the writer emits AES-256 (V5 R6) and AES-128 (V4 R4
   AESV2) `/Encrypt`, and since #643 a document opened encrypted SAVES
   encrypted by default on every mutating path (GUI save/redact/flatten,
   scripting, CLI `redact`/`fill-form`/`add-field`/`autodetect-fields
   --apply`/`make-searchable`, batch `redaction.apply`): same algorithm,
   same `/P`, same `/EncryptMetadata`, same password
   (`PdfDocument.GetReEncryptionOptions` + the `Save(path, options)`
   overloads; plain `Save()` stays plaintext by design). Caveats: RC4
   sources (V1/V2, V4 CFM=V2) re-encrypt **upgraded to AES-256** — the
   writer never emits RC4; the source's distinct *owner* password is
   unrecoverable from a user open (#324) so the user password is reused for
   both; and `--allow-decrypt` / `allowDecrypt: true` now means the explicit
   opt-OUT to write plaintext (#638's fail-closed gate and its
   `DECRYPT_CONFIRMATION_REQUIRED` batch error are gone — there is no
   forced loss left to confirm). GUI-side, dropping protection is only
   reachable via the Security dialog's Remove Protection (#641). The
   multi-reader interop gate is #644.
4. **`/P` permissions enforced at the action layer only** (#642) — the decoded
   mask is `document.Permissions` / `EffectivePermissions`
   (`Excise.Core/Security/PdfPermissions.cs`, bit meanings qpdf-verified).
   Enforced: GUI copy/selection-copy/page-image-export (bit 5), typewriter and
   form authoring (bit 4), annotations (bit 6), form fill (bit 6 or 9); CLI
   `text`/`letters`/`render`/`ocr` (bit 5), `fill-form` (6/9),
   `add-field`/`autodetect-fields --apply` (bit 4); batch steps and the
   scripting `ExtractAllText` likewise. Deliberately NOT gated: the engine
   (`page.Text`, search, rendering — it stays permission-blind by design),
   and **redaction** (core purpose; #643 owns the encrypted-redact flow).
   Caveats: bit 10 accessibility carve-out via `--for-accessibility` /
   `ExtractAllText(forAccessibility: true)`; explicit overrides
   (`--ignore-permissions`, batch `ignorePermissions: true`, scripting
   `IgnoreDocumentPermissions`) exist because owner-password opening is #324
   — every open today is user-level, so restrictions always apply. Page
   assembly (bit 11) is now gated on the CLI `merge`/`split` commands (#677,
   `DocumentAction.AssembleDocument` → `CanAssemble`); still NOT gated: bit 11
   in the GUI page-organization surface (reorder/rotate/delete in the app) and
   printing (doesn't exist, #621/#622 dropped it).

**Previously listed here and now FIXED — do not re-add:**
- ~~Inline images `BI...ID...EI` not handled~~ → **parsed and re-serialised**
  (`ContentStreamWriter.cs:39-81`), so redaction round-trips them. They are NOT
  fully rendered: 3 corpus pages carrying `BI/ID/EI` images render blank while
  the reference renderers draw them (#887). Narrowed 2026-08-02 — the original
  wording read as "inline images work", which is true of the content-stream
  path and not of the raster path.
- ~~Clipping paths `W`, `W*` not tracked~~ → tracked (`ContentStreamParser.cs:448`).
- ~~Rotated pages not supported~~ → `/Rotate` 0/90/180/270 and inherited rotation
  are honoured end-to-end (`PdfPage.ToContentStreamCoordinates`), covered by
  `RotatedPageRedactionTests`.

See GitHub issues labeled `component: redaction-engine` for enhancement tracking.

## File Locations Quick Reference

⚠️ This map was wrong for months: it pointed redaction at
`Excise.App/Services/Redaction/*` (ContentStreamBuilder, PdfOperation,
TextBoundsCalculator, PdfGraphicsState…). **That directory does not exist.** It
all moved to `Excise.Core` in v2.0. If you are looking for the redaction engine,
it is in `Excise.Core`, not in the GUI project.

```
Excise.Core/                          # the PDF engine — parser, writer, redaction
├── Text/Segmentation/              # ← THE REDACTION ENGINE
│   ├── GlyphRemover.cs             # orchestrates glyph-level removal
│   ├── LetterFinder.cs             # text-based letter matching
│   ├── OperationReconstructor.cs   # rebuilds BT/Tf/Tj without removed glyphs
│   ├── PdfPageRedactionExtensions.cs      # page.RedactArea(rect) entry point
│   ├── PdfDocumentRedactionExtensions.cs  # doc.RedactText(word) entry point
│   ├── StructureTreeRedactionScrubber.cs  # /ActualText, /Alt (#636)
│   ├── InteractiveRedactionScrubber.cs    # annotations, form fields
│   ├── ImageRedactor.cs            # raster/scanned pixel removal
│   ├── FormXObjectFlattener.cs     # inlines forms so their text is reachable
│   └── HiddenTextDetector.cs       # audit: visible-but-unextractable text
├── Content/
│   ├── ContentStreamWalker.cs      # ← THE content-stream state machine (see below)
│   ├── ContentStreamParser.cs      # a SINK: operator bounds + decoded text
│   └── ContentStreamWriter.cs      # serialize operators back to bytes
├── Operations/
│   └── PdfDocumentSanitizer.cs     # /Info, XMP, outlines, annots (#608)
├── Document/                       # PdfDocument, PdfPage, PdfPageRect, coords
├── Fonts/                          # CFF, TrueType parse + subset (see #512-#515)
└── Security/                       # decrypt + encrypt writers (#639-#641), /P permissions (#642)

Excise.Rendering/                     # SkiaSharp renderer
└── Differential/                   # ← REFERENCE ORACLES. Use these, don't build new ones.
    ├── MutoolReferenceRenderer.cs        # 178 uses in Differential tests
    ├── GhostscriptReferenceRenderer.cs   #  78
    ├── PdftocairoReferenceRenderer.cs    #  50
    ├── PdftoppmReferenceRenderer.cs      #  18
    ├── MutoolTextExtractor.cs            # independent TEXT oracle
    ├── QpdfReferenceTool.cs              # structure: --check, --show-npages
    ├── PdfiumReferenceRenderer.cs        # 2 uses — arg-builder unit test only, NOT the oracle below
    ├── PdfiumNativeReferenceRenderer.cs  # 14 uses — the REAL pdfium oracle (#857), see note below the map
    └── PdfBoxReferenceRenderer.cs        # 15 uses — see note below the map

Excise.App/                          # the Avalonia GUI (orchestration only)
├── Services/
│   ├── PdfDocumentService.cs       # load/save/page manipulation
│   ├── PdfRenderService.cs         # render to image
│   └── RedactionService.cs         # ORCHESTRATES Excise.Core; owns no engine logic
├── ViewModels/MainWindowViewModel.cs   # (partial: .Commands/.Search/.Forms/…)
├── Views/MainWindow.axaml(.cs)
└── Automation/CommandAccessibility.cs

Excise.Avalonia/                      # reusable viewer control
└── Controls/PdfViewerControl*.cs   # incl. .Continuous.cs (continuous scroll)

Excise.App.Tests/
├── Integration/
│   ├── GuiRedactionSimulationTests.cs  # GUI workflow simulation
│   ├── ComprehensiveRedactionTests.cs  # Full redaction tests
│   └── ...
├── Unit/
│   ├── CoordinateConverterTests.cs     # Coordinate math tests
│   └── ...
├── UI/
│   └── HeadlessUITests.cs              # UI integration tests
├── Security/
│   └── ContentRemovalVerificationTests.cs
├── Utilities/
│   ├── TestPdfGenerator.cs
│   └── PdfTestHelpers.cs
└── Excise.App.Tests.csproj

Documentation:
├── README.md                       # User-facing documentation
├── CLAUDE.md                       # This file - AI assistant guidelines
├── REDACTION_AI_GUIDELINES.md      # AI safety guidelines for redaction
└── LICENSES.md                     # Dependency licenses
```

**The rendering tools CI job provisions all six renderers (#935, re-checked
2026-08-15).** It is easy to read "six reference renderers" as "six independent
oracles corroborate our rendering"; that is true only for the
`Rendering (Linux, with reference tools)` job and for a local checkout where
the optional tools/corpora are installed.

- **mutool, pdftocairo, Ghostscript, pdftoppm** — run wherever the binary is on
  PATH, which is every fully provisioned developer box and the rendering-tools
  CI job.
- **PDFium and PDFBox** — real oracle tests exist (`PdfiumOracleSmokeTests`,
  `PdfBoxOracleDifferentialTests`: an ink-agreement comparison plus a
  page-selection regression from #868). ⚠️ Two classes share "Pdfium...
  Renderer" in their name and only one of them is the oracle:
  `PdfiumOracleSmokeTests` calls `PdfiumNativeReferenceRenderer`, not
  `PdfiumReferenceRenderer` (that one's only current caller is a unit test for
  its argument-building helper — see the file map's per-class use counts,
  which is exactly the kind of drift `check-doc-claim-freshness.sh` (#936) now
  re-derives instead of trusting). The rendering-tools CI job downloads
  the smoke corpus, the PDFBox jar, and a prebuilt PDFium library before running
  those tests. Locally, use `scripts/download-smoke-corpus.sh`,
  `scripts/download-pdfbox.sh`, and `scripts/download-pdfium.sh`.

⚠️ **This entry said the opposite until 2026-08-12, and the wrong half was the
actionable half.** It claimed PDFBox was "referenced by zero test files", that
"no pdfium binary is ever invoked by a test", and — the damaging part — that
"setting `EXCISE_PDFIUM_TEST` / `EXCISE_PDFBOX_JAR` changes nothing for the test
suite". That was true when written and is now false: setting them turns on two
more independent oracles. Anyone who read this and skipped the setup got less
corroboration than was available, which is the precise opposite of what the
no-self-oracle rule is for.

So: use the rendering-tools CI job or a fully provisioned local checkout when
renderer work needs the wider quorum. Do not restate either the count or the
"changes nothing" claim from memory — check
`Excise.Rendering.Tests/Differential/` and `tests/skip-allowlist/`.

### Skia-origin differences are registered, not re-triaged (#1011)

excise rasterises through SkiaSharp **by choice**. A difference that originates
in Skia's own scan conversion is an ACCEPTED LIMITATION: not fixed, not
compensated for (no threshold tuning, no per-fixture special cases), not
re-triaged — excise is judged against Skia's actual behaviour. The one
exception is a Skia-triggered process death or hang (#363, #985), which must be
contained, never emulated.

Which differences those are is **data**: `tests/skia-rasterisation-register.json`,
gated by `SkiaRasterisationRegisterTests`. Every row carries the evidence that
**Skia received correct geometry** — the load-bearing field, and what separates
an accepted limitation from an open bug ("we could not explain it" is a bug, not
a row). The gate re-measures excise AND the oracles, so a row that stops
reproducing FAILS and must be deleted rather than left standing to excuse a
future defect.

⚠️ Read it before re-deriving one. The register's `notASkiaDifference` list
already holds one: #1011's own "excise's 1 pt link border inks 800 px where
poppler inks 400" does **not** reproduce — re-measured 2026-08-16, both ink 400
at an identical bbox; the 800 and the 400 are the poppler counts of two
different policy rows (2 pt and 1 pt).


## Security Notes

This redaction implementation:
- ✅ Removes content from PDF structure (not just visual covering)
- ✅ Handles text, graphics, and images
- ✅ Scrubs document-level text carriers by default — `/Info`, XMP `/Metadata`,
  `/Outlines` bookmark titles, annotation `/Contents`, and link-action `/A /URI`
  (#608 + #1155, `PdfDocumentSanitizer`, `SanitizeMetadata = true`; indirect
  string carriers are resolved before reading — the #1155 gap); `RemoveAllMetadata`
  strips them wholesale. ⚠️ #1168 tracks the remaining URI-action locations
  (outline `/A`, catalog `/OpenAction`, `/AA`); #1169 tracks the per-carrier
  policy UX (stripping a term from a known URL can REVEAL it).
- ✅ Scrubs the structure tree (`/ActualText`, `/Alt`, `/E`) (#636 + #1155)
- ✅ Scrubs embedded files/attachments **by default** in the GUI redaction-copy
  flow — `RedactedCopySafetyService` (`ScrubAttachments = true`) →
  `PdfDocument.ScrubMetadata(scrubAttachments: true)` /
  `ScrubEmbeddedFiles()` removes `/Catalog/Names/EmbeddedFiles` and the `/AF`
  associated-files arrays (#467). At the lower `RedactionService` level the
  same wholesale strip is under `RemoveAllMetadata`; the default there is the
  targeted `SanitizeMetadata` term scrub.
- ✅ Leaves no recoverable prior revisions in the redacted **output** — it is a
  fresh, fully-rewritten file (not an incremental update), so incremental-update
  prior-revision recovery does not apply to a excise-redacted copy. Verified by
  #586's previous-revision recovery tests.
- ❌ The **viewer** does not expose or roll back prior revisions of an
  incrementally-updated *input* (a viewing-feature gap, not a redaction leak).

For scorched-earth output, set `RemoveAllMetadata` and flatten forms first.
(Historical note: the old "does NOT remove attachments / does NOT handle
revision history" bullets here were stale — attachment scrub shipped in #467
and redacted output is fresh-rewrite; corrected 2026-07 after a doc-accuracy
audit.)

## Current Status

For the authoritative, version-by-version status see `CHANGELOG.md` and the
GitHub Releases page (`gh release list`). The current line is **v2.x**: the PDF
stack is pure-.NET and excise-owned (Excise.Core / Excise.Rendering / Excise.Ocr) and
the legacy PdfPig/PDFsharp/PDFtoImage dependencies were removed in v2.0. Do not
hard-code a "current release" version into this file — it goes stale; check the
changelog instead.

**Glyph-Level Redaction Implementation:** ✅ Complete

#### Implementation Files

**Glyph-Level Redaction** (`Excise.Core/Text/Segmentation/`):
- ✅ `GlyphRemover.cs` - Orchestrates glyph-level redaction
- ✅ `LetterFinder.cs` - Text-based letter matching (issue #90 fix)
- ✅ `OperationReconstructor.cs` - Rebuilds BT/Tf/Tj blocks with positioning
- ✅ `PdfPageRedactionExtensions.cs` - `page.RedactArea(rect)` / `RedactAreas(rects)` entry points

**GUI Integration** (`Excise.App/`):
- ✅ `Services/RedactionService.cs` - Unified area + text redaction; mirrors the
  rewritten content stream onto the rendered page
- ✅ `ViewModels/MainWindowViewModel.Scripting.cs` - Scripting surface

The separate `Excise.App.Redaction` library (the PdfPig/PDFsharp-based
`TextRedactor` engine and its `pdfer` CLI) was removed once both the
area-click and scripting paths were unified onto Excise.Core.

⚠️ A **second, unrelated** class of the same name lived in
`Excise.Core/Operations/TextRedactor.cs` until #928 deleted it (2026-08-12). It
was unreachable from production, and its docstring promised glyph-level removal
while `RemoveIntersecting` deleted whole operators. Two classes sharing a name,
one of them mis-describing the project's core guarantee, is why the name is
called out here rather than quietly dropped.

## Task Tracking and GitHub Issues

**IMPORTANT**: This project uses GitHub Issues for ALL task tracking, feature requests, bugs, and enhancements.

### Rules for Task Management

1. **DO NOT add TODO comments** in code
   - ❌ Bad: `// TODO: Add error handling`
   - ✅ Good: Create GitHub issue, reference in code: `// See issue #25`

2. **DO NOT create scattered enhancement lists** in documentation
   - ❌ Bad: Adding "Future Enhancements" sections to docs
   - ✅ Good: Create GitHub issues with proper labels

3. **DO reference GitHub issues** when relevant
   - In code comments: `// Handles deleted files - See issue #25`
   - In documentation: `Window position/size persistence is tracked in issue #23`
   - In commit messages: `Fixes #17` or `Addresses #19`

4. **ALWAYS create issues proactively** when you identify:
   - Bugs or problems
   - Enhancement opportunities
   - Technical debt
   - Documentation gaps
   - Test coverage needs

### GitHub Issue Labels

The project uses standardized labels (see `scripts/setup-github-labels.sh`):

**Type Labels** (GitHub defaults):
- `bug` - Something isn't working
- `enhancement` - New feature or request
- `documentation` - Improvements to docs
- `security` - Security concerns
- `question` - Further information needed

**Component Labels** (architecture-specific):
- `component: redaction-engine` - Content stream parsing, glyph removal
- `component: pdf-rendering` - PDFium, image rendering, caching
- `component: ui-framework` - Avalonia, XAML, bindings, ReactiveUI
- `component: text-extraction` - Text extraction, OCR, search
- `component: file-management` - Open/save, recent files, document state
- `component: clipboard` - Copy/paste, clipboard history
- `component: verification` - Signature/redaction verification
- `component: coordinates` - PDF/screen coordinate systems

**Priority Labels**:
- `priority: critical` - Blocks usage, data loss, security
- `priority: high` - Important but not blocking
- `priority: medium` - Nice to have
- `priority: low` - Future consideration

**Effort Labels**:
- `effort: small` - < 1 hour
- `effort: medium` - 1-4 hours
- `effort: large` - > 4 hours

**Other Labels**:
- `status: blocked` - Waiting on something else
- `good first issue` - Easy for new contributors
- `help wanted` - Community input needed
- `platform: linux/windows/macos` - Platform-specific issues

### Creating Issues via CLI

```bash
# Create a new issue
gh issue create \
  --title "Add dark mode support" \
  --body "Description of the feature..." \
  --label "enhancement,component: ui-framework,priority: medium"

# View all issues
gh issue list

# View issues by label
gh issue list --label "priority: high"

# Close an issue
gh issue close 42 --comment "Fixed in PR #43"
```

### Issue References in Code

When code relates to a known issue, add a comment:

```csharp
// File existence check for Recent Files
// See issue #25 for enhancement: show user-facing error dialog
if (!System.IO.File.Exists(filePath))
{
    _logger.LogWarning("Recent file not found: {FilePath}", filePath);
    return;
}
```

### Current High-Priority Issues

**Do not hard-code an issue list here — it goes stale and misleads.** (#95, #96
and #87 sat in this section long after they were closed, and two separate agents
planned work off them.) Query it live:

```bash
gh issue list --label "priority: critical,priority: high" --state open
gh issue list --label "track: redaction-trust" --state open   # correctness first
gh issue list --label "track: daily-driver"    --state open   # usability second
```

The roadmap lives in numbered milestones, ordered by the sequence they should be
done in. As of 2026-08-16 they group by **ROOT CAUSE** — the defect mechanism the
issues share — and are ordered by who gets hurt if it is left alone. The `RC`
prefix marks that generation. Grouping by cause rather than by symptom is what
surfaced the ones worth doing first: five separate crash and escape bugs turned
out to be one missing contract, and three shipped defects turned out to be the
same two parsers drifting apart.

Do not hard-code the list here — it has gone stale THREE times, and the third
time was the paragraph you are reading warning about the first two. Query it:
`gh api repos/marctjones/excise/milestones --jq '.[] | "\(.number)\t\(.title)"'`.

⚠️ Numbering generations do not get reused, deliberately. `RC*` supersedes a
plain-numbered `N.` scheme, which superseded another `N.` scheme, which
superseded `R6/R9/R10`. Old milestones are CLOSED, never renamed or deleted, so
a reference in an old issue or commit still resolves to the thing it meant. If
you find a bare number like "milestone 2" in prose anywhere, distrust it: it has
meant three different things.

⚠️ An earlier version of this line said the two were coupled through #899 —
"redaction completeness is bounded by extraction coverage, so #899 is also the
largest untracked dependency of milestone 2". **That was wrong, and #899 itself
says so.** The general rule (redaction cannot remove what extraction cannot
read) is real and is documented under Limitations, but #899 is NOT an instance
of it: its letter stream is COMPLETE (3107 alnum chars on the worst page vs
mutool's 2945), the loss happens later when letters are assembled into a string,
and `RedactText` matches through the letter/search path rather than `page.Text`.
The issue verified this directly — redacting `1095-A` removed 9 of 9,
mutool-confirmed, including the page whose text output cannot see the string.

The lesson is the one #936 is about: a plausible coupling, asserted from the
shape of two issues rather than read from the evidence already in them.

View all: `gh issue list --label "priority: high,priority: critical"`

### Using Discussions for Research and Ideas

GitHub Discussions is enabled for collaborative research, ideas, and questions that don't fit the issue tracker. However, the GitHub CLI doesn't support creating Discussions directly, so we use a hybrid approach:

**When to use Discussions vs Issues:**
- **Discussions:** Research questions, ideas, open-ended exploration, lab notes
- **Issues:** Bugs, features, tasks with clear completion criteria

**Workflow:**
1. Create research issues with `question` label (like #97)
2. When ready for community input, manually convert to Discussion on GitHub
3. Reference the Discussion in related issues

**Checking for Research Topics:**
```bash
# List research/question issues
gh issue list --label "question"

# Check if any are ready to convert to Discussions
# Look for issues with active conversation but no clear action items
```

**Important:** When reaching a stable milestone (like v1.3.0), review Discussions for:
- Ideas that have crystallized into actionable features
- Research findings that inform next steps
- Community feedback on direction

### Bulk Issue Management

Scripts are available for managing issues:
- `scripts/setup-github-labels.sh` - Create all standardized labels
- `scripts/import-github-issues.sh` - Bulk import issues from backlog

## Knowledge Management Strategy

**IMPORTANT**: excise uses a four-tier content organization system across Wiki, Discussions, Issues, and Markdown files.

### The Four-Tier System

**Tier 1: Wiki** (Educational, Timeless Reference)
- **Purpose**: Explain concepts, file formats, algorithms, theory
- **Content**: PDF structure, redaction theory, coordinate systems, content streams
- **Audience**: Anyone learning about PDF editing concepts
- **Lifespan**: Timeless - updated when understanding changes
- **Examples**: "PDF Content Streams", "Glyph-Level Redaction", "PDF Coordinate Systems"

**Tier 2: Discussions** (Feedback, Ideas, Lab Notes)
- **Purpose**: Unstructured thoughts, feedback, ideas, Q&A, experiment results
- **Content**: Lab notebooks, feature ideas, usage questions, test findings
- **Audience**: Developers, contributors, future collaborators
- **Lifespan**: Permanent but evolving - stays open for ongoing conversation
- **Examples**: "Lab Notebook: Week of Dec 22", "Idea: Batch Redaction UI", "Corpus Test Results"

**Tier 3: Issues** (Actionable Tasks)
- **Purpose**: Track bugs, features, and tasks with clear completion criteria
- **Content**: Bugs to fix, features to implement, tests to add
- **Audience**: Developers implementing changes
- **Lifespan**: Temporary - closed when completed
- **Examples**: "Fix coordinate conversion (#25)", "Bug: Text extraction fails (#66)"

**Tier 4: Markdown Files** (Code Documentation)
- **Purpose**: Document code architecture, API, project-specific guides
- **Content**: README, CLAUDE.md, REDACTION_AI_GUIDELINES.md
- **Audience**: Developers working with the codebase
- **Lifespan**: Version-controlled - updates with code changes
- **Examples**: "README.md", "CLAUDE.md", "REDACTION_ENGINE.md"

### Decision Matrix: Where Does Content Go?

| Content Type | Wiki | Discussion | Issue | Markdown |
|--------------|------|------------|-------|----------|
| **PDF format spec** | ✅ Primary | - | - | Reference |
| **Algorithm theory** | ✅ Primary | - | - | - |
| **Bug to fix** | - | - | ✅ Primary | - |
| **Feature to implement** | - | Discussion→ | ✅ Primary | - |
| **Research question** | Reference | Discussion→ | ✅ Primary | - |
| **Unstructured thoughts** | - | ✅ Primary | - | - |
| **Feature idea (unvalidated)** | - | ✅ Primary | →Issue | - |
| **Test results** | - | ✅ Primary | Reference | Reference |
| **Usage question** | Reference | ✅ Primary | - | - |
| **Code API docs** | - | - | - | ✅ Primary |
| **Lessons learned** | ✅ Primary | ✅ Initial | - | - |

### Content Migration Guidelines

**FROM Issues TO Discussions**:
Migrate if issue is:
- ❌ Not actionable (no clear completion criteria)
- ❌ Open-ended research without specific goal
- ❌ Ideas without implementation plan
- ❌ Placeholder for "someday maybe"

**FROM Discussions TO Issues**:
Convert when discussion leads to:
- ✅ Specific, actionable task
- ✅ Clear success criteria
- ✅ Decision to implement

**FROM Discussions TO Wiki**:
Migrate when discussion crystallizes into:
- ✅ Documented understanding
- ✅ Educational reference material
- ✅ Timeless knowledge

## Long-Running Commands

**IMPORTANT**: Avoid running long-running commands directly in Claude Code. Instead, create scripts for the user to run in a separate terminal.

### Script Runner Pattern

For long-running tests or builds, use the script runner pattern:

```bash
# Run tests with logging (user runs in separate terminal)
./scripts/run-tests.sh | tee logs/test_$(date +%Y%m%d_%H%M%S).log

# Run corpus tests with logging
./scripts/run-corpus-tests.sh 2>&1 | tee logs/corpus_$(date +%Y%m%d_%H%M%S).log
```

### Creating Scripts for Long-Running Tasks

When you need to run something that takes >30 seconds:

1. **Create a shell script** in `scripts/` with the command
2. **Add logging** with `tee` to capture output
3. **Tell the user** to run it in a separate terminal
4. **Access logs** later via the logged output file

Example script structure:
```bash
#!/bin/bash
# scripts/run-long-task.sh
set -e
LOG_DIR="$(dirname "$0")/../logs"
mkdir -p "$LOG_DIR"
LOG_FILE="$LOG_DIR/task_$(date +%Y%m%d_%H%M%S).log"

echo "Logging to: $LOG_FILE"
echo "Run this in a separate terminal:"
echo "  ./scripts/run-long-task.sh 2>&1 | tee $LOG_FILE"

# Actual command here
dotnet test --logger "console;verbosity=detailed"
```

### Available Test Scripts

- `scripts/test.sh` - Run all unit tests
- `scripts/run-corpus-tests.sh` - Run veraPDF corpus tests (long-running)
- `scripts/verify-true-redaction.sh` - Verify redaction removes content

## Test PDF Corpus

**IMPORTANT**: Test PDFs from PDF Association are NOT checked into git due to licensing concerns.

### Downloading Test PDFs

Run the download script to fetch test PDFs locally:

```bash
./scripts/download-test-pdfs.sh
```

That script fetches the two PDF Association corpora. **The corpus rendering
scan covers four**, and each has its own script — `download-test-pdfs.sh` alone
leaves you with half the coverage:

| corpus | files | script | what it is |
|---|---|---|---|
| veraPDF | 2694 | `download-test-pdfs.sh` | PDF Association PDF/A + PDF/UA conformance |
| pdf.js | 685 | `download-pdfjs-corpus.sh` | Mozilla's renderer regression history |
| PDFium | 331 | `download-pdfium-corpus.sh` | Chrome's renderer regression history |
| Isartor | 205 | `download-test-pdfs.sh` | PDF Association PDF/A-1 violation suite |

`./scripts/check-test-prereqs.sh` reports which are present. Files land in
`test-pdfs/`, which is gitignored.

### Running Corpus Tests

Two different things share the word "corpus":

**1. The xunit corpus tests** — smoke corpus of real-world government PDFs:

```bash
dotnet test Excise.Rendering.Tests --filter "FullyQualifiedName~Corpus"
```

**2. The corpus rendering scan (#862)** — renders page 1 of all 3,915
documents and classifies each PASS / PASS_ONE / DIFF / MALFORMED_PDF / …
against up to five independent oracles, then fails when a page departs from
its checked-in expectation manifest:

```bash
./scripts/run-exploratory-corpus.sh --corpus test-pdfs/pdfium \
    --page-mode first --extra-oracles all \
    --expectation-manifest tests/corpus-expectations-pdfium.tsv
```

All four run under `scripts/run-full-suite.sh --everything`.

**Do not pin a scan result without triaging it.** The manifests are generated
by `scripts/update-corpus-expectations.sh`, which writes every status verbatim
— including statuses that record an excise bug. Run
`scripts/triage-corpus-nonpass.sh <corpus>` first: it splits non-PASS pages
into "excise rendered" (an agreement question), "corroborated refusal" (no
renderer managed it, so refusing is correct), and "excise-side gap" (an oracle
rendered it and excise did not — a defect that must be filed, not pinned).
It also flags credential-blocked pages, where "no oracle rendered either" only
means nobody had the password (see `tests/corpus-passwords.tsv`).

**Two verdicts in the scan are decided by the ORACLE MAJORITY, not by one
oracle** (#907, #932) — both were single-oracle rules and both mis-reported in
the same direction:

- **A refusal is classified from the oracle evidence.** `AGREED_REFUSAL` means
  excise refused and nothing else rendered it either (correct, pinnable);
  `EXCISE_SIDE_GAP` means an oracle rendered a page excise refused (a defect —
  pin it only with an issue number in the note column). Before this, both were
  `DECODE_ERROR`, so the gate could not tell them apart and defended the
  defect. Credential-blocked and load-dependent (`TIMEOUT`) refusals are
  deliberately left alone.
- **`MISSING_CONTENT` needs a majority.** A tile counts as missing only when
  most of the oracles that rendered inked it and excise did not. Scoring
  against the most-inked oracle elected the outlier by construction: on a page
  whose only content is an annotation with no `/AP` (§12.5.5 makes the
  appearance optional, and the oracles disagree in both directions) excise read
  as defective for agreeing with everyone else. Oracles that rasterized a
  different page box — pdftocairo renders the `/MediaBox` where excise, mutool
  and pdfium render the `/CropBox` — get no vote, because their tiles address a
  different part of the page.

Majority scoring is not a loosening. It caught two defects the most-inked rule
passed (#970, #972).

**A majority needs three, so the scan escalates to get them** (#976). Both
filters above shrink the pool `MISSING_CONTENT` votes over — normally only the
three primaries run, and an oracle that rasterized a different page box gets no
vote — and at two oracles a 1-1 split is not a majority, so the check returns
**no verdict at all**, reporting (0 missing, 0 inked), which is byte-identical
to a clean page. `bug1844576.pdf` measured both ways: primaries only,
`comparableOracles=2` and `referenceInkedTiles=0` (no opinion); with escalation,
`comparableOracles=3` and `referenceInkedTiles=65`, missing 0 (a real verdict).
So Ghostscript/PDFBox now also run when the comparable pool is under three, not
only when the page metrics disagree. Escalation decides who is right; it cannot
manufacture agreement, because three agreeing primaries stay a majority of four
and of five. It does not GUARANTEE a verdict either (four oracles can split
2-2), so the pool is recorded per page (`comparableOracles`) and per scan
(`summary.localityQuorumShortCount`) instead of being assumed away.

**The contracts and the manifests are two descriptions of the same page, and
they are now compared** (#977). `test-pdfs/rendering-contracts/**` pins
`ExpectedRawStatus` per page; `tests/corpus-expectations*.tsv` pins it for page
1. Nothing crossed them until #977, and 65 pages had drifted — all 65 stale
contracts, mostly PASS_ONE pins predating the majority-PASS rule (#865). The
check runs in t0 (`Contracts_AgreeWithTheCorpusExpectationManifests`, or
`scripts/check-contract-manifest-agreement.sh` to see the whole list); it needs
no corpus and no renderer. A `*` on either side is compatible with anything —
those wildcard rows are hand-written for load-dependent pages and must not be
"fixed".

`--extra-oracles all` is close to free: PDFBox and PDFium only run on pages
where mutool and pdftocairo have already disagreed. Use it — a second opinion
costs nothing on the 97% of pages that pass, and the escalation lands exactly
where a single oracle is least trustworthy.
