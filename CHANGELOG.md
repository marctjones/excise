# Changelog

All notable changes to pdfe are documented here. Format roughly follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses
semantic versioning.

## [2.10.0] — 2026-06-08

Library DX + authoring-correctness release. Additive; no breaking changes
(public-API gates confirmed).

### Added
- **Public-API gate for the viewer libraries (#384).** A new lightweight,
  non-GUI `Pdfe.Avalonia.Tests` project snapshots the public surface of
  `Pdfe.Avalonia` and `Pdfe.Rendering` against committed baselines (same
  treatment `Pdfe.Core` got in #383) — any API change now fails CI until the
  baseline is intentionally regenerated. It is deliberately separate from the
  heavy headless GUI suite, so viewer-library changes get reliable per-PR
  coverage.
- **`PdfField.ButtonExportValues` (#424).** For a Button field (e.g. a radio
  group), the selectable "on" export values — the appearance-state names from
  each widget's `/AP /N` other than `Off`. Lets a form importer map a radio
  group to a choice/dropdown instead of a generic boolean.

### Fixed
- **Base-14 text encoding mojibake (#426).** `PdfFont.EncodeString` formatted the
  Unicode code point in decimal as a `\ddd` escape, but PDF reads `\ddd` as
  octal — so `é`, `—`, `·`, curly quotes etc. came out as garbage (and code
  points above 255 were never mapped to their WinAnsi byte). The encoder now maps
  Unicode → WinAnsi (CP1252) and emits correct octal, falling back to `?` for
  characters genuinely unrepresentable in base-14 (embed a font via `DefaultFont`
  to keep those). No public-API change.

## [2.9.0] — 2026-06-08

Viewer + macOS-reader + archival release. Additive; no breaking changes
(public-API gate confirmed for `Pdfe.Core`).

### Added
- **Continuous (reading) view mode for `Pdfe.Avalonia` (#371).** New
  `PdfViewerControl.ViewMode` (`PdfViewMode.SinglePage` default | `Continuous`).
  Continuous shows every page in a vertically-scrolling, **render-virtualized**
  list — only pages near the viewport render, bitmaps are bounded by an LRU
  cache, and off-screen renders are cancelled. It is **read-only by design**:
  entering an editing interaction (Redaction / TextSelection / FormAuthoring)
  auto-switches back to single-page, so the editing/redaction overlays only ever
  run against a single rendered page. Scroll ⇄ current-page stay in sync and zoom
  resizes pages live. New public types `PdfViewMode`, `PdfPageSlot`.
- **macOS: open PDFs from Finder / be a default reader (#420).** The app handles
  the macOS file-activation event (Finder double-click, Dock, `open -a`), and the
  generated `.app` `Info.plist` declares `CFBundleDocumentTypes` for
  `com.adobe.pdf` so pdfe registers as a PDF handler. README documents setting it
  as the default reader and the one-time Gatekeeper unquarantine.
- **PDF/A archival output.** `PdfDocumentBuilder.PdfA(PdfAConformance.PdfA2B)`
  adds the document structures PDF/A requires at save time — an XMP metadata
  packet with the `pdfaid` identifier and an sRGB OutputIntent (embedded ICC
  profile). With an embedded font (`DefaultFont`), the output validates as
  **PDF/A-2b under veraPDF 1.30.2 (144/144 rules)**. New `PdfAConformance` enum.
  (PDF/A-1b is stricter and not yet fully met — tracked in #425.)
- **Trailer `/ID`.** Newly authored documents now always get a file-identifier
  array in the trailer (ISO 32000-1 §14.4) — required by PDF/A and recommended
  generally; an existing `/ID` is preserved.

### Fixed
- **Chronic headless GUI test host-crash (#363), part 2.** The headless test
  runner now closes each test's windows afterward (tracked via Avalonia's global
  routed-event streams), bounding the shared dispatcher's live-window set, and the
  heavy `*_MatchesBaseline` visual-regression tests are excluded from the PR gate
  (owned by the nightly job). Reduces — but does not yet fully eliminate — the
  residual native host crash; full resolution is in progress.

## [2.8.0] — 2026-06-08

Operator render-coverage release (#350). Additive; no breaking changes.

### Added
- **Dash pattern (`d`) rendering.** The dash operator was parsed but ignored by
  the renderer, so dashed strokes drew solid. `SkiaRenderer` now honors it via
  `SKPathEffect.CreateDash` on both stroke paths; odd-length PDF dash arrays are
  doubled (Skia needs even on/off pairs) and empty/degenerate arrays fall back to
  a solid line.
- **Authoritative operator inventory test.** One stream exercising every standard
  content-stream operator, each asserted to parse **and** survive a
  parse→write→parse round-trip through `ContentStreamWriter`.

### Tests
- **Shading (`sh`) render output is now actually verified.** Earlier shading
  tests referenced a `/Shading` resource the test PDFs never contained, so the
  axial/radial gradient code path ran as a no-op. New `OperatorRenderCoverageTests`
  build PDFs with real Type 2 (axial) and Type 3 (radial) shadings and assert
  gradient pixels, clip restriction, and graceful handling of a missing resource.
- Dash render tests assert real behavior (a dash leaves measurable gaps vs. a
  solid control; an empty array resets to solid).

## [2.7.0] — 2026-06-06

Fillable-table authoring + PDF/UA accessibility hardening. Additive; no breaking
changes (public-API gate confirmed).

### Added
- **`PdfDocumentBuilder.FillableTable(...)`.** Renders a table whose body cells
  are interactive AcroForm fields (text input, checkbox, or dropdown per cell) —
  a fillable grid. Mirrors `Table`'s layout (column weights, gridlines, automatic
  pagination) but places live fields instead of static text. The first column is a
  static row-header; each cell's `/TU` accessible name comes from its tooltip.
  New supporting types: `FillableTableRow`, `FillableTableCell`, `FillableCellKind`.
- **PDF/UA hardening for tagged output (#407).**
  - Decorative content (horizontal rules, form-field borders, table grid lines)
    is wrapped in `/Artifact` so every piece of page content is tagged or an
    artifact. New `PdfGraphics.BeginArtifact()`.
  - Form-field widgets are added to the structure tree as `Form` elements via
    `/OBJR`, with each widget carrying a `/StructParent` into the ParentTree.
  - Tagged tables now nest `Table → TR → TD/TH` (header cells `TH`), each cell in
    its own marked content, instead of one flat `Table` element;
    `StructureTreeBuilder` models a general nested element tree.

## [2.6.0] — 2026-06-06

Font, accessibility, and image-filter additions. All additive; the public-API
gate confirms no breaking changes.

### Added
- **Font subsetting + CFF/OpenType embedding (#393).** Embedded TrueType fonts
  are now subsetted to the glyphs actually drawn (retain-GID `glyf`/`loca`,
  composite-glyph closure, subset tag) — e.g. DejaVu drawing a short string went
  from ~759 KB to ~14 KB embedded. CFF-outline OpenType (`'OTTO'`) fonts can now
  be embedded too (`/CIDFontType0` + `/FontFile3 /Subtype /OpenType`).
- **Embedded fonts in the high-level builder (#398).** `TextStyle.WithFont(...)`
  and `PdfDocumentBuilder.DefaultFont(...)` let the friendly facade render
  arbitrary Unicode (not just base-14); the same typeface across sizes/weights
  embeds as one subset. `PdfFont.WithSize` is now `virtual`.
- **Tagged-PDF authoring / PDF-UA (#275).** `PdfDocumentBuilder.Tagged()` emits a
  logical structure tree (StructTreeRoot + Document→H1-H4/P/Table), marked
  content (`BDC`/`EMC` + MCID, `/MCR` with `/Pg`, `/ParentTree`), and catalog
  `/MarkInfo`, `/ViewerPreferences /DisplayDocTitle`. Plus
  `PdfGraphics.BeginMarkedContent`/`EndMarkedContent`. Combined with embedded
  fonts + `/Lang`, the builder now produces genuinely accessible documents
  (`pdfinfo` reports `Tagged: yes`).
- **Image filters: JBIG2 + JPEG2000 (#325).** Pure-managed JBIG2 decoder
  (MQ arithmetic + generic region, template 0) wired into the stream
  decompressor with strict decode-or-passthrough fallback (no silently-wrong
  images). JPEG2000 (`JPXDecode`) codestream/marker parsing (full pixel decode
  deferred). JPEG/PNG remain delegated to the SkiaSharp renderer.

### Notes
- Remaining tracked follow-ups: full PDF/UA conformance (artifacts, TR/TD,
  form-field tagging), CFF glyph subsetting, JBIG2 symbol/text regions, full
  JPEG2000 decode.

## [2.5.0] — 2026-06-06

Completes the **PromptResponse writer epic (#382)** — pdfe can now author
accessible, fillable, Unicode PDFs from structured content. All additive; the
public-API gate confirms no breaking changes.

### Added
- **Unicode text + embedded fonts (#378).** `PdfFont.FromFile(path, size)` /
  `FromTrueType(bytes|Stream, size)` embed a TrueType font as a Type0 /
  Identity-H composite font with a ToUnicode CMap, so arbitrary Unicode (CJK,
  Arabic, accented Latin, Greek, Cyrillic, …) both renders and stays
  extractable. Backed by a new dependency-free sfnt reader
  (`Pdfe.Core.Fonts.TrueTypeFontFile`). Full-font embedding; subsetting and CFF
  ('OTTO') are tracked in #393.
- **High-level text layout (#379).** `PdfGraphics.DrawText(text, font, brush,
  PdfRectangle, …)` word-wraps into a box and returns a `TextLayoutResult`
  (used height + overflow) for flowing across boxes/pages; `MeasureText(...)`
  returns wrapped size.
- **AcroForm field options (#380).** `/TU` tooltip (accessible name) on all
  field types; `/MaxLen` + comb for text fields; `AddDateField` (Acrobat
  `AFDate` format/keystroke actions); `SetTabOrder` (page `/Tabs`).
- **Document metadata (#381).** `PdfDocument.SetTitle/SetAuthor/SetSubject/
  SetKeywords/SetCreator/SetProducer` (creates the `/Info` dict on demand) and a
  read/write `Language` property (catalog `/Lang`, required by PDF/UA).
- **`PdfDocumentBuilder`** gains `Title/Author/Subject/Keywords/Language`,
  `DateField`, and `tooltip`/`maxLength`/`comb` passthrough on fields (with
  `/TU` defaulting to the visible label for screen readers).

### Changed
- `PdfFont` text-encoding/measurement/metrics members are now `virtual` so
  embedded fonts can override them; standard-font behavior is unchanged.
- Dependencies: bumped `FluentAvaloniaUI` to the latest preview (#340; full
  de-preview is blocked on an upstream FluentAvalonia 3.x stable for Avalonia 12).

### Tests / CI
- Raised `Pdfe.Core` CI line coverage to ~93% and ratcheted the gate to 92.5%
  (#351); CI installs `fonts-dejavu-core` so the embedding tests run
  deterministically. The macOS `.app` is now built and attached by CI.

## [2.4.1] — 2026-06-06

Packaging, API-stability, and CI hardening on top of v2.4.0. No public-API
changes (enforced by the new gate) — a pure patch.

### Added
- **Public-API gate (#383).** `PublicApiApprovalTests` snapshots the full
  `Pdfe.Core` public surface against a committed baseline
  (`Pdfe.Core.Tests/PublicApi/Pdfe.Core.approved.txt`); any public-API change
  fails CI until intentionally re-approved (`APPROVE_PUBLIC_API=1`). Makes every
  API change a deliberate SemVer decision.
- **SourceLink + symbols.** The three publishable libraries (`Pdfe.Core`,
  `Pdfe.Rendering`, `Pdfe.Avalonia`) now ship portable `.snupkg` symbol packages
  with SourceLink and deterministic CI builds (shared `Packaging.props`), so
  consumers can step into the source while debugging.
- README "Versioning & API stability" section documenting the SemVer policy,
  the `Pdfe.Core.Authoring.*` stable writer surface, and local-feed (not
  nuget.org) distribution.

### Fixed
- **Release pipeline cold-cache restore (#387).** `release.yml` now sets
  `DOTNET_NUGET_SIGNATURE_VERIFICATION=false` (matching `ci.yml`) so a
  version-bump cache miss no longer fails the license-manifest step with NU3012
  (revoked ReactiveUI/Splat signing cert). The v2.4.0 Windows/Debian/macOS
  installers — absent from that release due to this bug — are restored here.
- `generate-license-manifest.sh` no longer hard-fails on a cold NuGet cache and
  no longer suppresses restore output.

### CI / dev
- Headless GUI tests (`PdfEditor.Tests`) now run only when GUI-relevant paths
  change (or on `main`), so library-only PRs aren't gated on the slow GUI suite.
- Quarantined the flaky `KeyboardShortcutTests.CtrlS_SavesFile` on headless CI
  (#363) — it intermittently deadlocked the Avalonia dispatcher and crashed the
  test host. Still runs locally; the save path stays covered elsewhere.

## [2.4.0] — 2026-06-05

Adds a friendly, high-level **PDF authoring** API so third-party .NET apps can
generate PDFs from structured content without touching coordinates — the
writer-side facade tracked by #383 (PromptResponse writer epic #382).

### Added
- **`Pdfe.Core.Authoring.PdfDocumentBuilder` — high-level writer facade (#383).**
  A fluent, flow-layout builder over the existing `PdfGraphics` /
  `AcroFormAuthoring` API. Content flows top-to-bottom inside the page's content
  area with automatic word-wrap and pagination, so callers never compute
  coordinates or manage the PDF's bottom-left Y axis.
  - Content blocks: `Heading(level)`, `Paragraph` (word-wrap + hard-break
    aware), `Spacer`, `HorizontalRule`, `KeyValue`, `Table` (column weights,
    optional header row + grid lines), `PageBreak`.
  - Fillable AcroForm fields, flow-positioned with drawn labels and borders:
    `TextField` (multiline/required), `CheckBox`, `Dropdown` (combo). Auto-names
    fields when none is supplied.
  - `Custom(Action<PdfGraphics, LayoutContext>)` escape hatch to the low-level
    API; `Build()` returns the `PdfDocument` for further manipulation;
    `SaveToBytes()` / `Save(path)` / `Save(Stream)` output.
- **Authoring value types.** `PageSize` (Letter/Legal/A4/A3/A5 +
  `Landscape()`/`Portrait()`), `PageMargins` (`All`/`Symmetric`/`Default`),
  immutable `TextStyle` record (family/size/bold/italic/color/alignment/
  line-spacing/space-after with `With…` helpers), `FontFamily`, `LayoutContext`.
- README: a copy-paste "Authoring PDFs from scratch (high-level)" sample.

### Notes
- Targets the base-14 fonts and Latin text available today; Unicode / embedded
  TrueType-OpenType fonts (#378), richer text layout (#379), more AcroForm
  field options (#380), and document metadata setters (#381) extend the facade.
- Verified against external readers: generated forms pass `qpdf --check`,
  `pdfinfo` reports a live `AcroForm`, content auto-paginates, and `pdftotext`
  extracts all text. 17 new tests; full `Pdfe.Core` suite green (2744 passing).

## [2.3.1] — 2026-06-04

### Fixed
- **Thread-safe object resolution (#376).** A single `PdfDocument` resolved
  indirect objects through one shared lexer with a mutable stream position, so
  concurrent reads — e.g. the GUI's background search-indexer parsing pages
  while the UI thread reads links / renders — corrupted each other's seeks,
  surfacing as spurious `PdfParseException: Unexpected keyword 'obj'`.
  `GetObject` now serializes seek/parse + cache mutation behind a reentrant
  lock. Verified on a large real document: 8 threads reading every page
  produced 729 errors before and 0 after. Matters especially now that
  `Pdfe.Core` ships as a NuGet package.

## [2.3.0] — 2026-06-04

Turns pdfe's engine into reusable libraries for the wider .NET/Avalonia ecosystem.

### Added
- **`Pdfe.Avalonia` — reusable Avalonia PDF viewer control (#365).** The
  `PdfViewerControl` (zoom/pan, navigation, text selection, search highlights,
  annotations, links, form-field overlays) is extracted from the `PdfEditor`
  app into a standalone, dependency-light library (depends only on `Pdfe.Core`
  + `Pdfe.Rendering` + Avalonia + SkiaSharp). Any Avalonia app can now drop in a
  pure-managed, SkiaSharp-based PDF viewer — a gap the ecosystem lacked. The
  app consumes it as the reference implementation; a minimal `Pdfe.Avalonia.Sample`
  shows the dependency-light usage.
- **Framework-neutral render API (#366).** `Pdfe.Rendering.SkiaRenderer` gains
  `RenderPage(page, options, CancellationToken)` (cancellable between
  content-stream operators, companion to #346) and `RenderPageToPng(page, Stream, …)`
  for non-Skia consumers.
- **NuGet-packable trio.** `Pdfe.Core`, `Pdfe.Rendering`, and `Pdfe.Avalonia`
  carry package metadata + per-package READMEs; `dotnet pack` produces three
  valid `.nupkg`s (attached to this release; not pushed to nuget.org).

### Changed
- `PdfEditor` now consumes `Pdfe.Avalonia` rather than embedding the control;
  behavior is unchanged.

## [2.2.2] — 2026-06-03

### Fixed
- **Outline and page-preview (thumbnail) sidebars are now independently
  toggleable (#369).** The outline panel was nested inside the thumbnails
  sidebar, so "Show Outline" did nothing unless "Show Thumbnails" was also on,
  and hiding thumbnails hid the outline too. The left sidebar now shows when
  *either* panel is enabled, each panel binds its own visibility, and the
  splitter appears only when both are visible.

### Added
- **Toolbar toggle buttons** for the outline (📑) and page previews (🗐), plus
  **keyboard shortcuts** Ctrl+Shift+O (outline) and Ctrl+Shift+T (thumbnails) —
  the toggles were previously buried as View-menu checkboxes only. (#369)

## [2.2.1] — 2026-06-03

Maintenance release: parser-robustness hardening, a rotated-page render fix,
CI test-flake fixes, and a documentation refresh. No new user-facing features;
closes the remaining open **bug/fix** issues on top of v2.2.0 (the v2.2.0
release shipped the redaction-security trio; this release adds the
parser-hardening / known-issues batch that landed afterward).

### Fixed
- **Rotated PDFs render unrotated** — `SkiaRenderer` now honours the page
  `/Rotate` entry (0/90/180/270), sizing the bitmap in visual dimensions, so
  rotated pages display the right way up. (#364)
- **Writer re-emitted cross-reference plumbing** — `/ObjStm` and `/XRef`
  streams are no longer copied into the rewritten body, so a Form XObject
  flattened out of a compressed object stream can't survive redaction. (#359)
- **Inline-image `EI` scan was unbounded** on malformed image data lacking a
  `/L` length, causing O(n²) blowup; the scan is now bounded. (#347)
- **Parser hardening against hostile input** — content-stream array recursion
  is depth-bounded and a `CancellationToken` is threaded through parsing so a
  malicious/degenerate document can't hang or stack-overflow. (#346)
- **Exception-swallowing audit** — best-effort `catch` blocks no longer
  swallow `OutOfMemoryException` (and other critical failures) during the
  ToUnicode-CMap parse and related paths. (#345)
- Added an end-to-end CID/Type0 (CJK) redaction regression test on a real
  Identity-H PDF, locking in the v2.1.0 `RawBytes` reconstruction fix. (#353)

### Security / robustness
- **Malformed-PDF fuzz / property tests** for the parsers (`ParserFuzzTests`):
  on hostile or malformed bytes the parser must parse them or fail with a
  *typed* `PdfParseException` — never a raw CLR crash. The tests surfaced and
  fixed four genuine robustness bugs: a `FormatException` in content-stream
  hex-string parsing (`Uri.IsHexDigit`), a `KeyNotFoundException` on a
  `/Root`-less trailer, an `InvalidOperationException` on a catalog with no
  `/Pages`, and an `ArgumentOutOfRangeException` from a negative/past-EOF xref
  seek offset (`PdfLexer.Seek` now bounds-checks). (#352)

### CI / tests
- Removed a redundant 15s `OperationStatus` wait in the AcroForm overlay test
  and raised over-tight GUI timeouts (3s → 15s) that masked CI slowness as a
  hang; raised the cold-CI first-render budget (15s → 60s) in the headless
  render baseline test, which renders in ~2s locally but can exceed 15s on a
  cold CI runner (JIT + xvfb + SkiaSharp native init). (#363)

### Docs
- Refreshed stale `CLAUDE.md` notes: the redaction-engine architecture now
  points at `Pdfe.Core` (not the removed `PdfEditor/Services/Redaction/`), and
  the frozen "Current Status (v1.4.0)" block now points at `CHANGELOG.md` /
  GitHub Releases so the version no longer goes stale in-file. (#349)

## [2.2.0] — 2026-06-03

Redaction-security release: closes the remaining content-type and
coordinate gaps so redaction reliably removes — not merely covers — every
way content can land under the redaction area. Also restores a working CI
gate (it had been silently broken) and raises Pdfe.Core coverage.

### Added / Security
- **Inline-image redaction** (`BI…ID…EI`) — the parser now retains the
  embedded pixel bytes and the writer re-emits valid inline-image syntax, so
  an inline image overlapping the redaction area is removed, not just covered.
  (#354)
- **Form XObject redaction** — overlapping forms are flattened into the page
  (Matrix/BBox-correct, resources merged with collision renaming, nested
  forms recursed) and redacted; the now-orphaned form objects are pruned so
  the writer can't re-emit the removed content. (#355)

### Fixed
- **Rotation-aware redaction** — `PdfPage.ToContentStreamCoordinates` maps a
  visual-space rectangle into content space for `/Rotate` 0/90/180/270; the
  GUI no longer mis-targets redactions on rotated pages. (#356)
- **Outline / text-string decoding** — `PdfString` now decodes the
  PDFDocEncoding 0x80–0x9F / 0x18–0x1F / 0xA0 ranges (em/en dash, curly
  quotes, ligatures, €, …) instead of rendering C1 control characters as tofu
  boxes (e.g. bookmark "Part I—Fundamentals"). (#361)

### CI / tests
- Restored the Build/Test/Coverage gate, which had been masked by a failing
  veraPDF-install step: best-effort veraPDF, NuGet signature-verification
  workaround (revoked ReactiveUI cert), refreshed the redaction-architecture
  check, and fixed the coverage-report path. The PR gate now runs the
  deterministic test set (environment-dependent visual/corpus/differential/
  benchmark tests are owned by the nightly job). (#351)
- Raised Pdfe.Core coverage and set the enforced gate to the level CI meets.

## [2.1.0] — 2026-06-01

Graduates the `v2.1.0-rc1..rc8` line to a final release. v2.1 builds out the
pure-.NET stack with encryption, forms, advanced transparency, full CJK, and a
much broader content-stream operator set, then this release caps it with a
performance pass, dependency hygiene, and a round of stability/security
hardening.

### Added
- PDF **encryption/decryption** — RC4 (V1/V2) and AES-128/256 (V4/V5). (#237)
- **AcroForm** read, edit, and authoring — fill, flatten, create fields. (#272)
- **Advanced transparency** — soft masks, transparency groups, full blend-mode set. (#274)
- **Type0 / CID (CJK)** fonts — Identity-H/V, ToUnicode CMap, vertical writing, CFF wiring. (#327, #328)
- **Optional content groups** (OCGs) + **XMP** metadata extraction. (#329)
- **Embedded-file** extraction. (#330)
- Full content-stream **operator coverage** — text-state ops, color spaces, marked content, shading. (#326, #333)
- veraPDF / corpus **conformance harness**. (#332)

### Changed / Performance
- GUI **Release startup profile** — ReadyToRun + TieredPGO + concurrent GC; **~36% faster cold start** (1.18 s → 0.75 s). (#339)
- ReadyToRun for `Pdfe.Cli`. (#334)
- Moved off preview packages and bumped to latest stable: **Avalonia 12.0.4, ReactiveUI 23.2.27, SkiaSharp 3.119.4, .NET 10.0.8**. (#340)
- Removed the IdlerGear integration; refreshed stale docs (versions/architecture) and archived obsolete plan docs. (#349)

### Fixed (stability & security hardening)
- Parser **recursion-depth guard** — deeply nested hostile PDFs throw instead of StackOverflow. (#346)
- Inline-image **`/L` length** used to avoid false-positive `EI` in binary data. (#347)
- **Redaction re-encodes kept CID/CJK text** with original codes instead of unrenderable Unicode. (#353)
- ToUnicode CMap parse no longer swallows fatal exceptions. (#345)
- Headless test harness wires ReactiveUI to the Avalonia dispatcher — fixes a cross-thread `CanExecute` crash. (#358)

### Tests
- +18 tests: parser recursion limits, inline-image `/L`, CID-redaction pipeline, and previously-untested operators (`sh`, marked content, `BX`/`EX`, `d0`/`d1`). Full Pdfe.Core suite: 2562 passing.

### Known limitations / deferred
- Inline-image redaction round-trip (#354), Form XObject redaction (#355, flatten-then-redact), and rotated-page redaction (#356) remain open.

## [2.0.0] — 2026-04-25

The headline of v2.0 is **a complete rewrite of the PDF stack**. v1.0 sat on
top of PdfPig + PDFsharp + PDFtoImage (PDFium) + Tesseract.NET; v2.0 ships a
pure-.NET stack of pdfe-owned libraries — Pdfe.Core (parser/writer),
Pdfe.Rendering (SkiaSharp renderer), and Pdfe.Ocr (system tesseract shell) —
with no external PDF dependencies remaining. Same redaction guarantee, same
GUI, fewer moving parts, and the renderer now handles real-world PDFs from
WeasyPrint, Word, XEP, and CJK toolchains without falling back to garbage.

### Added

#### Pdfe.Core — pure-.NET PDF parser, writer, and content-stream library
- M1: parser for objects, indirect references, xref, encrypted streams. Plus
  tolerant recovery for the off-by-one /Length and stale-startxref errors
  that are common in real PDFs.
- M2: text extraction with letter-level positions, replacing PdfPig.
- M3: document writing — incremental save, full rewrite, object streams.
- M4: graphics API — `PdfGraphics` with path, text, image, and state ops.
- Content-stream parsing + serialization (`ContentStreamReader` /
  `ContentStreamWriter`) backing redaction.
- Glyph-level text segmentation: `LetterFinder`, `OperationReconstructor`,
  `GlyphRemover`, plus `PdfPageRedactionExtensions.RedactArea` /
  `RedactAreas` / `RedactText`.
- Image redaction: `ImageRedactor` tracks the CTM through `q`/`Q`/`cm` and
  removes Image XObject `Do` ops that overlap the redaction area.
- Hidden-text detection: `HiddenTextDetector` finds text occluded by later
  opaque obstructions (the classic "black box on top of text" bad-redaction
  pattern). `ObstructionStripper` peels overlays for the differential pass.
- Document authoring: `PdfDocument.CreateNew()`, `Pages.AddBlank(w, h)`,
  `page.GetGraphics()` — synthesize PDFs in-memory without the legacy stack.
- Page manipulation APIs: `Pages.Add`/`Insert`/`RemoveAt`, `page.Rotation`.
- Indirect /Length stream resolution via parser callback (XEP, LibreOffice,
  and other toolchains routinely use this).
- `PdfPage.GetFont` resolves indirect /Font references (WeasyPrint, Word,
  Office, and almost every browser-derived PDF).

#### Pdfe.Rendering — SkiaSharp-based renderer
- M5: full renderer covering text, paths, images, transparency, clipping
  paths, soft masks, ExtGState, color spaces, shading, and inline images.
- Embedded font support:
  - `/FontFile2` (TrueType) loaded directly into SKTypeface.
  - `/FontFile3` raw CFF (Type1C, CIDFontType0C) wrapped into a synthesized
    OpenType container with a Unicode cmap derived from /Differences.
  - `/Encoding` dictionaries with `/Differences` resolved against the Adobe
    Glyph List, falling back to AGL §D.1 `uniXXXX` for non-named glyphs.
  - Per-font glyph widths from the PDF's `/Widths` array (loaded *before*
    CFF wrapping, fixing a stale-state bug where every embedded font was
    wrapped with the previous font's widths).
- Type0 / CIDFontType2 (Identity-H) — full CJK rendering pipeline.
- Browser-style flipped text matrix (`Tm = 1 0 0 -1 e f`) handled correctly
  in both the simple-font and Type0 paths — fixes upside-down rendering
  found in the IRS-1040 footer, every WeasyPrint-produced page, and all CJK.
- Layout-correct text advance for non-embedded fonts via the PDF's `/Widths`
  table (instead of the system fallback's `MeasureText`).
- Tc / Tw scaled by the text-matrix X-scale, per PDF spec 9.4.4 (fixes the
  "Word-derived government form mid-word gap" pattern).
- TJ array kerning routed through the text-matrix X-scale, not Y-scale —
  fixes 6%-per-glyph drift in non-uniform Tm headers (SCOTUS opinions).
- Td/TD offsets transformed through the text matrix per PDF spec 9.4.2.
- Wingdings / dingbat fallback: when an embedded CFF subset wraps cleanly
  but Skia can't extract any glyph outlines, fall back to a system symbol
  font (Noto Sans Symbols2) so the user sees a glyph instead of `⊠`.
- Visual regression test infrastructure with PNG baselines.
- Dropped `PDFtoImage` / `PDFium` native dependency.

#### Pdfe.Ocr — OCR via system `tesseract` CLI
- New project. Shells out to the system tesseract binary, parses TSV
  output, returns `OcrResult` with per-word bounding boxes.
- Differential OCR auditor: render the page twice (once with overlays
  stripped, once without), OCR both, diff the word sets — surfaces text
  hidden inside rasters by overlay, the rasterized analogue of structural
  redaction.
- Replaces the previous Tesseract.NET nuget binding (which pinned to a
  leptonica version no longer shipping on modern Linux).

#### Pdfe.Cli — `pdfe` command-line tool
- `pdfe render <file> -o out.png [--page N] [--dpi N]`
- `pdfe redact <file> -o out.pdf --text "PHRASE"` — glyph-level removal.
- `pdfe audit <file> [--deep] [--json]` — structural and (with `--deep`)
  differential-OCR audit of hidden text.
- `pdfe ocr <file>` — OCR the page and emit TSV.

#### GUI — PdfEditor
- New reusable `PdfViewerControl` (Avalonia UserControl) with overlay layers
  for selection, search highlights, redaction marquee, and hidden-text
  reveal. Replaces the bespoke MainWindow rendering.
- `MainWindow` rewritten on top of `PdfViewerControl`.
- Reveal Hidden Text — Tools → "Reveal Hidden Text" toggle. Yellow boxes
  for structural detections (text covered by rectangles), orange boxes for
  differential-OCR recoveries (text inside rasterized images).
- Open PDF from command-line argument on startup.

### Changed

- All seven GUI services migrated from PdfPig / PDFsharp / PDFtoImage to
  Pdfe.Core / Pdfe.Rendering: `PdfRenderService`, `PdfTextExtractionService`,
  `PdfSearchService`, `SignatureVerificationService`, `PdfDocumentService`,
  `BatesNumberingService`, `RedactionService`.
- `RedactionService` unified — `RedactArea` (mouse marquee) and `RedactText`
  (find-and-redact) now share a single Pdfe.Core pipeline; the previous
  parallel PdfSharp+PdfPig path is gone.
- The legacy `PdfEditor.Redaction` library (and its `pdfer` CLI) deleted —
  glyph-level redaction lives in Pdfe.Core; the Pdfe.Cli `redact` command
  replaces `pdfer`.
- System-font fallback widened: strip the 6-letter PDF subset prefix,
  match by family prefix instead of exact name, and recognize Semibold /
  Medium as Bold. `TimesNewRomanPS-BoldMT` now correctly maps to Times New
  Roman instead of Sans-Serif; `BookmanStd` to Times; `ZapfDingbatsStd` to
  Noto Sans Symbols2.
- Build is clean — 0 warnings, 0 errors across all projects.

### Removed

- **PdfPig 0.1.11** — replaced by `Pdfe.Core.Text`.
- **PDFsharp 6.2.2** — replaced by `Pdfe.Core.Document` + `Pdfe.Core.Writing`.
- **PDFtoImage 4.0.2** + native PDFium — replaced by `Pdfe.Rendering` (Skia).
- **Tesseract.NET nuget** — replaced by `Pdfe.Ocr` (CLI shell).
- **PdfEditor.Redaction** project + **`pdfer` CLI** — replaced by
  Pdfe.Core glyph-level redaction + `pdfe redact`.
- **PdfEditor.Demo** + Validator tools — superseded by Pdfe.Cli + the new
  visual regression suite.

### Fixed

#### Renderer — real-world PDF reliability
- Stream `/Length` as an indirect reference no longer rejected (XEP,
  LibreOffice). Parser exposes an `IndirectObjectResolver` callback which
  `PdfDocument` wires to its own object cache.
- `\<EOL>` line continuations in literal strings (PDF spec 7.3.4.2)
  stripped correctly — fixes the `⊠` placeholders that appeared at the end
  of long underline runs in Word-derived government forms.
- Embedded-font /Widths loaded *before* the CFF→OpenType wrapper runs;
  previously every embedded font got hmtx widths from the previously-active
  font (or zero for the first font), producing visibly broken layout on
  multi-font pages — every page after the cover of any XEP-produced book.
- AGL reverse lookup synthesizes `uniXXXX` names for BMP codepoints not in
  the named-glyph table — required for CFF subsets keyed on uniXXXX names.
- Post-wrap outline probe: if a wrapped CFF resolves cmap entries but
  produces no glyph outlines, fall back to a system font instead of
  rendering empty space (catches a class of XEP-produced ZapfDingbats
  subsets where Skia's CFF interpreter can't extract charstrings).
- Y-flip applied conditionally on the sign of `Tm.d`, fixing upside-down
  text in browser-flipped Tm content (CJK, WeasyPrint, IRS-1040 footer).
- Effective font size computed from the text matrix Y-scale (handles the
  common `1 Tf` + scaled `Tm` idiom).
- Cursor advance honors text-matrix non-uniform scaling.
- `CodePagesEncodingProvider` registered for Windows-1252 / WinAnsi support.
- Search highlights refresh when the user changes pages manually.
- Birth-cert form layout: routes non-embedded fonts through the PDF's
  `/Widths` array for cursor advance instead of the substituted system
  typeface's metrics — fixes mid-word gaps in TJ-kerning-heavy PDFs.

#### Tests
- `PdfViewerControl_PageChanged_FiresEvent` deflaked. Test was timing-
  sensitive on the shared Avalonia headless dispatcher; now waits
  deterministically for the event with a 30-second deadline.

### Verified rendering

The new renderer has been smoke-tested against a real-world corpus:

| PDF | Source | Notes |
|---|---|---|
| Birth Certificate Request (CT) | scanned/scrambled gov form | TJ kerning, Tw column alignment, raster background |
| SCOTUS opinion (Trump v. Anderson) | Court PDF | Non-uniform Tm headers, Type1 PostScript subsets |
| IRS Form 1040 + Instructions | IRS / Adobe Distiller | Type0/Identity-H, Acrobat-distilled, 180° footer text |
| State Dept DS-82 (passport renewal) | XFA + Type0 | Acrobat / XFA mix |
| CDC COVID-19 VIS | CDC | Embedded TrueType, Wingdings dingbats |
| "Business Success with Open Source" | Pragmatic Bookshelf / XEP | 455 pages, multi-font CFF subsets, ZapfDingbats |
| Multilingual CJK fixture | WeasyPrint + Noto CJK | zh-Hans, zh-Hant, ja, ko |

All render essentially identically to mutool / Acrobat at the structural
level. `Pdfe.Rendering.Tests/Visual/` and `PdfEditor.Tests/UI/baselines/`
keep PNG baselines for regression detection.

### Migration

The architectural change is mostly transparent for end users — the desktop
app, the redaction guarantee, and the file format are unchanged. For
embedders moving off the v1.0 surface:

- `PdfEditor.Redaction` (library) → `Pdfe.Core.Text.Segmentation` —
  use `page.RedactArea(rect)` / `page.RedactAreas(rects)` /
  `document.RedactText("phrase")` from `PdfPageRedactionExtensions` /
  `PdfDocumentRedactionExtensions`.
- `pdfer` CLI → `pdfe redact` — same options.
- PdfPig text extraction → `Pdfe.Core.Text` — `PdfDocument.GetText(page)`
  and `PdfDocument.GetLetters(page)`.
- PDFsharp `PdfDocument` → `Pdfe.Core.Document.PdfDocument` — note that
  `PdfDocument.Open(stream)` now takes ownership semantics via
  `Open(stream, ownsStream)`.
- PDFtoImage → `Pdfe.Rendering.SkiaRenderer.RenderPage(page, options)`.

### Known gaps deferred to v2.1+

- PDF encryption / password handling (#237) — v2.1.
- Partial glyph rasterization for redaction cuts that bisect a glyph
  (#278). Current full-glyph removal is conservative-safe.
- PDF Annotations (#271), Interactive Forms (#272), Tagged PDF (#275),
  Advanced Transparency (#274), Multimedia (#273) — v2.2.
- Compass-image-style inline-image-with-Smask cases that still fall back
  to placeholder rendering (covered indirectly by #274).

### Test counts at release

- Pdfe.Core.Tests: 442 passing, 2 skipped
- Pdfe.Rendering.Tests: 175 passing
- Pdfe.Cli.Tests: 7 passing
- PdfEditor.Tests: 221 passing, 2 skipped (require Tesseract installed)

**Total: 845 tests, 0 failing**

---

## [1.0.0] — 2026-01-11

First major stable release. Cross-platform PDF editor with **true
glyph-level redaction** — content removed from the PDF structure, not just
visually covered. Built on PdfPig + PDFsharp + PDFtoImage + Tesseract.NET.

See the GitHub release for full v1.0.0 notes:
https://github.com/marctjones/pdfe/releases/tag/v1.0.0
