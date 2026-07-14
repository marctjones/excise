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

This is a cross-platform PDF editor built with **C# + .NET 10 + Avalonia UI** (MVVM architecture). The application runs on Windows, Linux, and macOS, providing PDF viewing, page manipulation, and content-level redaction capabilities. As of v2.0 the PDF stack is pure-.NET and pdfe-owned (Pdfe.Core parser/writer, Pdfe.Rendering SkiaSharp renderer, Pdfe.Ocr); the legacy PdfPig/PDFsharp/PDFtoImage dependencies have been removed.

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
Pdfe.Core/Text/Segmentation/GlyphRemover.cs            ← orchestrates glyph-level removal
Pdfe.Core/Text/Segmentation/LetterFinder.cs            ← text-based letter matching (issue #90)
Pdfe.Core/Text/Segmentation/OperationReconstructor.cs  ← rebuilds BT/Tf/Tj blocks without removed glyphs
Pdfe.Core/Content/ContentStreamParser.cs               ← parses content-stream operators
Pdfe.Core/Content/ContentStreamWriter.cs               ← serializes operators back to bytes
PdfEditor/Services/RedactionService.cs                 ← GUI orchestration; mirrors the rewrite onto the page
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
| #637 | A page our own **extractor cannot read** (IRS 1040 p47: pdfe sees 471 chars, mutool sees 3192) | ✅ clean |

The third is the general case, and the rule to remember:

*(The #637 p47 anecdote no longer reproduces — pdfe now extracts 3233 chars
there vs. mutool's 3192 — but the general failure mode is not fixed, it's now
**measured**: #645's corpus-wide gate (332 pages / 12 fixtures, including the
checked-in CJK Type0 fixture that names #645's second blind spot) shows no
broad under-extraction (aggregate coverage 102.6% of mutool's Unicode
letter/digit count — deliberately not ASCII-folded, so it can't silently
cancel out CJK/accented-text loss on both sides), but per-page content
similarity falls to 0.75 on 83 Type0/CID-font pages of
`irs-1040-instructions.pdf` (coverage >1.0 there — over-extraction, not
blindness: fonts decode fine, a marked-content `/Artifact` leak pollutes
the extraction ahead of the correct real content; tracked as #649, not a
font-resolution bug). See `tests/extraction-parity/baseline.json` and
`scripts/check-extraction-parity.sh`. Anecdote → measurement is the point of
#645: don't restate a specific number here without re-running the gate.)

> **Redaction completeness is bounded by extraction coverage. pdfe cannot redact
> what pdfe cannot read — and it will report success anyway.**

So a redaction test MUST also assert at least one of:

```csharp
// 1. CARRIER-AGNOSTIC — search the SAVED BYTES (ASCII *and* UTF-16BE).
//    If the secret is anywhere in the file, in any carrier, this fails.
var saved = SaveToBytes(redactedPdf);
(Encoding.ASCII.GetString(saved) + Encoding.BigEndianUnicode.GetString(saved))
    .Should().NotContain("REDACTED_TEXT");

// 2. INDEPENDENT EXTRACTOR — a tool that is not pdfe.
MutoolTextExtractor.ExtractPage(path, page).Should().NotContain("REDACTED_TEXT");

// 3. INDEPENDENT RENDERER — an ink differential over the redacted region.
//    Text can be gone from every text carrier and still be VISIBLE (vector
//    paths, raster pixels). Extraction cannot see ink; a renderer can.
InkFractionIn(after, box).Should().BeLessThan(0.001);   // was > 0.02 before
```

**The principle, learned the hard way:**

> **A tool must not be its own oracle for the property it exists to guarantee.**
> pdfe confirming that pdfe removed the text proves only that its bugs are
> self-consistent.

Working examples of all three:
- `Pdfe.Core.Tests/Text/Segmentation/StructureTreeRedactionLeakTests.cs` (saved bytes)
- `Pdfe.Rendering.Tests/Differential/RedactionReferenceVerificationTests.cs` (independent extractor + ink differential)
- `Pdfe.Rendering.Tests/Differential/RedactionRoundTripTests.cs` (corpus, both ways)

**See `REDACTION_AI_GUIDELINES.md` for complete documentation.**

## Build and Run Commands

### Basic Development

```bash
# Restore packages (required after cloning or adding dependencies)
cd PdfEditor
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
cd PdfEditor.Tests
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

Before tagging or describing a release, run `scripts/verify-doc-claims.sh` and
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
- `PdfRenderService.cs` - PDF-to-image rendering (uses Pdfe.Rendering / SkiaSharp)
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
PDF Libraries (Pdfe.Core for parsing/redaction/save, Pdfe.Rendering for Skia render, Pdfe.Ocr for OCR)
```

When modifying the UI, update the XAML and bind to ViewModel properties. Never put business logic in code-behind.

## Redaction Engine Architecture

This is the most complex part of the codebase. As of v2.0 the redaction engine
lives in **`Pdfe.Core`** (pure .NET), not the GUI project. The authoritative
glyph-level pipeline is in `Pdfe.Core/Text/Segmentation/` (GlyphRemover,
LetterFinder, OperationReconstructor) and the content-stream machinery in
`Pdfe.Core/Content/` (ContentStreamParser, ContentStreamWriter). The GUI's
`PdfEditor/Services/RedactionService.cs` only orchestrates and mirrors the
rewrite onto the rendered page — see the "Critical Files" box at the top of
this document for the canonical paths.

The component descriptions below are kept as a conceptual reference for the
parse → filter → rebuild → replace → draw flow; the class names map onto the
`Pdfe.Core` types above (e.g. ContentStreamBuilder → ContentStreamWriter).

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

Located in `PdfEditor/PdfEditor.csproj`:

**UI Framework:**
- Avalonia 12.0.4 (cross-platform XAML UI)
- ReactiveUI 23.2.27 (MVVM framework)
- FluentAvaloniaUI 3.0.0-preview2 (Fluent theme/controls)

**PDF Stack (pdfe-owned, pure .NET):**
- Pdfe.Core - parser, writer, content streams, fonts, encryption, glyph-level redaction
- Pdfe.Rendering - SkiaSharp-based renderer (replaces PDFium)
- Pdfe.Ocr - shells out to system tesseract

**Supporting:**
- SkiaSharp 3.119.4 (MIT) - 2D graphics / rasterization
- BouncyCastle.Cryptography 2.6.2 (MIT) - crypto primitives for encryption

The legacy PdfPig / PDFsharp / PDFtoImage dependencies were removed in v2.0.
All remaining licenses are permissive (MIT/Apache 2.0/BSD-3), no copyleft restrictions.
SkiaSharp ships a native component but is MIT-licensed.

## Test Infrastructure

Located in `PdfEditor.Tests/`:

**Framework**: xUnit 2.5.3 with FluentAssertions 6.12.0

**Test Count** (2026-07-13): ~7,600 across five suites — Pdfe.Core ~3,180,
Pdfe.Rendering ~3,420, PdfEditor ~905, Pdfe.Cli 86, Pdfe.Avalonia 10.
Don't hard-code a number here; it goes stale. Run the suites.

⚠️ **`PdfEditor.Tests` is SERIAL BY DESIGN** — `[assembly: CollectionBehavior(
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
4. Add corresponding tests in `PdfEditor.Tests/`

## Debugging Notes

### Redaction Not Working

1. Check console output - parser logs operations found/removed
2. Verify coordinate system (PDF vs Avalonia Y-axis)
3. Check bounding box calculations in `TextBoundsCalculator`
4. Enable verbose logging in `ContentStreamParser`

### PDF Rendering Issues

- Rendering goes through Pdfe.Rendering (SkiaSharp); SkiaSharp carries its own native component
- Check `PdfRenderService.cs` (GUI) and `Pdfe.Rendering/SkiaRenderer.cs` for rendering code

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
   #645) — ⚠️ the most important entry in this file. Where pdfe's extractor
   cannot read text, `RedactText` cannot match it, does not remove it, **and
   reports success**. Corpus-wide measurement (332 pages / 12 fixtures —
   10 real-world government PDFs plus checked-in CJK/Type0 and
   scrambled-glyph-order edge cases, `scripts/check-extraction-parity.sh`):
   aggregate coverage 102.6% of mutool's Unicode letter/digit count (counted
   per-script, not ASCII-folded, specifically so CJK/accented-text loss can't
   cancel out invisibly on both sides of the ratio). Both blind spots #645 was
   written to measure — the p47-style under-extraction and the CJK/Type0
   "extracts as empty" case (`RealWorldSearchTests.CjkFixture_*`) — are
   currently clean on their fixtures. The live finding is per-page *content
   similarity*, which drops to 0.75 on 83 Type0/CID-font pages (all
   `/Encoding /Identity-H`) of `irs-1040-instructions.pdf` — but coverage on
   those same pages is **>1.0**, i.e. over-extraction: glyphs decode
   correctly, and a marked-content `/Artifact` running-header leak is
   prepended ahead of the (correct) real content. That is a content-stream
   marked-content filtering gap (`ContentStreamParser`/`TextExtractor` not
   honoring `/Artifact`-tagged `BMC`/`BDC`/`EMC` spans), tracked as **#649**
   — it is a font-resolution non-issue on these pages (fonts decode fine) and
   is explicitly NOT part of #513's scope; don't let #513 work chase this
   number, it won't move it. (Checked that "CJK is
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
3. **Encryption is decrypt-only** (#624) — the writer emits no `/Encrypt`, so
   redacting a password-protected PDF still returns an **unprotected** copy.
   As of #638 this is no longer silent: the GUI asks for explicit
   confirmation before any save that would drop source encryption, and the
   CLI/batch-automation paths hard-fail unless `--allow-decrypt` /
   `allowDecrypt: true` is passed. The capability gap (#624) is unchanged —
   only the silence is fixed.
4. **`/P` permissions parsed but never enforced** (#642) — pdfe will copy text
   out of a copy-forbidden document.

**Previously listed here and now FIXED — do not re-add:**
- ~~Inline images `BI...ID...EI` not handled~~ → handled (`ContentStreamWriter.cs:39-81`).
- ~~Clipping paths `W`, `W*` not tracked~~ → tracked (`ContentStreamParser.cs:448`).
- ~~Rotated pages not supported~~ → `/Rotate` 0/90/180/270 and inherited rotation
  are honoured end-to-end (`PdfPage.ToContentStreamCoordinates`), covered by
  `RotatedPageRedactionTests`.

See GitHub issues labeled `component: redaction-engine` for enhancement tracking.

## File Locations Quick Reference

⚠️ This map was wrong for months: it pointed redaction at
`PdfEditor/Services/Redaction/*` (ContentStreamBuilder, PdfOperation,
TextBoundsCalculator, PdfGraphicsState…). **That directory does not exist.** It
all moved to `Pdfe.Core` in v2.0. If you are looking for the redaction engine,
it is in `Pdfe.Core`, not in the GUI project.

```
Pdfe.Core/                          # the PDF engine — parser, writer, redaction
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
│   ├── ContentStreamParser.cs      # parse operators (+ bounds, clip, marked content)
│   └── ContentStreamWriter.cs      # serialize operators back to bytes
├── Operations/
│   └── PdfDocumentSanitizer.cs     # /Info, XMP, outlines, annots (#608)
├── Document/                       # PdfDocument, PdfPage, PdfPageRect, coords
├── Fonts/                          # CFF, TrueType parse + subset (see #512-#515)
└── Security/                       # decrypt only — no encrypt-on-save (#624)

Pdfe.Rendering/                     # SkiaSharp renderer
└── Differential/                   # ← REFERENCE ORACLES. Use these, don't build new ones.
    ├── GhostscriptReferenceRenderer.cs
    ├── MutoolReferenceRenderer.cs
    ├── PdfiumReferenceRenderer.cs
    ├── PdftocairoReferenceRenderer.cs
    └── PdfBoxReferenceRenderer.cs

PdfEditor/                          # the Avalonia GUI (orchestration only)
├── Services/
│   ├── PdfDocumentService.cs       # load/save/page manipulation
│   ├── PdfRenderService.cs         # render to image
│   └── RedactionService.cs         # ORCHESTRATES Pdfe.Core; owns no engine logic
├── ViewModels/MainWindowViewModel.cs   # (partial: .Commands/.Search/.Forms/…)
├── Views/MainWindow.axaml(.cs)
└── Automation/CommandAccessibility.cs

Pdfe.Avalonia/                      # reusable viewer control
└── Controls/PdfViewerControl*.cs   # incl. .Continuous.cs (continuous scroll)

PdfEditor.Tests/
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
└── PdfEditor.Tests.csproj

Documentation:
├── README.md                       # User-facing documentation
├── CLAUDE.md                       # This file - AI assistant guidelines
├── REDACTION_AI_GUIDELINES.md      # AI safety guidelines for redaction
└── LICENSES.md                     # Dependency licenses
```

## Security Notes

This redaction implementation:
- ✅ Removes content from PDF structure (not just visual covering)
- ✅ Handles text, graphics, and images
- ❌ Does NOT remove PDF metadata (XMP, Info dict)
- ❌ Does NOT handle PDF revision history
- ❌ Does NOT remove embedded files/attachments

For maximum security, also remove metadata and flatten the PDF after redaction.

## Current Status

For the authoritative, version-by-version status see `CHANGELOG.md` and the
GitHub Releases page (`gh release list`). The current line is **v2.x**: the PDF
stack is pure-.NET and pdfe-owned (Pdfe.Core / Pdfe.Rendering / Pdfe.Ocr) and
the legacy PdfPig/PDFsharp/PDFtoImage dependencies were removed in v2.0. Do not
hard-code a "current release" version into this file — it goes stale; check the
changelog instead.

**Glyph-Level Redaction Implementation:** ✅ Complete

#### Implementation Files

**Glyph-Level Redaction** (`Pdfe.Core/Text/Segmentation/`):
- ✅ `GlyphRemover.cs` - Orchestrates glyph-level redaction
- ✅ `LetterFinder.cs` - Text-based letter matching (issue #90 fix)
- ✅ `OperationReconstructor.cs` - Rebuilds BT/Tf/Tj blocks with positioning
- ✅ `PdfPageRedactionExtensions.cs` - `page.RedactArea(rect)` / `RedactAreas(rects)` entry points

**GUI Integration** (`PdfEditor/`):
- ✅ `Services/RedactionService.cs` - Unified area + text redaction; mirrors the
  rewritten content stream onto the rendered page
- ✅ `ViewModels/MainWindowViewModel.Scripting.cs` - Scripting surface

The separate `PdfEditor.Redaction` library (the PdfPig/PDFsharp-based
`TextRedactor` engine and its `pdfer` CLI) was removed once both the
area-click and scripting paths were unified onto Pdfe.Core.

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

The roadmap lives in the named-track milestones (`1. Redaction Trust (blocking)`
… `10. Document Security`), ordered by the sequence they should be done in.

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

**IMPORTANT**: pdfe uses a four-tier content organization system across Wiki, Discussions, Issues, and Markdown files.

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

This downloads:
- **veraPDF Corpus** - 2,694 PDF/A test files covering PDF/A-1a/1b/2a/2b/2u/3b/4/4e/4f, PDF/UA-1/2
- **Isartor Test Suite** - PDF/A-1 conformance tests
- **Sample PDFs** - Additional challenging test cases

Files are stored in `test-pdfs/` which is gitignored.

### Running Corpus Tests

Corpus coverage now lives under `Pdfe.Rendering.Tests/Corpus/` (smoke
corpus of real-world government PDFs) and the Pdfe.Core test suite.

```bash
# Download corpus first (if not already done)
./scripts/download-test-pdfs.sh

dotnet test Pdfe.Rendering.Tests --filter "FullyQualifiedName~Corpus"
```
