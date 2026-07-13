<p align="center">
  <img src="PdfEditor/Assets/pdfe_logo.svg" alt="PDFE Logo" width="128" height="128">
</p>

# pdfe

A cross-platform PDF editor and pure-.NET PDF framework, built with **C# + .NET 10 + Avalonia UI** and shipped with **true content-level redaction**, **page organization**, **flat typewriter text editing**, **AcroForm editing/authoring**, highlight/sticky-note annotation authoring, and **PDF 2.0 conformance**.

[![Release](https://img.shields.io/github/v/release/marctjones/pdfe)](https://github.com/marctjones/pdfe/releases)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![Tests](https://img.shields.io/badge/tests-7000%2B%20passing-brightgreen)](Pdfe.Core.Tests)
[![Build](https://img.shields.io/badge/build-0%20warnings-brightgreen)](Pdfe.Core)

> The current release line has completed the everyday PDF workbench gate:
> renderer and GUI display evidence are issue-linked, PDF 2.0 renderer
> coverage is tracked by contract, and remaining advanced renderer/font work is
> explicitly deferred rather than implied as shipped. The PDF stack is
> pdfe-owned end-to-end — `Pdfe.Core` (parser/writer), `Pdfe.Rendering` (Skia),
> and `Pdfe.Ocr` (system tesseract shell) — with no third-party PDF
> dependencies. See [CHANGELOG.md](CHANGELOG.md) for full release notes.

## What's in the box

```
Pdfe.Core/             Pure-.NET PDF parser, writer, content-stream library
Pdfe.Rendering/        SkiaSharp-based renderer (text, images, paths, transparency)
Pdfe.Avalonia/         Reusable Avalonia PDF viewer control (PdfViewerControl)
Pdfe.Avalonia.Sample/  Minimal "open and view" sample app for the control
Pdfe.Ocr/              OCR via the system `tesseract` CLI + differential-OCR auditor
Pdfe.Cli/              `pdfe` command-line tool
PdfEditor/             Cross-platform Avalonia desktop app (reference consumer)
```

The libraries are usable independently — embed `Pdfe.Core` if you only need parsing and redaction, `Pdfe.Rendering` if you need page rasterization, or `Pdfe.Avalonia` to drop a PDF viewer into any Avalonia app.

### Reusable libraries (NuGet-packable)

`Pdfe.Core`, `Pdfe.Rendering`, and `Pdfe.Avalonia` are packable as a dependency-light,
pure-managed, MIT-licensed stack — a niche the .NET/Avalonia ecosystem largely lacks (the
alternatives are native PDFium or commercial SDKs):

- **`Pdfe.Avalonia`** — `<pdf:PdfViewerControl Document="…" CurrentPage="…" ZoomLevel="…" />`.
  Depends only on `Pdfe.Core` + `Pdfe.Rendering` + Avalonia + SkiaSharp. See
  [`Pdfe.Avalonia/README.md`](Pdfe.Avalonia/README.md) and `Pdfe.Avalonia.Sample`.
- **`Pdfe.Rendering`** — framework-neutral render API: `RenderPage(page, options[, ct]) → SKBitmap`
  and `RenderPageToPng(…)`. Pair with any UI (WPF/MAUI/Blazor/Uno) or a headless service. See
  [`Pdfe.Rendering/README.md`](Pdfe.Rendering/README.md).
- **`Pdfe.Core`** — parser/model/redaction. See [`Pdfe.Core/README.md`](Pdfe.Core/README.md).

Build the packages locally with `dotnet pack -c Release` (they are also attached to releases).

## Features

### Desktop app
- Open, view, navigate PDFs with smooth Skia rendering
- Page organization (add/insert, extract, remove, reorder, rotate; current page or selected pages; 90°/180°/270°)
- Text selection and copy with letter-level positions
- Find with highlights and navigation
- Zoom modes: fit width, fit page, actual size, free zoom
- Page thumbnails sidebar
- **Typewriter text** — place editable text boxes on flat PDFs, then save them as normal page content instead of annotations
- **AcroForm editing** — click text, checkbox, radio, or dropdown widgets and edit inline; save filled forms as interactive copies or create a flattened form copy
- **AcroForm authoring** — drag-rect on a page to create new fields (Text / Checkbox / Choice / Signature); auto-detect underline placeholders and empty squares as fields
- **Annotation review tools** — highlight selected text and add sticky notes as real PDF annotations
- Reveal Hidden Text — yellow highlights for structural detections (text covered by rectangles), orange for differential-OCR recoveries (text inside rasterized images)
- Digital signature inspection — checks ByteRange structure, verifies the detached CMS digest/signature over the signed bytes, and clearly reports current OS trust-chain validation limitations
- Bates numbering
- CLI-first automation with stable JSON, batch workflows, progress NDJSON, and
  AppleScript/Shortcuts, PowerShell/Power Automate, and Linux/GNOME examples
- Roslyn-based GUI scripting for developer/test automation in Debug builds; Release builds exclude it by default unless `-p:EnableScripting=true` is set

### Glyph-level redaction
**Text is removed from the PDF structure, not just visually covered.**

- Glyph-level removal — individual glyphs are excised from content streams
- Image XObject redaction — image overlays that intersect a redaction area are removed, not just blacked out
- External tools (`pdftotext`, mutool, Acrobat copy-paste) cannot recover redacted content
- Mark-then-apply workflow with red dashed previews and a Clipboard History sidebar showing what was removed
- Original protection — defaults the save dialog to `filename_REDACTED.pdf`
- Safe-to-share save path — `RedactedCopySafetyService` scrubs Info metadata, XMP metadata, and embedded files/attachments by default, then reports content-removal, metadata, attachment, and hidden-text audit status without repeating removed text
- `PdfDocument.ScrubMetadata(scrubAttachments: true)` strips Info dict, XMP, and embedded files in one call — important when redacted documents may carry the data they were redacted of in attachments (ZUGFeRD, Factur-X)
- OCG-aware — `RedactText` defaults to `includeHiddenLayers=true` so hidden optional content groups don't slip past
- Verified against real-world fixtures (CT birth certificate, government forms) at the pixel and content-stream level

### AcroForm editing & authoring
- `PdfField.SetValue(string?)` mutates `/V`, sets `/NeedAppearances`, updates `/AS` for buttons, and throws on read-only and signature fields
- `PdfField` exposes effective `/Ff` flags plus widget metadata/export values so callers can distinguish checkboxes, radio groups, combo boxes, and push buttons
- `PdfDocument.FlattenAcroForm()` bakes values into static page content, clips/wraps text to widget bounds, draws only the selected radio widget, and strips widget annotations
- `AcroFormAuthoring` extension methods: `AddTextField`, `AddCheckBox`, `AddChoiceField`, `AddSignatureField` — auto-create the AcroForm dict and `/DR/Font/Helv` on first call
- `PdfFormAutoDetector` heuristically suggests fields where the page has horizontal underlines or empty checkbox-sized outlines (Acrobat-style "Prepare Form")

### Page and annotation authoring
- Page organization is supported in the desktop app and service layer: append/insert pages from another PDF, extract the current page or selected pages, remove current or selected pages, move current or selected pages earlier/later, and rotate pages. Page-owned streams/resources/annotations are cloned into copied pages; the app warns when document-level structures such as outlines, named destinations, or AcroForm metadata may need review.
- The desktop app can highlight selected text and add sticky notes as real PDF annotations. `PdfAnnotationAuthoring` extension methods expose the same common review workflows in code: `AddTextAnnotation` for sticky notes and `AddHighlightAnnotation` for text markup highlights.

### CLI (`pdfe`)
```bash
pdfe info              <file>           [--json] [--password P]
pdfe text              <file>           [--json] [--password P]
pdfe letters           <file>
pdfe render            <file>           -o out.png  [--page N] [--dpi N] [--password P] [--json]
pdfe commands          [id]             [--json]
pdfe batch             <workflow.json>  [--json] [--progress] [--output report.json]
pdfe draw              <file>                                 # graphics-API demo
pdfe redact            <input> <output> <text>  [--case-sensitive]
pdfe fill-form         <input> <output> --field Name=Value [...] [--flatten]
pdfe add-field         <input> <output> --type T --name N --page P --rect "l,b,r,t" [--value v] [--option o]...
pdfe autodetect-fields <input> [output] [--apply]
pdfe audit             <file>           [--deep] [--json]
pdfe ocr               <file>
pdfe demo
```

`audit --deep` runs differential OCR — renders the page twice (once with overlays stripped) and diffs the OCR text — to catch words hidden inside rasterized images by an opaque overlay (the rasterized analogue of a black-box redaction).

See [`docs/AUTOMATION_API.md`](docs/AUTOMATION_API.md) for the supported
automation contract, exit codes, batch workflow schema, security boundary, and
platform examples. The public automation path is CLI-first; Release builds do
not enable a background GUI automation listener.

### Renderer coverage
The Skia renderer has been smoke-tested against a real-world corpus and is validated with a MuPDF-first differential harness. When MuPDF disagrees, the test suite escalates to Poppler and Ghostscript for second and third opinions. Known divergences are issue-linked allowlist entries; new unclassified divergences fail the differential slice.

For release-quality rendering work, pdfe also has an exploratory all-pages
corpus scanner for the pdf.js corpus. The report separates visual fidelity
results (`PASS`, `PASS_ONE`, `DIFF`) from semantic release-gate results
(`resultStatus`, `resultCategory`, and `resultReason`) and bounded non-fidelity
classifications such as malformed PDFs, unsupported encryption/compression,
decode failures, invalid page geometry, render resource limits, oracle refusal,
and timeouts. Expectation manifests keep raw scanner status stable while
allowing reviewed page-box, color-management, reference-refusal, and degenerate
fixture cases to be counted separately from real content-loss bugs. This keeps
quick-win rendering work focused on shared root causes rather than per-file
exceptions.

| PDF type | Notes |
|---|---|
| State-issued government forms (CT birth-cert, DS-82) | TJ kerning, Tw column alignment, raster backgrounds |
| SCOTUS opinions | Non-uniform Tm, Type1 PostScript subsets |
| IRS Form 1040 + Instructions | Type0/Identity-H, Acrobat-distilled, 180° footers |
| CDC VIS | Embedded TrueType, Wingdings dingbats |
| Pragmatic Bookshelf books (XEP) | 455-page multi-font CFF subsets, ZapfDingbats |
| Multilingual CJK | zh-Hans, zh-Hant, ja, ko via Noto Serif CJK |

See [`Pdfe.Rendering.Tests/Visual/`](Pdfe.Rendering.Tests/Visual) and [`PdfEditor.Tests/UI/baselines/`](PdfEditor.Tests/UI/baselines) for the regression baselines.

### Release scope and known limitations

pdfe targets an everyday PDF workbench: open, read, search/copy, annotate,
organize pages, fill/flatten forms, add flat typewriter text, audit hidden
content, and perform true content-level redaction. It is not an Acrobat Pro
replacement for prepress, color-managed print production, JavaScript workflows,
portfolio workflows, or certificate-authority trust decisions.

Current release-quality limitations are tracked in GitHub Issues and surfaced in
release notes:

- **Digital signatures** — pdfe checks ByteRange structure and verifies the
  detached CMS signature/digest over the signed bytes, but does not evaluate the
  signer certificate chain against the OS trust store yet (#466).
- **Rendering fidelity** — the current release dashboard classifies every
  contracted page as release `PASS`. Remaining non-exact rows are issue-linked
  accepted-reference matches, malformed-input/refusal classifications, or named
  accepted limitations rather than unclassified `DIFF` blockers (#491).
  Niche color/shading residuals and deeper font-model work remain tracked for
  future releases (#512, #513, #514, #515, #532).
- **Color-managed print preview** — pdfe renders DeviceCMYK through a
  deterministic screen-preview conversion, resolves `/DefaultCMYK` and ICCBased
  CMYK through managed ICC preview support, and uses document output-intent data
  in the CMYK transparency-preview paths covered by the release corpus. It is
  still not a prepress proofing engine; shade/tone-only differences are tracked
  below missing content, geometry, and unreadable-output defects.
- **Encrypted and malformed PDFs** — PDFs requiring a non-empty user password,
  some owner-password-only flows, unsupported compression filters, invalid
  geometry, and intentionally malformed xref/stream structures are classified by
  the corpus scanner rather than treated as everyday-release blockers.
- **Text-extraction parity** — redaction completeness is bounded by extraction
  coverage: `RedactText` cannot remove what pdfe cannot read, and reports
  success anyway (#637). Measured against `mutool` across 332 pages / 12
  fixtures (10 real-world government PDFs plus checked-in edge-case fixtures
  covering CJK/Type0 text and scrambled glyph order), pdfe currently extracts
  **102.6%** of mutool's Unicode letter/digit count in aggregate — counted
  per-script, not ASCII-folded, so CJK/accented-text loss would show up here
  rather than cancel out on both sides. Both blind spots #645 was written to
  measure are currently clean on their fixtures (the CJK page and the
  scrambled-order page both extract byte-for-byte against mutool), but
  per-page **content similarity** dips as low as **0.75** on 83 Type0/CID-font
  pages (all `/Encoding /Identity-H`) of `irs-1040-instructions.pdf` — but
  coverage on those same pages is **>1.0** (over-extraction, not blindness):
  fonts decode correctly, and a marked-content `/Artifact` running-header leak
  is prepended ahead of the (correctly extracted) real content. That's a
  content-stream marked-content filtering gap, tracked separately as #649 —
  not a font-resolution defect, so it is explicitly out of scope for #513.
  (The CJK fixture is clean on both the raw-text path this gate measures
  *and* the word/search path `RedactText` actually depends on —
  `RealWorldSearchTests.CjkFixture_Search_FindsLatinWord` now genuinely
  passes, not skips, confirming the paths agree rather than one extractor
  vouching for the other.) This is a checked-in,
  ratcheting floor per page, not an aspiration — see
  `tests/extraction-parity/baseline.json`, regenerate with
  `scripts/check-extraction-parity.sh --update` (requires `mutool`), and see
  #645/#513.

### PDF 2.0 conformance
All 15 conformance phases shipped:

| Phase | Feature |
|---|---|
| 3 | Standard 14 fonts via embedded core metrics |
| 4 | Image XObjects (DCT/Flate/CCITTFax) |
| 5 | Composite (Type0/CID) fonts with Identity-H/V |
| 6 | Inline images (BI/ID/EI) |
| 7 | Color spaces (DeviceRGB/CMYK/Gray, ICCBased, Indexed, CalRGB) |
| 8 | Embedded TrueType + raw-CFF/Type1C |
| 9 | CFF parser + MVP subsetter |
| 10 | Annotations (Text, Link, Highlight, Underline, StrikeOut, Squiggly, Stamp, Ink, Widget) |
| 11 | AcroForms — read, fill, flatten, author, auto-detect |
| 12 | Optional Content Groups (OCGs) + structure tree (read-only, redaction-aware) |
| 13 | Document-level embedded files / portfolios — read + scrub |
| 14 | Page labels (`/PageLabels`) and named destinations |
| 15 | Conformance harness — corpus parse + render + round-trip + redaction regression |

## Installation

### From releases

Download the latest from [GitHub Releases](https://github.com/marctjones/pdfe/releases) and run the executable for your platform.

### From source

```bash
git clone https://github.com/marctjones/pdfe.git
cd pdfe
dotnet restore
dotnet run --project PdfEditor
```

`dotnet 10.0` SDK required. No additional native dependencies — the renderer is pure SkiaSharp, the OCR auditor shells out to the system `tesseract` binary if installed (skipped gracefully if not).

## Usage

### Desktop redaction (mark-then-apply)

1. **Enable redaction mode** — toolbar button or press `R`
2. **Mark areas** — click and drag (red dashed outline = pending)
3. **Review pending marks** — sidebar shows preview text
4. **Apply** — toolbar button or `Enter` (permanent removal)
5. **Verify** — Clipboard History panel shows exactly what came out
6. **Save** — defaults to `filename_REDACTED.pdf`

Multiple areas across multiple pages can be marked and applied as a single batch.

### Typewriter text on flat PDFs

1. Click **✎ Type** in the toolbar.
2. Click or drag on the page to place a text box.
3. Type, move, resize, or delete the pending box before saving.
4. Save to flatten the text into the PDF page content. When the open file is still the original, pdfe routes the save through **Save a Copy** so the original is preserved.

### Form fill (existing AcroForm)

1. Open a PDF with form fields. Each field becomes an inline editor on the page: text fields use text boxes, choice/radio fields use selectors, and checkboxes use checkboxes.
2. Edit a value. Single-line text commits on Enter or focus loss; multiline text commits on Ctrl+Enter or focus loss; Escape restores the last committed value.
3. Use **Save Filled Copy** / **Save As** to preserve interactive form fields and values.
4. Use **Flatten Form** to create a copy where form values are baked into static page content and widget annotations are removed.

### Form authoring (create new fields)

1. Click **📋 Add Field** in the toolbar (or call `doc.AddTextField(...)` etc. from code).
2. Pick a field type from the combo (Text / Checkbox / Choice / Signature).
3. Drag a rect on the page — the new field appears immediately and is editable.
4. **🪄 Auto-detect** scans the current document for likely field positions — long horizontal strokes (text-field underlines) and small square outlines (checkboxes) — and creates them in one click.

### Authoring PDFs from scratch (high-level)

`Pdfe.Core.Authoring.PdfDocumentBuilder` is a friendly, flow-layout writer that
handles word-wrap, pagination, and field placement so you never touch raw
coordinates. It sits on top of the low-level `PdfGraphics` / `AcroFormAuthoring`
API (drop down to those any time via `.Custom(...)` or `.Build()`).

```csharp
using Pdfe.Core.Authoring;

byte[] pdf = PdfDocumentBuilder.Create()           // US Letter, 1-inch margins
    .Heading("Membership Application")
    .Paragraph("Please complete all required fields.")
    .HorizontalRule()
    .KeyValue("Date", "2026-06-05")
    .TextField("Full name", "fullName", required: true)
    .CheckBox("I agree to the terms", "agree")
    .Dropdown("Tier", new[] { "Basic", "Standard", "Premium" }, "tier", "Standard")
    .TextField("Comments", "comments", multiline: true, lines: 4)
    .Table(new[]
    {
        new[] { "Item", "Qty", "Price" },
        new[] { "Widget", "3", "$9.00" },
    }, columnWeights: new[] { 2.0, 1.0, 1.0 }, headerRow: true)
    .SaveToBytes();                                 // or .Save("form.pdf")
```

The result is a real, fillable AcroForm PDF: text is extractable, content
flows onto new pages automatically, and the form fields are live in any viewer.
Styling is via the immutable `TextStyle` record (family/size/bold/italic/color/
alignment); page size/margins via `PageSize` and `PageMargins`. See issue #383.

### Reveal Hidden Text

`Tools → Reveal Hidden Text` finds text that's been visually hidden by overlays:

- **Yellow boxes** — structural detections from `Pdfe.Core.Text.Segmentation.HiddenTextDetector` (text covered by later filled rectangles, the classic bad-redaction pattern)
- **Orange boxes** — differential-OCR recoveries (`Pdfe.Ocr.DifferentialOcrAuditor`) for text hidden inside rasterized images by an opaque overlay

Useful for auditing third-party redactions before relying on them.

### Keyboard shortcuts

Press **F1** to view all in-app.

| Category | Action | Shortcut |
|---|---|---|
| File | Open / Save / Save As / Close | `Ctrl+O` / `Ctrl+S` / `Ctrl+Shift+S` / `Ctrl+W` |
| Edit | Find / Find Next / Find Previous | `Ctrl+F` / `F3` / `Shift+F3` |
| View | Zoom In / Out / Actual / Fit Width / Fit Page | `Ctrl+Plus/Minus/0/1/2` |
| Navigation | Next/Previous/First/Last Page | `Page Down/Up`, `Home`, `End` |
| Modes | Redaction / Text Selection / Apply | `R` / `T` / `Enter` |
| Pages | Rotate Left / Right | `Ctrl+L` / `Ctrl+R` |

### CLI examples

```bash
# Render page 1 of a PDF at 200 DPI
pdfe render report.pdf -o report-p1.png --page 1 --dpi 200

# Glyph-level redact a phrase
pdfe redact report.pdf report-redacted.pdf "ACCOUNT 9876"

# Audit a "redacted" PDF for hidden text leftovers — both structural and rasterized
pdfe audit purportedly-redacted.pdf --deep --json

# Fill an AcroForm and flatten so the result is no longer interactive
pdfe fill-form blank-w9.pdf w9-filled.pdf --field Name=Acme --field EIN=12-3456789 --flatten

# Add a text field to an existing PDF
pdfe add-field invoice.pdf invoice-with-form.pdf \
  --type Text --name CustomerNote --page 1 --rect "72,200,540,260"

# Auto-detect form fields on a Word-exported PDF and apply them
pdfe autodetect-fields exported-from-word.pdf form-ready.pdf --apply

# Extract text from a scanned PDF (requires system tesseract)
pdfe ocr scan.pdf
```

## Technology stack

### Framework & UI
- **.NET 10.0** — Cross-platform runtime
- **Avalonia UI 12.x** (MIT) — Cross-platform XAML UI
- **ReactiveUI** (MIT) — MVVM framework

### Pdfe libraries (this repo)
- **Pdfe.Core** — Pure-.NET PDF parser, writer, content streams, glyph-level redaction, text extraction with letter positions, hidden-text detection, AcroForm read/fill/flatten/author, OCG + structure tree (read-only), embedded files (read + scrub), page labels, named destinations, document authoring
- **Pdfe.Rendering** — SkiaSharp renderer with embedded TrueType + raw-CFF/Type1C support, Type0/CID composite fonts, /Differences-aware encoding, image XObjects (DCT/Flate/JPX/CCITTFax), inline images, transparency, clipping paths, color spaces (DeviceRGB/CMYK/Gray, ICCBased, Indexed, CalRGB, Lab)
- **Pdfe.Ocr** — Wrapper around the system `tesseract` CLI + a differential-OCR auditor

### Permissive third-party deps
- **SkiaSharp 3.119.x** (MIT) — 2D graphics
- **CSJ2K 3.x** (BSD) — managed JPEG 2000 / JPX image decoding
- **Clipper2** (BSL 1.0) — Polygon clipping for redaction geometry
- **BouncyCastle.Cryptography** (MIT) — CMS cryptography for digital-signature inspection
- **Microsoft.CodeAnalysis.CSharp.Scripting** (MIT) — optional Roslyn scripting for GUI automation; enabled in Debug/test builds and opt-in for Release builds with `-p:EnableScripting=true`

No copyleft obligations. No PDFium / PDFsharp / PdfPig / Tesseract.NET — all dropped in v2.0.

## Project structure

```
pdfe/
├── Pdfe.Core/                       # PDF parser, writer, content streams, redaction, AcroForm
│   ├── Parsing/                     # Lexer, parser, xref
│   ├── Document/                    # PdfDocument, PdfPage, AcroForm (parse/edit/flatten/author/autodetect),
│   │                                # OCG, structure tree, embedded files, page labels
│   ├── Content/                     # ContentStreamReader/Writer
│   ├── Text/                        # TextExtraction, letter positions
│   │   └── Segmentation/            # GlyphRemover, ImageRedactor, HiddenTextDetector
│   ├── Fonts/                       # CffParser, CffSubsetter
│   ├── Graphics/                    # PdfGraphics API (paths, text, images)
│   └── Writing/                     # Save, incremental update
│
├── Pdfe.Rendering/                  # Skia-based renderer
│   ├── SkiaRenderer.cs              # Content-stream → SKBitmap
│   ├── Fonts/                       # OpenType wrapper, AGL
│   └── ColorSpaces/                 # DeviceRGB/CMYK/Gray, ICCBased, Indexed, CalRGB
│
├── Pdfe.Ocr/                        # OCR shim
│   ├── PdfOcrService.cs             # tesseract CLI invocation
│   └── DifferentialOcrAuditor.cs    # render-twice-and-diff hidden-text finder
│
├── Pdfe.Cli/                        # `pdfe` CLI
│   └── Program.cs                   # 12 subcommands
│
├── PdfEditor/                       # Desktop GUI
│   ├── Controls/PdfViewerControl    # Reusable Avalonia PDF viewer (annotations, links, form/typewriter overlays)
│   ├── Models/                      # HiddenTextHighlight, etc.
│   ├── Services/                    # App services on Pdfe.Core / Pdfe.Rendering
│   ├── ViewModels/
│   └── Views/
│
├── Pdfe.Core.Tests/                 # ~2880 tests
├── Pdfe.Rendering.Tests/            # ~287 tests, including visual baselines + corpus
├── Pdfe.Avalonia.Tests/             # public API and viewer utility tests
├── Pdfe.Cli.Tests/                  # 22 tests
├── Pdfe.Ocr.Tests/                  # 41 tests (some require tesseract)
├── PdfEditor.Tests/                 # ~775 tests, including headless GUI
└── test-pdfs/                       # Smoke corpus + sample PDFs
```

## Testing

```bash
# Full suite
dotnet test

# Single project
dotnet test Pdfe.Rendering.Tests
dotnet test Pdfe.Core.Tests --filter "Redaction"

# With detailed output
dotnet test --logger "console;verbosity=detailed"
```

Test categories:
- Unit tests — primitives, parser, content streams, segmentation, coordinate math
- Integration tests — real-world PDFs (birth certificates, government forms, books)
- Visual regression — MuPDF-first bitmap diffs with Poppler/Ghostscript escalation for disputed renders
- Headless GUI — `[AvaloniaFact]` tests render `PdfViewerControl` against fixtures
- Security — content-stream verification that redacted text is structurally absent
- Conformance — smoke corpus parse/render, Isartor round-trip, and optional veraPDF corpus parse/render

The PDF Association corpora are downloaded on demand with `scripts/download-test-pdfs.sh`. The full veraPDF corpus is intentionally treated as a slower conformance lane, not as a required inner-loop test.

## Building

### Plain self-contained binaries

```bash
# Linux
dotnet publish PdfEditor -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true

# Windows
dotnet publish PdfEditor -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true

# macOS Intel / Apple Silicon
dotnet publish PdfEditor -c Release -r osx-x64    --self-contained true -p:PublishSingleFile=true
dotnet publish PdfEditor -c Release -r osx-arm64  --self-contained true -p:PublishSingleFile=true
```

Published binaries land in `bin/Release/net10.0/<runtime>/publish/`.

Release builds exclude the Roslyn scripting engine by default to keep shipped
packages lean and AOT/trim-friendlier. To produce a developer build with the
GUI scripting service included, add `-p:EnableScripting=true` to the publish
command.

Repo-local `tessdata/*.traineddata` files are also excluded from app packages by
default; pdfe uses the system `tesseract` installation when differential OCR is
requested. To bundle local language data for an offline/developer package, add
`-p:IncludeTessdataInApp=true`.

### Installers

```bash
# Ubuntu / Debian .deb (requires dpkg-deb; preinstalled on Ubuntu)
scripts/build-deb.sh                          # → dist/pdfe_<version>_amd64.deb
scripts/build-deb.sh --arch arm64             # arm64 variant
scripts/build-deb.sh --version 2.1.0-rc8      # explicit version

# Windows .exe (requires Inno Setup 6: choco install innosetup)
pwsh scripts/build-windows-installer.ps1      # → dist/pdfe-<version>-win-x64-setup.exe

# macOS .app bundle (Apple Silicon by default; Intel via --rid osx-x64)
scripts/build-macos-app.sh --version <version>            # → dist/pdfe-<version>-macos-arm64.zip
scripts/build-macos-app.sh --version <version> --rid osx-x64
```

### Native AOT release lane

Native AOT is an explicit release lane, not the default package until its GUI
smoke and warning budget are green for the target platform. The local gate
publishes/packages the AOT app, captures IL/AOT warning output, separates
debug symbols from the user-facing artifact, and writes JSON/markdown evidence:

```bash
scripts/release-smoke.sh --quick --only=aot
scripts/run-aot-smoke.sh --version <version> --rid osx-arm64
scripts/build-macos-app.sh --version <version> --rid osx-arm64 --aot
```

Use `scripts/run-aot-smoke.sh --gui-smoke` on an interactive macOS runner when
validating the AOT app against packaged GUI launch/open/render evidence. ReadyToRun
remains the fallback artifact until AOT passes the same release gates.

### Using pdfe as a PDF reader on macOS

The `.app` bundle declares itself a handler for PDF files (`CFBundleDocumentTypes`)
and opens documents passed by Finder, the Dock, or `open -a` — so it can be used
as a regular reader, not just launched empty.

```bash
# First launch: the build is not notarized, so clear the Gatekeeper quarantine
# (one time, see issue #421 for signing/notarization tracking):
xattr -dr com.apple.quarantine /Applications/pdfe.app

# Open a PDF in pdfe:
open -a pdfe ~/Documents/example.pdf
```

To make pdfe the **default** PDF app: select any `.pdf` in Finder → **⌘I** (Get
Info) → **Open with** → choose *pdfe* → **Change All…**. Double-clicking PDFs
then opens them in pdfe.

### Using pdfe as a PDF reader on Windows

The Windows installer registers pdfe as a PDF-capable app in the per-user
`Default apps` / `Open with` registry metadata. During install, select
**Associate pdfe with .pdf files** to add the `pdfe.pdf` ProgID. Windows 10/11
still require the user to choose the default handler: Settings → Apps →
Default apps → choose defaults by file type → `.pdf` → **pdfe**.

The portable `.zip` does not write registry entries. For portable installs, use
Explorer → right-click a PDF → **Open with** → **Choose another app** → browse to
`PdfEditor.exe`; selected PDFs are passed to pdfe and opened on launch.

### Release automation

`.github/workflows/release.yml` builds installers/app bundles and attaches them
to a GitHub Release whenever a `v*` tag is pushed (or a release is published
manually):

1. `linux-deb` job (ubuntu-latest) → `pdfe_<version>_amd64.deb` + portable `.tar.gz`
2. `windows-exe` job (windows-latest) → `pdfe-<version>-win-x64-setup.exe` + portable `.zip`
3. `macos-app` job (macos-latest) → arm64 `.app` bundle `.zip`
4. `publish` job uploads all artifacts with `.sha256` files; tags containing `-rc`/`-beta`/`-alpha` are flagged as pre-releases.

Before tagging, run the release checklist in
[`docs/RELEASE_CHECKLIST.md`](docs/RELEASE_CHECKLIST.md), including
`scripts/verify-doc-claims.sh`, so documentation claims are checked against
the implemented commands and APIs. The repeatable local gate is:

```bash
scripts/release-smoke.sh --visual --package --packaged-gui --version 2.27.1
```

The release-smoke script does not create tags or upload artifacts. It runs the
documentation, build, redaction, signature-verification, UI workflow,
benchmark, optional Native AOT, full-test, local visual-regression, packaging,
packaged-GUI evidence, packaged first-page responsiveness timing, and diff-cleanliness gates
and writes logs under `logs/release-smoke_*`.

```bash
# Cut a new release
git tag -a v2.27.1 -m "pdfe v2.27.1"
git push origin v2.27.1          # workflow runs, attaches release artifacts
# Or via the GitHub UI: Releases → Draft a new release → choose tag
```

## Versioning & API stability

The publishable libraries — **`Pdfe.Core`**, **`Pdfe.Rendering`**, **`Pdfe.Avalonia`** — follow [Semantic Versioning](https://semver.org/) on their **public** API:

- **MAJOR** — a breaking change to a public type/member.
- **MINOR** — backward-compatible additions (new types/members/overloads).
- **PATCH** — backward-compatible fixes with no public-API change.

What counts as the supported public contract:

- Public types and members of the three libraries are the contract. Anything marked `internal` (or excluded from the public surface) may change in any release.
- The high-level authoring surface — `Pdfe.Core.Authoring.*` (`PdfDocumentBuilder`, `TextStyle`, `PageSize`, `PageMargins`, `FontFamily`, `LayoutContext`) — is the recommended, stable entry point for *writing* PDFs. The low-level `PdfGraphics` / `AcroFormAuthoring` API remains available as an escape hatch.
- The public API is **gated in CI**: `PublicApiApprovalTests` snapshots the full public surface of `Pdfe.Core` against a committed baseline (`Pdfe.Core.Tests/PublicApi/Pdfe.Core.approved.txt`). Any addition, removal, or signature change fails the build until the baseline is intentionally regenerated (`APPROVE_PUBLIC_API=1`) and committed — so every public-API change is a deliberate, reviewable SemVer decision.

**Distribution:** packages ship as `.nupkg` + `.snupkg` (symbols) with [SourceLink](https://github.com/dotnet/sourcelink) for step-into debugging, attached to each [GitHub Release](https://github.com/marctjones/pdfe/releases). They are **not published to nuget.org** — consume them via a local/private feed or a project reference. See issues #383 (writer DX) and #384 (viewer/render DX).

## Documentation

- **[CHANGELOG.md](CHANGELOG.md)** — release notes
- **[GitHub Wiki](https://github.com/marctjones/pdfe/wiki)** — Architecture, redaction engine internals, PDF spec reference
- **[CLAUDE.md](CLAUDE.md)** — Development guidelines (also for AI-assisted contributions)
- **[REDACTION_AI_GUIDELINES.md](REDACTION_AI_GUIDELINES.md)** — Critical safety rules for redaction-code changes

## License

MIT License. See [LICENSES.md](LICENSES.md) for the complete dependency-license inventory. All dependencies are permissive (MIT / Apache 2.0 / BSD-3 / BSL-1.0); no copyleft.

## Contributing

Contributions welcome. The biggest open areas tracked in GitHub Issues:

- In-place text editing (change text inside a paragraph with reflow)
- Annotation authoring (highlight / underline / sticky-note / freehand drawing)
- E-signature workflow (click-to-sign + multi-party + audit trail)
- PDF encryption authoring (we read encrypted PDFs but can't write them)
- PDF → DOCX conversion
- PDF compare / diff
- Field-properties dialog for AcroForm authoring (rename, /Q alignment, JS actions, validation)
- Radio-button groups (parent + /Kids on different /AS names)
- Tab-order management

Smaller-scope improvements (additional operator coverage, performance, accessibility, CFF charstring rewriting) are good first issues.
