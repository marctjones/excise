# Changelog

All notable changes to excise are documented here. Format roughly follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses
semantic versioning.

## [Unreleased]

### Added
- **PDF 2.0 page-level and document-level structural features: parse, model,
  round-trip** (#331) — page transitions (`/Trans`, all twelve ISO
  32000-2:2020 §12.4.4 styles: Split/Blinds/Box/Wipe/Dissolve/Glitter/R
  ("Replace")/Fly/Push/Cover/Uncover/Fade, plus duration/dimension/motion/
  direction/fly-scale/fly-rectangle) via `PdfPage.Transition`; page display
  duration (`/Dur`) via `PdfPage.Duration`; embedded page thumbnails (`/Thumb`)
  via `PdfPage.ThumbnailStream` (parsed and preserved, deliberately not
  decoded/rendered — a thumbnail strip should fall back to the renderer when
  null); and document/page actions (`/OpenAction` — both the modern action-
  dictionary form and the legacy bare-destination-array form —, `/AA` on both
  document and page, and the `/Names/JavaScript` name tree) via the new
  `PdfAction` model (`PdfDocument.OpenAction`/`.AdditionalActions`/
  `.DocumentJavaScriptActions`, `PdfPage.AdditionalActions`). `PdfAction`
  never executes anything it parses — `JavaScriptSource` decodes `/JS`
  (string or stream form) purely as inert data for inspection/audit, and
  `/Next` action chains are followed (depth-capped) without evaluation. Page
  labels (`/PageLabels` → `PdfDocument.GetPageLabel`) and named destinations
  (`/Dests` and `/Names/Dests`, both forms → `PdfDocument.GetNamedDestinations`)
  were already implemented; this issue added save/reopen round-trip coverage
  for both. All of the above are purely additive parse-side properties — no
  writer changes — so save output for documents that don't use these features
  is unaffected. UI integration (presentation-mode playback, thumbnail-strip
  wiring, page-label status bar) is explicitly deferred, per the issue.
- **Accessibility MCID→letter bridge: screen readers read tagged elements'
  real body text (#776).** Follow-up to the tagged-PDF structure layer (#631,
  PR #775). Text extraction now tags each `Letter` with the marked-content ID
  (`/MCID`) of the `BDC ... EMC` span it was drawn inside, and
  `PdfDocument.ResolveStructElementText` gathers a structure element's glyphs by
  matching its `/MCID` references (both `/K` integers and `/MCR` child
  dictionaries, each honouring its `/Pg`) in reading order. The
  `Excise.Avalonia` accessibility peers use this so a heading, list item, or
  table cell with no `/ActualText` carrier now exposes its real body text to a
  screen reader instead of a role-only peer. The MCID tagging is additive: it
  does not change extraction output (verified by the extraction-parity gate,
  which is unchanged).
- **OCG-aware text extraction now resolves OCMD membership and visibility
  expressions** (#336) — the per-letter hidden-layer flag
  (`Letter.IsInHiddenOptionalContent`) previously identified only content
  inside a directly-referenced Optional Content Group named in the catalog
  `/OCProperties /D /OFF` array, matched by name. It now resolves the full
  default-configuration visibility of a marked-content `/OC` span through a
  shared resolver (`Excise.Core/Document/OptionalContentVisibility.cs`):
  Optional Content Membership Dictionaries (`/Type /OCMD`) with a `/P` policy
  (AnyOn/AllOn/AnyOff/AllOff) or a `/VE` And/Or/Not visibility expression, OCG
  membership matched by object reference (so two OCGs sharing a `/Name` are
  distinguished), `/ON` arrays, and `/BaseState /OFF`. This makes hidden-layer
  content in those carriers precisely identifiable for audit and for the
  `RedactText(includeHiddenLayers: false)` opt-out (which now correctly skips
  them). Default redaction is unaffected: `RedactText` includes hidden layers
  by default and already reached this content — the flag governs identification
  and the opt-out, not the default removal path. Default extraction output is
  likewise unchanged (hidden layers are still extracted; only the flag differs)
  — the extraction-parity gate holds at 98.7% / 332 pages. The SkiaSharp
  renderer already suppressed paint for default-off optional content (Part C);
  this brings the text extractor's OCG resolution to parity with it.
  Structure-tree mutation on redaction (Part B) shipped earlier as #636.
- **ICCBased-CMYK (N=4) overprint participation** (#803, follow-up to #634) —
  a fill or stroke whose colour space is an ICCBased space with four
  components now takes part in overprint simulation, treated as DeviceCMYK
  under the same nonzero-overprint-mode (`/OPM 1`) gating: a component that is
  exactly zero leaves that colorant of the backdrop unchanged instead of
  knocking it out. Previously such a colour knocked out even under `/OP`
  `/op` `/OPM 1`. Participation is preview-grade only — the raw four
  components drive the zero-component merge; there is still no colour-managed
  ICC CMM. Verified against the `gs -dOverprint=/simulate` oracle and by
  spec-driven relative tests inside a DeviceCMYK transparency group.
- **App-wide in-session undo/redo** (#782) — a single edit-history stack
  (command pattern with per-operation inverse closures, plus a collection
  snapshot for type-over edits) now covers the reversible, pre-flatten editing
  state: type-over create/edit/move/delete, annotation authoring (highlight and
  sticky-note add), and page reorder/rotate/delete. Wired to Ctrl+Z / Ctrl+Y
  (Cmd+Z / Cmd+Shift+Z on macOS) and the Edit menu, with live `Undo`/`Redo`
  labels that name the pending action. The stack clears on document open, close,
  and save — content already flattened into the PDF content stream on save is
  irreversible by design and is never recorded.
- **Audit flag for symbolic (3,0) glyphs no extractor recovers** (#796) — a
  simple symbolic TrueType with a Microsoft-Symbol `(3,0)` cmap AND an
  `/Encoding` renders meaningful text through its `(3,0)` glyphs, but every
  extractor (excise, mutool, poppler — established by #794/#795) honours
  `/Encoding` and extracts the WinAnsi interpretation instead. The visible text
  is therefore unrecoverable and `RedactText` cannot reach it. excise
  deliberately matches the reference tools rather than diverging (no
  self-oracle), so #796 is the conservative response: `HiddenTextDetector` now
  DETECTS this class — it decodes each such font's `(3,0)`/`post` glyph names
  (reusing #791's `TrueTypeFontFile.GidForSymbolByte`/`GlyphName`) and, where
  that spells real text that DIVERGES from what extraction yielded, emits a
  finding ("visible text via (3,0) symbol cmap not recoverable by extraction —
  redaction may not reach it") through the same audit surface as every other
  hidden-text finding (CLI, GUI reveal, redacted-copy safety check). Detection
  only — no change to extraction decoding, and no false flag for a normal
  WinAnsi font, a `(3,0)` font WITHOUT `/Encoding` (#791 already extracts it),
  or a `(3,0)`+`/Encoding` font whose decode already equals the extracted text.
- **Overprint simulation for Separation and DeviceN colours (#634).** Overprint
  (ISO 32000-1 §8.6.7) previously engaged only for DeviceCMYK fills/strokes;
  it now also engages for Separation and DeviceN colours whose tint transform
  resolves to a DeviceCMYK alternate. Such a colour is tint-transformed to
  DeviceCMYK (already done for display), and overprint then leaves the process
  colorants the transform outputs as zero unchanged in the backdrop instead of
  knocking them out. Per the spec this "unnamed colorants stay put" rule applies
  whenever `/OP`/`/op` is set **regardless of `/OPM`** — unlike DeviceCMYK,
  which still requires `/OPM 1`. Verified against Ghostscript's overprint
  simulation (`gs -dOverprint=/simulate`, the only harness reference renderer
  that simulates overprint on an RGB device): a spot colour mapping to yellow
  over a cyan backdrop keeps the cyan (green overlap) under both `/OPM 0` and
  `/OPM 1`, matching the oracle, versus a plain-yellow knockout with overprint
  off (`SeparationOverprintDifferentialTests`, plus relative
  `SeparationOverprintRenderingTests`). Works both outside and inside a
  DeviceCMYK transparency group. DeviceCMYK `/OPM 0`/`/OPM 1` behaviour is
  unchanged (the existing Ghent GWG011 OPM-mode traps still pass). ICC colour
  management remains preview-grade with no real CMM (matrix/TRC and lut16 A2B
  tables are genuine transforms; there are no rendering intents, black-point
  compensation, or gamut mapping), and ICCBased-CMYK overprint is not yet
  handled — tracked as #803.

### Performance
- **Renderer image decode / colour-conversion allocation cuts** (#599) — the
  image decode path no longer stages a large transient managed pixel buffer per
  image. The raw, DCT-RGB, and fast Gray/RGB/CMYK decoders now fill the final
  `SKBitmap`'s pixel store directly (via a writable span) instead of allocating
  a `width*height*4` byte[] and `Marshal.Copy`-ing it in, and the fast CMYK
  decoder reuses one CMYK sample buffer instead of allocating a `double[4]` per
  pixel — that per-pixel array was the dominant allocator on colour-heavy CMYK
  pages. The `/Decode`-array lookup and max-sample constant are hoisted out of
  the per-pixel loop. Decoded images are also cached across child render
  contexts (transparency groups / tiling patterns / soft masks), so an image
  reused inside a group or pattern decodes once per page, not once per context;
  only the owning context disposes the shared cache. Measured in Release on the
  colour-heavy Altona visual suite (`altona_visual_1v2a_x3.pdf` p1, min-of-4):
  managed allocation 2473.7 → 2028.1 MB (-18%) with render time flat; the
  remaining per-pixel cost is the ICC CMYK→RGB conversion in `Excise.Core`
  (out of scope here). Output is byte-identical — verified by the Visual PNG
  baselines (63/0 unchanged) and the full `Excise.Rendering.Tests` differential
  suite (3463/0). The decoded-image cache lifetime is one page render.
- **Renderer glyph-outline caching on the text hot path** (#598) — glyph
  outlines are now tessellated once per (typeface, size, glyph) and reused for
  the rest of the page instead of re-decoding the same outline on every draw.
  The dominant win is the embedded-subset-font path (Type0/CID and byte-cmap
  simple fonts drawn glyph-by-glyph via `SKFont.GetGlyphPath` in
  `BuildGlyphIdTextPath`), where real-world body text recurs the same glyph IDs
  thousands of times per page: on the smoke corpus the outline cache serves
  94.6% of glyph lookups (≈917k hits / 970k lookups; ≈561k avoided
  tessellations on `irs-1040-instructions.pdf` alone). Measured back-to-back in
  Release on the smoke corpus (`scripts/check-perf-budgets.sh`, min-of-3, with
  the non-rendering `text-extract` workflow flat as a machine-stability
  control): `all-page-render` managed allocation 206→197 MB (-4.4%) and render
  time 794→749 ms (-5.7%); `navigation-rerender` 271→258 ms (-4.8%). The caches
  hold only the UNPOSITIONED outline; every draw still transforms a fresh copy
  to its own cursor/scale, so output is byte-identical — verified by the Visual
  PNG baselines (63/0 unchanged) and the mutool differential smoke suite (49/0)
  on the embedded-font corpus. Cache lifetime is one page render; keys compare
  the typeface by reference.
- **Text-extraction hot path: fewer allocations, less CPU** (#600) — the
  `TextExtractor` content-stream parse now caches all per-font derived state
  (ToUnicode map, `/Differences`, the Identity / Mac-glyph-order /
  embedded-CID / symbol-cmap decode tables, and the CID/CMap/`/W` width
  geometry) keyed by the resolved font dictionary, instead of re-parsing those
  streams on every `Tf` operator — the dominant repeated cost, since every
  text block re-issues `Tf`. `ParseNumber` gained an exact inline integer
  parser for the common operand (TJ kerns, `Td`/`Tm`/`cm` coordinates) that
  bypasses `int.TryParse`'s culture machinery, and the operand list is
  pre-sized. Measured over `test-pdfs/smoke` (min-of-3, Release,
  `scripts/check-perf-budgets.sh`): text-extract allocation **389.7 → 160.7 MB
  (-59%)** and wall time **230.2 → 164.7 ms (-29%)**; redaction-save
  allocation also fell ~8% (it shares the extractor). Every change is
  behavior-preserving: the font cache re-assigns all derived fields on each
  `Tf` (a snapshot, never a skip-if-same short-circuit, so it still heals the
  partial state restore in form-XObject parsing) and the integer parser falls
  back to `int.TryParse` for any non-trivial span. The 332-page
  extraction-parity gate is **unchanged at 98.7%** — proving extraction output
  (and therefore redaction reach) did not move. The text-extract allocation
  budget was tightened to the new floor to lock in the win.

### Fixed
- **Flaky redaction test: `FullwidthFormsRedactionTests` collided with the
  random `/ID`** (#771, #800) — the fullwidth-forms redaction test intermittently
  failed on macOS and Windows CI (same commit could pass on Linux and fail on
  macOS — definitionally non-deterministic, and it was a false red, never a real
  leak). Root cause, measured directly: the test's carrier-agnostic saved-bytes
  oracle searched the WHOLE file for a short ASCII needle (`"123"`, `"ABC"`),
  which collided with the trailer `/ID` — a random 16-byte file identifier
  written twice as 32 hex characters and regenerated on every save. Over 20,000
  saves the needle appeared inside `/ID` 139–143 times and in NO real carrier
  even once; lowercase `"abc"` never collided because hex is uppercase, which is
  exactly why only the ABC and 123 cases were ever reported. #771's font-metric
  guess and #800's parallelism guess were both wrong: the redaction box width is
  the platform-independent constant `49.344` (Helvetica AFM advances A=667, B=667,
  C=722, ×24/1000 — identical on every platform, and identical across all three
  cases since the fixture always emits codes A/B/C), so parallel CI only raised
  the number of draws on a ~0.7%-per-needle random `/ID` collision. Fixed by excising
  the `/ID` array from the searchable view before the needle check (a lossless
  Latin1 round-trip that keeps every real text carrier — content streams,
  ToUnicode, `/ActualText`, XMP, annotations, hex text strings — in the search).
  The redaction guarantee is unchanged: no redaction code path writes page text
  into `/ID`, so removing a random identifier eliminates a false-positive source,
  not a leak-detection surface. The other redaction assertions (independent
  saved-bytes carrier search, extractor-agnostic, plus the removed>0 and reopened
  checks) are intact.

### Changed
- **Linux coverage gate restored to green** (R6 CI health) — `Excise.Core` line
  coverage had drifted to 92.39% on `develop`, below the 93% ratchet, reddening
  every merge. Added targeted unit tests for the least-covered recently-added
  Core code: `SignatureAppearanceAuthoring` (#623 baked `/AP /N` signature
  appearance, previously 0% covered), the `TrueTypeFontFile` symbolic `(3,0)`
  cmap parse + `post`-glyph-name path (#791, driven by an in-assembly symbol-cmap
  font builder), and deterministic `PdfOutlineParser` destination resolution
  (direct `/Dest`, `/A` GoTo, `/Names/Dests` name tree, and legacy
  `/Catalog/Dests`) — the pre-existing outline tests silently no-op'd on CI
  because they load a book from a hard-coded local path (a #619-style invisible
  coverage loss). Clears the existing 93% gate (measured 93.27% locally, up from
  the 92.39% that had reddened `develop`; the Linux CI coverage job is the
  authority); the threshold itself is unchanged.
- **`Excise.Cli.Tests` wall-clock cut ~93% (72s → 5s measured, `-c Release`
  only) by removing an accidental full rebuild** (#731). Root cause:
  `BenchmarkSuite.ResolveExciseCliInvocation()` (`tools/Excise.RenderTools/`
  — the shared implementation behind the shipping `benchmark-suite` CLI
  command, not test-only code) picked which `Excise.Cli/bin/<config>/`
  directory to look in via this assembly's own `#if DEBUG`/`#else "Release"`
  compile-time symbol. `Excise.RenderTools` is a tool project outside
  `excise.sln`, so it always builds Debug regardless of what configuration
  the CLI or the calling process used. `run-benchmarks.sh`,
  `check-perf-budgets.sh`, and every `release-smoke.sh`/`ci.yml` caller
  already set the `CONFIG` env var explicitly and were never affected by the
  symbol; only `BenchmarkSuiteTests`, which calls `RenderProgram.RunAsync`
  in-process with no `CONFIG` set, hit the bug. Any `-c Release`
  `Excise.Cli.Tests` run (`release-smoke.sh --release-tests`, or a
  maintainer running `dotnet test -c Release` directly) looked for a Debug
  binary a Release-only build never produces, silently fell through to
  `dotnet run -c Debug --`, and paid for a from-scratch Debug build of
  `Excise.Cli` + `Excise.Core` + `Excise.Rendering` + `Excise.Ocr` inside a
  single test (`BenchmarkSuite_WritesJsonCsvMarkdownAndPassesSyntheticGate`,
  measured 68 of the suite's 72s). Fixed by probing the filesystem for an
  already-built `excise`/`excise.dll` under both `Release` and `Debug`
  before ever falling back to `dotnet run` — same CLI binary invoked, same
  assertions, same 126 tests (125 passed / 1 skipped, unchanged) — a
  redundant-build removal, not a coverage or behavior change. Verified `-c
  Debug` (CI's PR gate, `t0`/`t1`) is unaffected either way: 865ms before and
  after, with only a Debug build on disk (simulating a fresh CI checkout) —
  RenderTools' Debug default already matched, so this fix saves nothing on
  the PR gate; the win applies to `t2`/`release-smoke.sh --release-tests`
  and any local `-c Release` run. Measured this session on a 10-core Apple
  Silicon machine, Release build, all five suites run individually:
  `Excise.Core.Tests` 4s (3755 tests), `Excise.Rendering.Tests` 32s (3420
  tests, already 4-way parallel per #732), `Excise.App.Tests` 226s (1098
  tests, serial by design — #363), `Excise.Avalonia.Tests` 2s (86 tests),
  `Excise.Cli.Tests` 72s→5s (126 tests). Investigated and explicitly NOT
  pursued this pass, per the #731 analysis already on the issue: overlapping
  `Excise.Core.Tests` with the now-4-way-parallel `Excise.Rendering.Tests` in
  `release-smoke.sh`, and sharding `Excise.App.Tests` across concurrent
  local processes — both reintroduce the CPU-contention false-red class #619
  exists to prevent (wall-clock-budgeted tests reading as hangs under
  contention), for a smaller, single-machine-only saving than this fix.
  `Excise.App.Tests` stays serial (#363 SkiaSharp native font-manager crash)
  and untouched.

## [3.3.1] - 2026-07-26
- **GUI interaction latency: per-page search-highlight index (#601).** Page
  navigation recomputed the current page's search highlights with a linear
  `O(total matches)` scan over every match on *every* page flip, so the cost
  grew with document size (a dense search on a large book — thousands of
  matches — made each page change scan all of them). Matches are now indexed by
  page once when results publish, making the per-navigation lookup
  `O(matches on the target page)`. Measured (new `GuiLatencyBenchmarkTests`,
  400-page document, 2,000-match active search, 2 runs): the per-navigation
  match lookup dropped from **~22 µs to ~0.05 µs** (~400×), and — being now
  independent of match count — no longer scales with the document (the old scan
  was linear in total matches, so ~2 ms at 200k matches).
  **User-visible latency was already sub-frame and is unchanged within
  measurement noise** — every direct interaction averaged well under one 60 Hz
  input frame both before and after (end-to-end page navigation ≈ 0.5–0.7 ms,
  its run-to-run baseline jitter larger than the change). The value is removing
  the one per-interaction cost that scaled with document content, plus the new
  benchmark that gates each profiled interaction under a 16 ms budget going
  forward.

### Fixed
- **Symbolic TrueType with a (3,0) symbol cmap: text extraction mis-decode**
  (#791) — a simple (non-Type0) symbolic TrueType font that carries a
  Microsoft-Symbol `(3,0)` cmap subtable and ships no `/ToUnicode` addresses
  glyphs through an F000-based Private Use offset. excise rendered such fonts
  correctly but text EXTRACTION echoed the raw content byte through
  WinAnsi — e.g. a page reading "Redaction" extracted as `¡¢£¤¥¦§¨©`. Because
  extraction bounds redaction, `RedactText` then removed **0** occurrences and
  reported success: a silent redaction leak (CLAUDE.md, #637/#645). Extraction
  now resolves such fonts through the embedded program's `(3,0)` cmap
  (code→glyph, ISO 32000-2 §9.6.6.4) and recovers Unicode from the program's
  `post` glyph names (or a Unicode cmap subtable), matching the independent
  oracle (mutool). Scoped strictly to symbolic simple TrueType with an embedded
  `(3,0)` cmap and no `/ToUnicode` / no `/Encoding`, so every other simple font
  keeps its existing decode — the 332-page extraction-parity gate stays at
  98.7%. A purpose-built fixture (`SymbolCmapTtfBuilder`, patches DejaVu Sans to
  a `(3,0)` symbol cmap) proves render parity, extraction parity, and — the
  redaction-relevance made concrete — that excise now removes the text and
  mutool confirms it is gone.

### Investigated (no code change)
- **Symbolic TrueType with a (3,0) symbol cmap AND `/Encoding` present**
  (#794) — the sibling case of #791 was measured and found **not to reproduce**
  as a excise-specific mis-decode, so no extraction/precedence change was made.
  #791 fixed the **no-`/Encoding`** shape (where mutool recovers the intended
  text from the `post` glyph names, so excise was made to match). #794 asked
  whether the same font **with `/Encoding /WinAnsiEncoding`** (or a
  `/Differences` dict) still mis-decodes. Controlled measurement against two
  independent oracles (fixture: `SymbolCmapTtfBuilder` + non-ASCII codes
  0xA1..0xA9 so WinAnsi(code) != the intended letter):

  | fixture | excise | mutool | poppler |
  |---|---|---|---|
  | NO `/Encoding` (#791 shape) | Redaction | Redaction | ¡¢£… |
  | `/Encoding /WinAnsiEncoding` | ¡¢£… | ¡¢£… | ¡¢£… |
  | `/Encoding <<WinAnsi base + Differences>>` | ¡¢£… | ¡¢£… | ¡¢£… |
  | `/Encoding /WinAnsiEncoding`, WinAnsi-undef codes | ••• | ••• | ••• |

  The only variable that flips mutool off `(3,0)`/`post` recovery is the
  presence of `/Encoding`: with it, **both mutool AND poppler honour WinAnsi
  and never consult the `(3,0)` cmap** — even for codes WinAnsi leaves
  undefined (they emit bullets, not the cmap glyph). excise already agrees with
  both oracles, so preferring the `(3,0)` cmap here would make excise the sole
  tool emitting "Redaction" — the no-self-oracle violation CLAUDE.md forbids.
  (Spec tension noted for a human call: ISO 32000-2 §9.6.6.4 says a symbolic
  TrueType ignores `/Encoding`, so the oracles are arguably non-compliant.)
  Characterization tests
  (`SymbolicTrueTypeSymbolCmapWithEncodingExtractionTests`) pin that excise
  matches the independent oracle for each shape, that redaction removes the
  extracted text with mutool confirming removal, and that an explicit
  `/Differences` per-code name is honoured (§9.6.6.2 precedence).

## [3.3.0] - 2026-07-26

### Added
- **Type-over tool: GUI-save independent-oracle test coverage** (#780) — closed
  a no-self-oracle gap in type-over save verification. The existing GUI-save
  test reopened the file and verified with excise's own extractor
  (`saved.GetPage(1).Text` — excise vouching for excise), and the independent
  extractor check lived only in the engine-path fidelity suite
  (`PdfTypewriterTextApplier`, not the GUI save command). A new headless test
  now drives the REAL `SaveFileAsAsync` command → disk → an INDEPENDENT
  extractor (`MutoolTextExtractor.ExtractPage`), asserting the typed note reads
  back and the pre-existing page text survives. Skips gracefully where mutool
  is absent.
- **Type-over tool: move/resize-handle and wrap-parity test coverage** (#780) —
  closed the two coverage gaps left after the type-over workflow work. A
  control-level headless test drives a real routed pointer gesture
  (press → move → release) on the editor's move handle and resize grip and
  asserts the `TypewriterTextBoundsChanged` PDF bounds reflect the drag (move:
  position shifts, size preserved; resize: size grows, the anchored corner is
  fixed). A wrap-parity test compares the on-screen editor `TextBox` wrapping
  (Avalonia `TextWrapping.Wrap`) against the flattened PDF output (Skia +
  base-14 metrics) via save/reopen/extract, asserting both wrap to multiple
  lines, all words survive in reading order, and the line counts agree within
  ±1 (observed 4 vs 4 exactly).

### Fixed
- **Typewriter (type-over) workflow: pending edits can no longer be lost or
  flattened unseen** (#780) — pending type-over edits used to persist silently
  after leaving typewriter mode and bake into the PDF on the next save with no
  signal, off-page edits were never shown yet still flattened, and a plain
  click placed nothing. Now: mode-exit is non-destructive and the pending-edit
  count stays visible in the status bar; an explicit "Discard Pending Type-over
  Edits" command is the only non-saving way to clear them; "Go to Next Pending
  Type-over Edit" navigates to off-page edits before they commit; `Esc` removes
  an empty active box and keeps typed text in a non-empty one; and a click now
  places a default-sized box (drag still sizes it). Added GUI/headless coverage
  for the pointer-driven creation path, DIP↔PDF round-trips (incl. `/Rotate`
  90/180/270 and page clamp), the on-create permission re-check, and mode-exit
  non-loss / discard verified by reopening the saved PDF.
- **RTL redaction: numbers inside right-to-left lines no longer evade
  removal** (#632) — a number embedded in an Arabic/Hebrew line (an ID, date,
  or phone number) kept its surrounding words in visual order, so a
  phrase-spanning-a-number search matched nothing and `RedactText` silently
  removed nothing while reporting success. Digit "islands" now reorder to
  logical order (segments reverse, the number stays put), so RTL content that
  contains numbers is searchable and redactable. Verified against the Unicode
  Bidi Algorithm (UAX #9) reference, not against excise itself.

### Security
- **RTL redaction in Type0/Identity-H (CID) Arabic/Hebrew fonts verified with
  independent oracles** (#632) — the redaction-critical case where the content
  stream carries the word only as 2-byte CIDs (glyph indices), so the Unicode
  string never appears in the file and a saved-bytes search — even UTF-16BE —
  is structurally blind to it. Added fixtures embedding a real font that paint
  the word in visual (reversed) order and drive `RedactText` with a
  logical-order needle, asserted with the two mandated non-excise oracles:
  mutool independent extraction (word present before, unrecoverable after) and
  a Ghostscript ink differential over the word's region (blank after removal,
  not merely covered). Confirms logical→visual matching and true glyph removal
  in the CID path; a keep-word guards against blanking the page. Also pins, as
  a measured limitation, the whole-line bidi gap for mixed-direction lines
  (per-word RTL redaction works; a phrase spanning a direction change on an
  RTL-base line is not matched) — split to #785. No engine behaviour changed;
  the existing bidi reorder already covered these cases and this locks them
  under independent verification.
- **Type0/CID horizontal advance now scales `Tc`/`Tw` by `Th`** (#734) — for
  Type0 fonts the character/word-spacing contributions were applied outside the
  horizontal-scaling factor (`Th`), drifting extracted glyph positions on text
  that combines Type0 fonts with non-default horizontal scaling and non-zero
  spacing (ISO 32000-1 §9.4.4). Applied identically in the text extractor and
  the redaction content-stream parser so redaction bounds track letters.
- **Renderer: horizontal Type0/CID glyph advance now applies `Tc`/`Tw`**
  (#734) — `SkiaRenderer.RenderCidBytes` advanced the horizontal text matrix
  by summed `/W` glyph widths only, never adding character spacing (`Tc`) or
  word spacing (`Tw`, single-byte code 32 only per §9.3.3), unlike the
  simple-font path and the #515 vertical Type0 path, which both already
  applied them. On CJK/Type0 pages with non-zero `Tc`, rendered glyphs
  progressively fell behind where extraction and reference renderers placed
  them. Fixed per §9.4.4 (`tx = ((w0/1000)·Tfs + Tc + Tw)·Th`), mirroring the
  #515 vertical path: Tc/Tw now accumulate into the same per-glyph cursor
  used to position and advance the text matrix, so drawn positions and pen
  advance cannot drift apart. Verified against live pdftocairo/Ghostscript
  (not excise's own extractor): a synthetic Type0 fixture with `Tc`/`Tw` that
  diverged from both references by 1.8%-2.0% differing pixels before the fix
  now agrees within 0.25%.
- **Redaction on a multi-run line no longer shifts the kept text** (#758) —
  when `GlyphRemover` removed a text-showing operator from a multi-run `BT`
  block, it dropped that operator's pen advance, so kept runs after the
  redaction on the same line shifted left. The removed run's advance is now
  consumed, so following kept text stays in place (the removed content is still
  gone from every carrier).
- **Deterministic real-number formatting in the PDF writer** (#762) — every
  real-number emit site (content-stream operands in `ContentStreamWriter`,
  object serialization in `PdfObjectWriter`, `ContentOperator.ToString`) now
  formats through a shared `PdfNumberFormatter`: invariant culture, at most
  six decimal places, trailing zeros trimmed, never exponent notation. The
  previous `"G"` (shortest-round-trip) format faithfully reproduced
  accumulated float noise (`216.01600000000002`, `49.343999999999994`),
  making saved bytes differ across platforms — and on Windows a noisy
  coordinate's digit run coincidentally matched a redacted number, tripping
  the carrier-agnostic saved-bytes redaction check (a byte-check false
  positive, not a leak). Six decimals bounds any coordinate perturbation at
  5e-7 pt — far below a rendered pixel — so redaction bounds, extraction
  parity, and visual baselines are unchanged, while files get slightly
  smaller and byte-identical cross-platform. The RTL saved-bytes redaction
  tests additionally pin the trailer `/ID` (normally random bytes serialized
  as uppercase hex) so their short A–F raw-code needles can't collide with
  it either.
- **CID glyph-selection matrix: deterministic handling of missing maps**
  (#515, final slice) — the renderer's CID→GID resolution for Type0 fonts now
  handles every cell of the matrix the way the reference renderers do, each
  behavior verified empirically against poppler/Ghostscript (and mutool where
  it has CMap resources) rather than assumed:
  - A CID **absent from a CID-keyed CFF charset** selects GID 0 (.notdef)
    instead of falling through to identity — which indexed the CFF's
    unrelated glyph order with the CID and **drew an arbitrary wrong glyph**.
  - `/CIDToGIDMap` on a **CIDFontType0** descendant is ignored (§9.7.4.2:
    CIDFontType2 only); the embedded CFF charset governs, matching poppler.
  - A CID **beyond a `/CIDToGIDMap` stream's extent** keeps the identity
    fallback — the unanimous mutool/poppler/Ghostscript behavior — while
    in-range zero entries still mean an explicit .notdef.
  - A **CID-keyed CFF with a predefined/absent charset offset** now maps
    Identity over all glyphs; previously it fell into the IsoAdobe table,
    which is accidentally identity up to glyph 228 and silently unmapped
    (.notdef) above.
  In every case layout comes from `/W`/`/DW` keyed by CID, so a missing
  glyph still consumes its full advance and neighbouring positions (and the
  redaction bounds derived from them) never drift. Covered by the new
  `CidGlyphSelectionMatrixTests` — CIDFontType2 fixtures over DejaVuSans
  (explicit/identity/absent/truncated/all-zero/odd-length maps, GID beyond
  glyph count) plus a synthetic CID-keyed CFF (CIDFontType0C with a
  non-identity charset, charset misses, bogus map) — with live
  pdftocairo/Ghostscript differentials pinning the out-of-range fallback.

### Added
- **Right-to-left text selection in the viewer** (#373) — selecting a line that
  contains Arabic/Hebrew now copies the text in logical reading order (the way
  it is read) rather than the visual order it is painted in, reusing the same
  bidi ordering the extractor already applies (#632) instead of a second bidi
  pass. The on-screen highlight still follows visual order, so each glyph
  rectangle — including within an RTL run — is drawn where the user sees it. A
  bounded multi-column improvement also keeps a column-local drag from vacuuming
  up an adjacent column that shares a Y-band; full multi-column and CJK
  selection correctness remain deferred (#774).
- **Visible signature appearance** (#623, last remaining bullet) — a signed
  signature field whose widget `/Rect` has non-zero area now gets a baked
  `/AP /N` appearance stream (`SignatureAppearanceAuthoring` in
  `Excise.Core`, mirroring the annotation-authoring baked-appearance pattern
  from #626): a bordered box with "Digitally signed by {name}", the signing
  date, and any `/Reason`/`/Location` the caller supplied. `SignFile`/
  `SignDocument` wire this in automatically after the field is signed, and it
  touches only the widget's appearance — the CMS/ByteRange machinery is
  unchanged. A zero-size (or absent) `/Rect` — the default for a
  freshly-authored field — stays untouched: invisible signatures remain
  fully valid, as before. Verified with an independent renderer (mutool) so
  excise is not its own oracle for whether a third-party viewer actually
  draws the appearance.
- **Tagged-PDF structure accessibility layer** (#631) — the Avalonia viewer's
  automation peer tree now exposes the tagged-PDF structure tree to screen
  readers: the page's accessible text is ordered by the structure tree when a
  tagged document supplies orderable `/ActualText` (falling back to geometric
  reading order otherwise), structure elements are surfaced as role peers
  (headings H1–H6, lists and list items, tables/rows/cells; figures continue
  to come through as `/Alt` image peers), and `H` / `Shift+H` navigate to the
  next/previous heading across page boundaries. Reading a heading's body glyphs
  in struct order still awaits MCID-to-letter mapping from Excise.Core (a
  follow-up slice of #631); untagged reading-order heuristics (#773) and
  PDF/UA conformance validation (#772) are out of scope.
- **Remaining #626 annotation subtypes: markup, shapes, stamps, edit/reply**
  (#626) — `PdfAnnotationAuthoring` now covers the rest of ISO 32000-2
  §12.5.6's programmatic authoring surface, each with a baked, self-contained
  `/AP /N` appearance stream so third-party viewers render the same pixels
  (excise cannot be its own oracle for this — see below):
  - **Text markup**: `AddUnderlineAnnotation`, `AddStrikeOutAnnotation`,
    `AddSquigglyAnnotation` (§12.5.6.10) mirror `AddHighlightAnnotation`'s
    single-quad shape but bake a stroked line/zig-zag appearance, since
    (unlike Highlight) most viewers do not synthesize one for these subtypes.
  - **Line/Arrow/Polygon/PolyLine** (§12.5.6.7, §12.5.6.9): `AddLineAnnotation`
    and `AddArrowAnnotation` write `/L` plus `/LE` line-endings (`None`,
    `OpenArrow`, `ClosedArrow`) with a matching baked triangular arrowhead;
    `AddPolygonAnnotation` (closed, optional `/IC` fill) and
    `AddPolyLineAnnotation` (always open, stroke-only) write `/Vertices`.
  - **Stamp** (§12.5.6.12): `AddStampAnnotation` renders one of the 15
    standard rubber-stamp names (`PdfAnnotationAuthoring.StandardStampNames`)
    as a bordered, colored, bold-labeled box — excise has no bundled Acrobat
    icon artwork, so this trades exact icon fidelity for guaranteed
    cross-viewer pixel identity. `AddImageStampAnnotation` embeds a
    caller-supplied raw RGB24 image as an uncompressed DeviceRGB Image
    XObject for a custom/logo stamp, with no dependency on a JPEG/PNG codec.
  - **Edit and delete**: `SetAnnotationContents`, `SetAnnotationColor`,
    `SetAnnotationOpacity` mutate `/Contents`, `/C`, `/CA` on an existing
    annotation in place (refreshing `/M`); `RemoveAnnotation` detaches an
    annotation from a page's `/Annots` array.
  - **Reply threads** (§12.5.6.2): `SetReplyTo` sets `/IRT` (an indirect
    reference to the parent annotation) and `/RT` (`R` or `Group`).
  - `XfdfSerializer`/`FdfSerializer` gained a `stamp`/`Stamp` import case
    (previously exported but not re-importable) and now carry `/IC` through
    generic Polygon imports (a pre-existing round-trip gap this work
    surfaced); every new subtype round-trips position, color, `/T` author,
    `/Contents` and `/CA` opacity through both formats.
  - Verified with an independent-renderer gate, not excise reading its own
    output: mutool and pdftocairo render every new appearance stream
    (ink-region assertions on the saved file), and a third test confirms
    excise's own `SkiaRenderer` agrees with mutool on the same pixels
    (`Excise.Rendering.Tests/Differential/RemainingAnnotationSubtypesDifferentialTests.cs`).
- **FDF annotation import/export round-trip** (#626) — `Excise.Core.Forms.FdfSerializer`
  reads and writes the PDF-syntax `/FDF` annotation interchange format (the
  counterpart to XFDF), so annotations round-trip with tools that prefer FDF.
- **DeviceCMYK overprint rendering** (#634) — the renderer now honours `/OP`,
  `/op`, and `/OPM` overprint state for DeviceCMYK fills and strokes (ISO
  32000-1 §8.6.7): with overprint on, a zero colorant no longer knocks out the
  underlying separation. Conservatively scoped to literal DeviceCMYK
  (Separation/DeviceN tracked under #634); verified against Ghostscript's
  `-dOverprint=/simulate` oracle on the Ghent GWG overprint fixtures, with the
  OPM-0 trap patch confirmed unchanged (no over-application).
- **Screen readers announce tagged-PDF `/ActualText`** (#631) — replacement
  text (`/ActualText`, ISO 32000-2 §14.9.4) is exposed to assistive technology
  through the viewer's automation tree, so hyphenation rejoins, ligature/symbol
  substitutions, and pages where glyph extraction fails are read correctly.
  De-duplicated against the page-text so content is never announced twice.
- **XFDF annotation import/export round-trip** (#626, final headline slice) —
  `Excise.Core.Forms.XfdfSerializer` speaks Adobe XFDF 3.0, the interchange
  dialect behind Acrobat/Foxit "Export comments as data file" review
  workflows. `ExportAnnotations` serializes every markup/geometry subtype the
  reader surfaces (text, freetext, line, square, circle, polygon, polyline,
  highlight, underline, squiggly, strikeout, stamp, caret, ink, watermark,
  redact) with the spec's attributes — page, rect, `#RRGGBB` color,
  interior-color, flags, name/title/subject, PDF-format dates, opacity,
  border width/style/dashes — and subtype geometry (raw 8-number `coords`
  quads, line `start`/`end`, `vertices`, `inklist`/`gesture` strokes,
  freetext `justification` + `defaultappearance`). `ImportAnnotations` adds
  the described annotations to a document: subtypes with an authoring method
  are created through `PdfAnnotationAuthoring` so they carry baked `/AP`
  appearance streams; text-markup/line/polygon/polyline/caret become
  spec-correct dictionaries; XFDF identity (name, dates, flags, subject,
  opacity) overrides authoring defaults so a round-trip preserves it, and
  unimportable elements are reported in `XfdfImportResult.Skipped` rather
  than failing the import. Round-trip is the proof: authored annotations
  export → re-import into a fresh document and match on subtype, rect,
  geometry, color, contents, author and `/NM`; the interop gate parses a
  spec-derived Acrobat-dialect fixture (multi-quad `coords`,
  `contents-richtext`, `f`/`ids` elements, timezone dates) — not
  excise-as-oracle. Widget form data (`<fields>`) and the PDF-syntax FDF
  container remain out of scope.
- **Apply self-signed PDF signatures — PKCS#7/CMS detached** (#623, first
  slice) — `SignatureApplicationService` signs a document with a self-signed
  or locally-held certificate: `/Sig` dictionary (`/Filter /Adobe.PPKLite`,
  `/SubFilter /adbe.pkcs7.detached`), correct two-pass `/ByteRange` (a
  fixed-capacity zero-filled `/Contents` hex hole plus a fixed-width
  ByteRange placeholder patched in place after serialization, so no byte
  offset shifts), and a BouncyCastle detached CMS SignedData backfilled into
  the hole. `SigningCertificateFactory` generates an in-process self-signed
  RSA-2048 identity or loads a PKCS#12 from disk — no CA account, no paid
  service, no network, per the issue's deliberate constraint. Signing an
  already-authored empty signature field is supported; signing a document
  that already carries a signature is refused (excise saves are full
  rewrites, which would silently invalidate it). Round-trip proven against
  the independent #466 verifier (valid + byte-exact ByteRange coverage,
  tamper ⇒ Invalid, self-signed ⇒ ValidUntrusted, pinned anchor ⇒
  ValidTrusted) and against poppler `pdfsig` as an out-of-repo oracle
  ("Signature is Valid / Total document signed / Certificate issuer is
  unknown"). Still open on #623: visible signature appearance, GUI/CLI
  surface, and multi-signature incremental-update saves.
- **Ink (freehand) annotation authoring** (#626) — `AddInkAnnotation` writes
  ISO 32000-2 §12.5.6.13 ink annotations from one or more polylines:
  `/InkList` (one inner array of x/y pairs per stroke), `/Rect` (the bounding
  box of every point, padded by half the pen width), stroke color (`/C`) and
  pen width (`/BS`) — plus a baked, self-contained `/AP /N` appearance stream
  that strokes each polyline with round caps and joins, so the drawing
  renders identically in excise, Acrobat, mutool, and pdftocairo (verified by
  independent-renderer differential tests). Surfaced in the app as
  `AnnotationWorkflowService.AddInk`.
- **FreeText annotation authoring** (#626) — `AddFreeTextAnnotation` writes
  ISO 32000-2 §12.5.6.6 text-box annotations: `/Contents`, a `/DA` default
  appearance string (color + base-14 Helvetica + size), `/Q` quadding
  (left/center/right via the new `PdfFreeTextQuadding` enum), optional border
  (`/BS`) and background fill (`/C`) — plus a baked, self-contained `/AP /N`
  appearance stream that draws the text (word-wrapped with real Helvetica
  advance widths, quadding-aware) so the box renders identically in excise,
  Acrobat, mutool, and pdftocairo (verified by independent-renderer
  differential tests). Surfaced in the app as
  `AnnotationWorkflowService.AddFreeText`.
- **Signature trust-chain validation and consolidated result states** (#466) —
  signature verification now evaluates the signer certificate chain (OS trust
  store by default; an explicit trust-anchor policy is injectable) in addition
  to the existing ByteRange/CMS checks, and reports a consolidated state:
  valid+trusted, valid-but-untrusted, invalid (modified after signing / broken
  signature), or indeterminate (could not verify). Trust is additional to
  cryptographic validity, never a replacement: the "trusted" state is
  unreachable unless the signature is also cryptographically valid over the
  correct byte range, and the summary text can only claim a trusted signer in
  that state. Signing time is now extracted from the CMS signed attributes.
  Certificate revocation (CRL/OCSP) remains deliberately unchecked (offline by
  design) and is stated in the summary.
- **Square and Circle annotation authoring** (#626, first slice of #271) —
  `AddSquareAnnotation` / `AddCircleAnnotation` write ISO 32000-2 §12.5.6.8
  shape annotations with border color (`/C`), optional interior fill (`/IC`),
  border width (`/BS`), and — unlike the earlier sticky-note/highlight
  authoring — a baked normal appearance stream (`/AP /N`), so the authored
  shape renders identically in excise, Acrobat, mutool, and pdftocairo
  (verified by independent-renderer differential tests).
- **Complete predefined CJK CMap coverage — the full PDF 32000 Table 118 set**
  (#515) — 50 more registered encoding CMaps ship embedded (Adobe
  cmap-resources, BSD-3), covering every predefined name a conforming reader
  must support: the legacy national encodings (GBK-EUC, GB-EUC, GBpc-EUC,
  GBK2K, Big5 `B5pc`/`ETen`/`ETenms`/`HKscs`, CNS-EUC, EUC-JP, the RKSJ
  Shift-JIS family, ISO-2022 `H`/`V`, KSC-EUC, KSCms-UHC, KSCpc-EUC) and the
  PDF 1.5+ `Uni*-UTF16` encodings including Adobe-KR's `UniAKR-UTF16-H`
  (ISO 32000-2). Previously only the `Uni*-UCS2` and `90ms-RKSJ` CMaps
  shipped; a Type0 font using any other predefined name fell through to the
  2-byte identity fallback — bytes misread as CIDs, extraction garbled, and
  `RedactText` **silently unable to match** the text (extraction coverage
  bounds redaction, CLAUDE.md limitation #1). UTF-16 surrogate-pair
  codespaces decode as single 4-byte codes and re-encode byte-exactly through
  redaction, so plane-2 CJK survives a round trip. Vertical writing is now
  detected from each CMap's own parsed `/WMode` rather than the `-V` name
  suffix, which the one-letter vertical CMap `V` does not carry.
- **Type 3 d0/d1 glyph metrics and d1 bounding-box clipping** (#514) — the
  renderer now honors the metrics a Type 3 CharProc declares: when the font
  has no `/Widths` entry covering a code, the advance falls back to the `wx`
  operand of the glyph's leading `d0`/`d1` operator (`/Widths` still overrides
  an inconsistent `wx`, per §9.6.5); glyph-space advances map through
  `/FontMatrix` as a displacement vector, so rotated matrices no longer drift
  glyphs apart by a bogus 1/1000 scale; and the glyph bounding box declared by
  `d1` clips the glyph description (an all-zero box declares no bounds).
  A stray `d0`/`d1` in an ordinary content stream is now ignored instead of
  colour-locking the rest of the page. Corroborated against live pdftocairo
  and Ghostscript renders of generated fixtures.
- **Vertical writing mode for Type0/CID fonts** (#515) — the `/W2` and `/DW2`
  vertical metric tables (PDF §9.7.4.3) are now parsed and honored across the
  extractor, the redaction content-stream parser, and the renderer. Vertical
  (`/Identity-V`, registered `-V` CMaps, or embedded CMap streams declaring
  `/WMode 1`) text now advances DOWN the page by the per-CID vertical
  displacement (previously: up, by the horizontal width), glyphs are placed
  via the `/W2` position vector (default centered, `v = (w0/2, 880)`), TJ
  adjustments move the vertical coordinate, and `Tz` horizontal scaling no
  longer applies vertically. Letter bounding boxes follow, so area redaction
  and `RedactText` target the correct region on vertical text (verified by a
  vertical redaction round-trip test with carrier-agnostic saved-bytes
  assertions, plus live pdftocairo/Ghostscript render differentials).

### Fixed
- **Word spacing (`Tw`) no longer fires on 2-byte character code `<0020>`**
  in CID fonts — per §9.3.3 it applies only to the single-byte code 32
  (e.g. 90ms-RKSJ's 1-byte space still gets it).
- **CID width tables are parsed by one shared, hardened parser**
  (`CidFontWidths`) instead of three divergent `/W` walks: indirect
  references are resolved at every level, junk tokens are skipped, reversed
  ranges are dropped, and hostile ranges like `[0 999999999 500]` are clamped
  to the valid CID space instead of allocating billions of entries.

## [3.2.1] - 2026-07-24

A redaction-trust release. Search and redaction now match a word regardless of
how the PDF happens to store it — several of these were **silent redaction
failures** (the tool reported success and left the word in the file). CJK text
now extracts *and* renders, and page content is exposed to screen readers.

### Fixed — silent redaction failures (search/redaction now matches however text is stored)
- **Right-to-left text is matched in logical order** (#632) — Arabic/Hebrew
  stored in visual order (the common single-`Tj` encoding) extracted reversed,
  so `RedactText` matched 0 and reported success. Now reversed at the source
  (`BidiReorderer`) for `page.Text`, search, and redaction.
- **Arabic presentation forms fold to base letters for matching** (#632) — a
  base-letter search now matches text stored as shaped forms / lam-alef
  ligatures (U+FB50–FDFF, U+FE70–FEFF).
- **Latin ligatures fold for matching** (#722) — a search for "office"/"final"
  now matches text stored with `ﬃ`/`ﬁ` (U+FB00–FB06).
- **Canonical (NFC) accents match** (#724) — precomposed "café" and decomposed
  `cafe`+U+0301 now match.
- **Arabic harakat / Hebrew niqqud are matched insensitively** (#725) — a
  bare-letter needle finds vocalized/pointed text.
- **Invisible separators no longer break matching** (#726) — soft hyphen
  (U+00AD), zero-width characters, and non-breaking spaces.
- **Fullwidth ↔ halfwidth forms match** (#727) — a keyboard "ABC"/"123" finds
  `ＡＢＣ`/`１２３` and halfwidth katakana.
  (All folds are matching-only via `MatchingNormalization`; extraction stays
  raw so glyph-level removal still targets the original glyphs.)

### Added
- **Text extraction for Type0 CJK / CID fonts** (#715, #515) — `/ToUnicode`
  `/Identity-H|V` (name form), non-embedded Identity-H CID fonts via the
  standard Macintosh glyph order (#532), and embedded fonts via reverse-cmap
  GID→Unicode now decode correctly instead of garbling (which previously made
  `RedactText` silently fail on CJK).
- **Registered (predefined) CJK CMap support in text extraction and
  redaction** (#515 slice 2; the CJK half of #715) — a Type0 font whose
  `/Encoding` is a registered CMap NAME (`/UniGB-UCS2-H`, `/UniCNS-UCS2-H`,
  `/UniJIS-UCS2-H`, `/UniKS-UCS2-H`, `/90ms-RKSJ-H`, and their vertical `-V`
  variants) now decodes code→CID through the actual Adobe CMap data, and —
  when there is no embedded `/ToUnicode` — CID→Unicode through the
  `Adobe-<Ordering>-UCS2` CMap selected from the descendant's
  `/CIDSystemInfo` (PDF §9.10.2 method (b)). The ordering path also fires
  for `Identity-H/V` fonts whose CIDSystemInfo names a known ordering, and
  for a registered-CMap-name `/ToUnicode` (#715). Mixed 1/2-byte codespaces
  (Shift-JIS) segment per the CMap's codespace ranges instead of a fixed
  2-byte stride, and `/W` width lookups are now CID-keyed under these
  encodings. Previously such text extracted as garbage, which made
  `RedactText` silently fail on it (CLAUDE.md limitation #1). The CMap data
  (15 files, ~750KB gzipped) is embedded from Adobe's cmap-resources /
  mapping-resources-pdf repositories (BSD-3-Clause, see
  `Excise.Core/Resources/CMaps/LICENSE.md`).

### Fixed
- **Renderer now selects glyphs through registered CMap names** (#515
  renderer slice) — `SkiaRenderer`'s Type0 path only honored an embedded
  `/Encoding` CMap *stream*; a registered CMap NAME fell through to
  identity decoding, so 2-byte character codes were misread as CIDs and
  CJK pages rendered as .notdef tofu even though extraction (above) read
  them fine. Glyph selection now loads the same predefined Adobe CMap for
  code→CID; unknown names keep the identity fallback, and Identity-H/V and
  embedded-stream behavior is unchanged. Verified against pdftocairo and
  Ghostscript (`RegisteredCMapRenderingTests`, 2%-differing-pixel gate).
- **Content-stream parser no longer mangles multi-byte text operands**
  (#515) — `ContentStreamParser` round-tripped `Tj`/`TJ` string operands
  through `PdfString.Value`'s document-string decode heuristics and Latin-1,
  clamping any byte the heuristic mapped above U+00FF to `?`. It now reads
  the raw font-encoded bytes. This is what let glyph-level redaction match
  CJK operator text against extracted letters instead of degrading to
  whole-operator removal.
- **Text no longer renders heavier than reference renderers** (#710, root
  cause of #584) — fill-mode text was rasterized through `SKCanvas.DrawText`,
  whose glyph masks come from the platform scaler (CoreText on macOS, hinted
  FreeType on Linux, DirectWrite on Windows); on macOS that added ~+0.45px of
  width to every stem at body-text sizes (~17% more ink on an identical
  embedded Type 1C outline vs mutool/pdftocairo). Text fills now draw the
  glyph outline path through Skia's own analytic scan converter — exact area
  coverage, platform-independent, within ~0.1% ink of mutool on the same
  outline — with a DrawText fallback for bitmap-only faces (color emoji).
  Gated by `TextRasterInkParityTests` against mutool on an embedded-CFF
  fixture.
- **Raw Type 1 (`/FontFile`) text keeps the platform glyph-mask fill**
  (#710 regression fix) — the outline-path fill above made embedded raw
  Type 1 faces render *worse* against the references (highlights.pdf p5,
  URW Nimbus Roman: DifferingPixelFraction vs mutool 0.00067 → 0.0152),
  because the platform's raw-Type1 raster path never applied the CFF
  stem darkening the outline fill was fixing — DrawText already matched
  mutool almost exactly there. Outline fill is now scoped to embedded
  CFF/Type1C, TrueType, OpenType, and system-substituted faces
  (`ResolvedRenderFont.HasRawType1Program` gate in
  `FillTextUsingGlyphPath`); both directions are test-gated
  (`PdfJsFontFallbackDifferentialTests` + `TextRasterInkParityTests`).
- **Type 3 uncolored-glyph (d1) colour semantics** (#514) — a d1 CharProc's
  own colour operators are now ignored so the glyph paints in the text
  object's fill colour, per ISO 32000-1 §9.6.5.

### Added
- **PDF page text is exposed to the platform accessibility tree** (#631,
  first slice) — a screen reader entering the viewer now reaches the current
  page's text in reading order (via a `PdfViewerAutomationPeer`), updating on
  page navigation and content changes. Struct-tree reading order and PDF/UA
  validation remain follow-ups.
- **Direct per-font-class rendering test matrix** (#512) — embedded
  TrueType/CFF/OpenType, base-14, encoding, render-mode, and Type0/CID paths
  are now covered by focused render tests independent of complex corpus PDFs.

## [3.2.0] - 2026-07-22

A stabilization release: viewport continuity is preserved across zoom and
view-mode changes, the Native AOT release lane publishes with zero warnings,
and saved editing output (typewriter, forms) is now verified against
independent reference renderers.

### Fixed
- **Continuous viewport anchoring across zoom** (#700) — zooming in the
  continuous viewer now keeps the page under the viewport centre in place and
  the page-number label live, instead of jumping to the top and freezing the
  label. The current page and intra-page fraction are captured before the
  re-layout and restored through a permanent scroll-extent subscription.
- **View-mode switch visual continuity** (#693) — switching between
  single-page and continuous modes carries the reading position over and uses
  a unified display scale, so text no longer jumps or changes size across the
  switch.
- **Duplicate class-handler registration** (#700) — viewer input class handlers
  were registered in the instance constructor, so every constructed viewer
  added another static handler (N-plicated event dispatch, a source of
  UI-test flakiness). Moved to the static constructor.
- **Packaged/AOT startup crash** (#593) — `FluentAvaloniaTheme`'s compiled-XAML
  constructor hard-references the DataGrid themes at startup; an over-eager
  assembly trim broke app launch in the published bundle. DataGrid is restored
  and guarded by `AppThemeCanaryTests` asserting on the app's build output.

### Changed
- **Native AOT publishes with zero warnings** (#593) — `ReactiveUI.Avalonia`
  was dropped (the main-thread scheduler is vendored as
  `RxSchedulers.MainThreadScheduler`; `RxApp` is gone in ReactiveUI 23), and
  third-party AOT/IL warning roll-ups were eliminated at the source. The lone
  survivor (CSJ2K `IL2104`, a terminal cctor assembly-scan) is narrowly
  suppressed; unsuppressed AOT publish now reports 0 warnings.
- **AOT support matrix documented** (#595) — `osx-arm64` is shipped and
  validated by the AOT CI lane and `run-aot-smoke.sh`; `win-x64`, `linux-x64`,
  and `osx-x64` are explicitly deferred with probe issues. Release notes and
  docs must not claim AOT targets beyond this table.

### Added
- **Editing-output fidelity gates** (#610, #611) — typewriter and interactive
  form saves are now verified against mutool/Ghostscript reference renders, so
  regressions in what excise writes are caught by independent tools. Interactive
  form saves are documented as `/NeedAppearances`-dependent (excise generates no
  appearance stream on fill). Closes epic #605.

### CI / Infrastructure
- **Windows veraPDF install fixed** (#666) — it had never actually run (a
  pwsh-wrapped bash string expanded `$(find …)` as its own subexpression behind
  `continue-on-error`); veraPDF setup failures are now explicit on all three OS
  jobs, and veraPDF prints its version on Windows for the first time.
- **Branch model** — `develop` is the default branch where work lands; `main`
  is a stable release pointer that only ever advances to release tags.
- Removed the never-configured Gemini workflows that reported red on every PR
  (#699).

## [3.1.0] - 2026-07-20

A display-correctness release: text is now crisp on HiDPI displays and at any
zoom, and two live-reproduced selection/zoom display bugs are root-caused and
fixed with pixel-level regression batteries.

### Fixed
- **Crisp text on HiDPI and when zoomed** (#682, #683) — both the continuous
  and single-page viewers render at device-pixel resolution (zoom ×
  device-pixel-ratio), re-rendering as you zoom instead of upscaling a 96-DPI
  raster. Layout sizes, coordinates, and overlays are unchanged.
- **Selection highlights no longer drift left** (#693) — overlay canvases are
  pinned to the page image's origin; at narrow zooms the highlight previously
  landed up to ~400 dips left of the selected text.
- **Fit-after-selection display corruption** (#697) — pressing Fit in
  select-text mode at HiDPI showed ~2× oversized text over blank space with
  seemingly orphaned highlights. Root cause: Avalonia's `Image` mispaints any
  bitmap stamped at a DPI other than 96 as a magnified top-left pixel crop
  (pinned by `DpiStampedBitmapPaintProbeTests`). Single-page bitmaps are now
  always 96-stamped, with the logical layout size carried explicitly.
- **Quick-win batch** (#675, #674, #668, #665) — in-page links no longer
  dispatch double click events; a flaky AES round-trip test stabilized;
  skip-budget comment hygiene.

### Added
- **Thumbnail viewport window** (#687–#690) — thumbnails are evicted,
  prefetched, and pre-warmed around the visible window with a disk-cache trim,
  keeping the sidebar responsive on large documents.
- **Page-assembly permission enforcement on CLI merge/split** (#677) —
  `/P` bit 11 now gates `excise merge`/`excise split`
  (`DocumentAction.AssembleDocument`).
- **Native AOT release lane for Excise.App** (#590), validated on osx-arm64.
- Deterministic SVG→raster icon generation script (#679).

### Tests / infrastructure
- Mode-switch display invariants across modes and device pixel ratios,
  pixel-level displayed-text verification for mode buttons, and a
  fit-after-selection live-repro battery (red-checked at dpr 2).
- Project-authored test-data drift gate and the skip-budget self-test wired
  into tier t0 (#678).
- `EXCISE_TRACE_VIEWER=1` viewer-state probes (ViewMode/render plans/overlay
  origins) used for the live #697 diagnosis.

## [3.0.0] - 2026-07-18

**The project is renamed from `pdfe` to `Excise`.** Same engine, same
philosophy (a tool must not be its own oracle for the property it exists to
guarantee) — new name, chosen to say what the product does: content isn't
covered, it's *excised*, and the removal is proven against independent tools.

This is the first tagged release since 2.28.0, so it also carries the changes
documented in the (never-tagged) **[2.29.0]** (test-integrity gates) and
**[2.30.0]** (the AES-256/AES-128 encryption epic, #624/#639–#644, and the
Make Searchable GUI) sections below.

### Changed — BREAKING (why this is a major version)
- **CLI command `pdfe` → `excise`.** `excise redact in.pdf out.pdf "secret"`,
  `excise info`, `excise render`, … — same commands, same flags.
- **Library namespaces / assemblies / NuGet ids `Pdfe.*` → `Excise.*`**:
  `Excise.Core`, `Excise.Rendering`, `Excise.Avalonia`, `Excise.Ocr`,
  `Excise.Cli`; the desktop app is `Excise.App`. Any code referencing the
  old `Pdfe.*` types must update its `using` directives and package references.
- **App identity**: window title, macOS bundle (`cl.skpt.excise`), and Linux
  desktop id updated to Excise. Internal `PDFE_*` environment toggles are now
  `EXCISE_*`.
- **Repository** renamed `github.com/marctjones/pdfe` → `.../excise`
  (old URLs redirect).

### Added
- **New document-first app icon.** A PDF page with a cleanly *excised* line —
  a see-through slot where text was, not a black bar hiding it — expressing the
  product in one mark. Vector master plus a regenerated 16–256px raster set.
- **First-class in-page links in continuous view** (#667) — click-to-follow and
  hover affordance for internal/GoTo and URI link annotations while scrolling.

### Notes
- No engine behavior changed in the rename; the redaction, encryption, and
  rendering pipelines are byte-for-byte the 2.30.0 code under new names.
- The two blocking roadmap tracks — **Redaction Trust** and **Document
  Security** — are complete and closed. Remaining work (fonts, performance/AOT,
  interop, editing fidelity) is enhancement, tracked in the named-track
  milestones.

## [2.30.0] - 2026-07-17

The encryption release: excise now WRITES password-protected PDFs — the full
#624 epic (#639–#644) landed and closed in one pass, every piece verified
against independent readers (qpdf, mutool, Ghostscript, pdftoppm), never
against excise itself. Alongside it: a sweep of redaction-trust extraction
fixes that emptied the #651 adversarial-corpus allowlist, the "Make
Searchable" OCR feature reached the GUI, and a rendering positioning bug
that had been misattributed to font scaling for months was root-caused and
fixed.

Note: the `[2.29.0]` section below was documented on 2026-07-13 but never
tagged — v2.30.0 is the first tagged release containing those changes too.

### Security
- **Empty owner password no longer grants passwordless full authority**
  (found and fixed pre-release, during #644 verification — no released
  build was ever affected). AES-256/R6 files written with a user password
  but no owner password derived `/O`/`/OE` from the empty owner password,
  which qpdf, Ghostscript, and pdftoppm all accepted as the full-authority
  owner password with NO password supplied — silently bypassing the user
  password. `CreateR6` now falls back to the user password as the owner
  password, exactly as R4's Algorithm 3 always did; the interop gate,
  the core writer suite, and a rebuilt-binary falsifiability drill all pin
  the user-password-only configuration for both algorithms.

### Added
- **Encryption writer: AES-256 (V5 R6, PDF 2.0 native) and AES-128
  (V4 R4, CFM=AESV2)** (#639, #640, part of the #624 encryption epic).
  `new PdfDocumentWriter(document, new PdfEncryptionOptions { ... })`
  emits a spec-correct `/Encrypt` dictionary (Algorithms 8/9/10 for R6;
  Algorithms 3/5 plus per-object Algorithm 1 key derivation for R4), with
  fresh random-IV AES-CBC per stream/string and correct exemptions for the
  `/Encrypt` dictionary itself, the trailer `/ID`, and (when
  `EncryptMetadata=false`) the XMP metadata stream. Building R4 exposed and
  fixed a real ordering bug: the trailer `/ID` was generated AFTER key
  derivation, which would have silently produced undecryptable R4 files.
  Verified per algorithm against qpdf (structure, permissions, both
  passwords, `--decrypt` round-trip), mutool (content extraction), and
  Ghostscript (pixel-identical renders) — including a reverse-direction
  oracle where excise independently decrypts a file qpdf itself encrypted.
- **Password management: Document > Security dialog and `excise encrypt` /
  `excise decrypt`** (#641). Set a user (open) and/or owner (permissions)
  password, choose AES-256 (default) or AES-128, change a password (gated
  on re-entering the current one), or remove protection — removal is a
  distinct, confirmation-gated action, so clearing the password fields and
  clicking Apply on an encrypted document can never silently strip
  protection. Change-password on the CLI is the documented two-step
  `excise decrypt` → `excise encrypt`.
- **Multi-reader encryption interop gate** (#644). 37 assertions covering
  both algorithms × four independent tools (mutool, qpdf, Ghostscript, and
  a new pdftoppm oracle) × correct/owner/wrong/absent password, plus
  semantic `/P` verification via qpdf and an anti-vacuity guard
  (`EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1` makes an all-tools-missing run
  a hard failure). Wired into tier T1 and `docs/RELEASE_CHECKLIST.md` as
  the release's encryption evidence, with Adobe Acrobat as a documented
  manual step. Falsifiability-drilled: ignoring the `/Encrypt` dictionary
  flips 28 of 33 original assertions red.
- **Make Searchable in the GUI** (#658, completing #627). Tools > Make
  Searchable OCRs pages without a text layer and writes the recognized
  words back as an invisible, searchable text layer — with language
  selection, progress, cancellation, and a result summary. The #627 engine
  (`PdfSearchableConverter`) and `excise make-searchable` CLI shipped
  earlier in this cycle; redaction of a made-searchable scan removes both
  the invisible text and the raster ink (verified via independent
  extractor + ink differential).
- **Encryption is preserved across redact/edit/save round-trips** (#643, part
  of the #624 encryption epic). A document opened encrypted now SAVES
  encrypted by default on every mutating path — GUI save/save-as, redacted
  copy, flattened-form copy, scripting, CLI `redact` / `fill-form` /
  `add-field` / `autodetect-fields --apply` / `make-searchable`, and batch
  `redaction.apply` — with the same algorithm, the same `/P` permission mask,
  the same `/EncryptMetadata` choice, and the same password it was opened
  with. Core API: `PdfDocument.GetReEncryptionOptions(password)` plus
  explicit `Save`/`SaveToBytes` overloads taking `PdfEncryptionOptions?`
  (the parameterless `Save()` still writes plaintext so nothing re-encrypts
  by surprise). RC4 sources (V1/V2, V4 CFM=V2) are re-encrypted **upgraded
  to AES-256** — never downgraded, never silently decrypted. `excise redact`
  gained `--password`; `--allow-decrypt` / batch `allowDecrypt: true`
  flipped meaning from #638's "opt in to proceed at all" to the explicit
  opt-OUT that writes an unprotected copy, and the GUI's "Encryption Will Be
  Removed" confirmation is gone — dropping protection now happens only via
  the Security dialog's Remove Protection (#641). Verified with independent
  oracles (qpdf structure/permissions/decrypt, mutool extraction), including
  a ciphertext-aware redaction-leak scan over qpdf's decrypted,
  uncompressed serialization of the re-encrypted output.
- **Document permissions (`/P`) are surfaced and enforced** (#642, part of the
  #624 encryption epic). `PdfDocument.Permissions` /
  `EffectivePermissions` decode the ISO 32000-2 Table 22 bitmask
  (bit meanings verified against qpdf's `--show-encryption`). Enforcement is
  at the action layer: GUI copy, text-selection copy, and page-image export
  refuse (with a visible toast) on copy-forbidden documents; typewriter and
  form authoring require the modify permission, annotations the annotate
  permission, and form fill the fill-forms permission. The CLI gates
  `text`/`letters`/`render`/`ocr` (copy/extract), `fill-form`,
  `add-field`/`autodetect-fields --apply`, and the batch-automation steps,
  each failing closed with an explicit override (`--ignore-permissions` /
  `ignorePermissions: true` / scripting `IgnoreDocumentPermissions`) for
  document owners, since owner-password opening is not yet supported (#324).
  The bit 10 extract-for-accessibility carve-out is honoured
  (`--for-accessibility`; search, rendering, and the accessibility/automation
  tree are never permission-gated). Redaction is deliberately not gated:
  removing sensitive content from your own copy is excise's core purpose.

### Fixed
- **Redaction-trust extraction sweep — the #651 adversarial-corpus
  allowlist is now empty.** The #648 gate's original finding (11 pdf.js
  fixtures where excise catastrophically under-extracted vs. mutool) is fully
  resolved: off-page metadata pollution filtered by crop-box bounds (#649);
  Type0/CID fonts with 1-byte codespaces decode correctly, which also
  surfaced and fixed a raw-byte corruption in `ContentStreamWriter` and an
  incorrect CID-font inference in redaction reconstruction (#659); FreeText
  annotation content is extractable AND removable by `RedactText` (#660);
  list-box widgets emit their full `/Opt` option list (#661);
  `/Differences`-encoded simple fonts without `/ToUnicode` decode via the
  Adobe glyph list (#662); signature widget `/AP` appearance text is
  extracted and removable (#669); orphaned merged field/widgets outside
  `/AcroForm/Fields` are surfaced (#670); widgets without the optional `/P`
  key resolve their page via the page's own `/Annots` (#671); multiline
  field values are no longer truncated to one line (#672). Every fix
  verified against mutool, and every new extraction carrier proven
  REMOVABLE via saved-bytes redaction round-trips — findable-but-not-
  removable is a leak, not a feature.
- **Embedded-CFF text ignored character/word spacing when drawing** (#652).
  The glyph-run draw path applied `Tc`/`Tw` only to the tracked text
  cursor, not the drawn glyphs, so justified lines drifted until glyphs
  visually collided (the "em-dash strikethrough" report — the issue's
  FontMatrix hypothesis was refuted at the byte level). Page 36 of the
  local book fixture: 9.55% → 0.59% pixel diff vs. mutool.
- **Trust PDF `/Widths` over embedded-font `hmtx` for inter-glyph
  advance** (#584) and **ShadingType 5/7 wired into pattern-fill
  dispatch** (#633).
- **PDF string-literal line-continuation escape handled** (#637).
- **Continuous-view cache is byte-budgeted** (#615): the page cache is
  bounded by memory (200 MB) instead of a flat page count, so mixed-size
  documents can't blow past intended memory use.
- **Four flaky/incorrect UI tests root-caused** (#653): a view-mode
  default mismatch, not layout timing — and the investigation found link
  click/hover has no continuous-mode implementation at all (filed #667).
- Test-infrastructure hardening: skip-budget gates extended to every test
  project with real CI-log-verified allowlists (#655, #663, #664, #654);
  corpus-resilience and adversarial-extraction gates (#648) plus the
  corpus-wide extraction-parity floor gate (#645); test tiers T0–T3 with
  one entry point (#646); per-OS CI jobs (#647); Excise.Core coverage gate
  restored to 93% (#603); font-parser fuzzing closed a real hang and crash
  (#648).

### Changed
- **`--allow-decrypt` flipped meaning** with #643 (see Added): #638 had
  made "saving an encrypted document decrypts it" loud and fail-closed
  because excise could not write encryption; now that it can, preservation is
  the default and `--allow-decrypt` is the explicit plaintext opt-out. The
  #638-era `PdfWouldLoseEncryptionException` and batch
  `DECRYPT_CONFIRMATION_REQUIRED` error code are gone.
- Printing removed from the roadmap as an intentional decision (#621,
  #622).

## [2.29.0] - 2026-07-13

User-facing: continuous scroll is the default again, and "go to page N" now
actually goes there. Under the hood: the test suite can no longer lose coverage
silently, and a performance change can no longer quietly rewrite what a
correctness test considers correct.

### Fixed
- **Continuous mode swallowed programmatic navigation.** "Go to page N" — an
  outline click, the page-number box, a jump to a search hit — could be silently
  discarded and land the user on page 1. Three stacked defects: the scroll request
  was dropped when the page slots did not exist yet; the document-changed path
  wiped the pending-navigation latch; and the "did we arrive?" check treated an
  un-laid-out ScrollViewer (extent 0, so max offset 0) as *already arrived*, which
  disarmed the guard instantly and let the scroll handler snap back to page 1.

### Changed
- **Continuous scroll is the default view mode again**, now that the navigation
  race above is fixed. The preference is still remembered across sessions.

### Test integrity (#617, #618, #619, #620)
- **Coverage can no longer vanish silently.** `scripts/check-skip-budget.sh` fails
  the build when the set of skipped tests changes in either direction. Seeding it
  found **33 skipped tests in Excise.Core alone** — including rotation tests in code
  v2.28.0 had just touched. A security-relevant assertion (does hidden-text reveal
  avoid loading OCR?) had already stopped running unnoticed.
- **A perf change can no longer rewrite a correctness assertion quietly.**
  `scripts/check-gate-asymmetry.sh` (in CI) fails a change that touches a
  performance-sensitive path *and* rewrites a test's expected values, unless the
  commit says so explicitly. Validated against the commit that did exactly that.
- **The 144-page display sweep no longer fails on machine load.** It owns its
  deadline and reports what actually happened; `scripts/run-gui-display-sweep.sh`
  shards it (one shard of four: 1m24s, vs 5–20min). It had produced three false
  reds in a single day.
- **Geometry tests state invariants, not pinned numbers**, so they survive a legal
  optimization and still fail an illegal one. Mutation-tested against three real
  defects.
- **CLAUDE.md corrected**: it was pointing contributors at a redaction directory
  that does not exist, listing closed issues as current, and — worst — prescribing
  a redaction test assertion that is **blind** to three of the leaks fixed in 2.28.0.

## [2.28.0] - 2026-07-13

**Security release. Two redaction leaks are fixed. Upgrading is recommended for
anyone using excise to redact sensitive documents.**

Both fixed leaks share one root cause: redaction was verified by asking excise's
own text extractor whether excise had removed the text. That extractor reads the
content stream and nothing else, so text surviving in any other carrier was
reported as a clean redaction — by a fully green test suite.

### Security

- **Fixed: redacted text survived in the structure tree of tagged PDFs (#636).**
  `/ActualText` and `/Alt` restate the text of a marked-content span. Glyph
  removal rewrote the content stream and left them untouched, so Acrobat, screen
  readers, and any tag-aware extractor still read the redacted name straight out
  of the file. Tagged PDFs are exactly the institutional documents (government
  forms, court filings, medical records) most likely to hold sensitive data.
- **Fixed: redacted text survived in document-level carriers (#608).** The XMP
  `/Metadata` packet, outline (bookmark) titles, and annotation `/Contents` were
  never scrubbed — only `/Info` was, and only in the GUI. A redacted name left in
  a bookmark title is visible in the reader's navigation sidebar without the page
  ever being opened.
- **Verified (was only asserted in a comment): a full save garbage-collects the
  previous revision**, so an incremental-update PDF cannot retain the
  un-redacted page. Now proven by test rather than believed.
- **New: redaction is now verified by tools that are not excise** (#606, #607,
  #609) — independent extraction (mutool), independent rendering (Ghostscript)
  as a before/after ink differential, and the full corpus. Ink absence is the
  stronger claim: extraction cannot see text rendered as vector paths or raster
  pixels; a renderer can.

### Known security limitations (unchanged from 2.27.1 — not introduced here)

- **Redaction is silently incomplete where text extraction is blind (#637).**
  Where excise cannot read text, it cannot redact it, and it reports success
  anyway. Measured on `irs-1040-instructions.pdf` page 47: excise extracts 471
  characters, mutool extracts 3,192. **Verify redactions of unfamiliar documents
  with an independent tool.** This is pre-existing; it is disclosed here because
  the new independent-verification suite is what found it.
- **Redacting an encrypted PDF returns an unencrypted copy (#638).** The writer
  cannot emit `/Encrypt`. The redaction succeeds; the protection on the rest of
  the document is silently dropped.
- **`/P` permissions are parsed but never enforced (#642).**

### Added
- Continuous scroll can now be enabled from View > Continuous Scroll and the
  choice is remembered across sessions (`ContinuousScrollEnabled`). It is
  **opt-in**; making it the default is deferred to 2.29.0 (see Deferred below).
- `PdfDocumentSanitizer.ScrubTerms` (public API, additive) — removes redacted
  terms from `/Info`, XMP `/Metadata`, outline titles, and annotation `/Contents`.

### Deferred to 2.29.0
- **Continuous scroll as the default view mode.** Enabling it by default surfaced
  a pre-existing navigation race in the viewer: a programmatic "go to page N"
  (outline click, page-number box, search hit) issued before layout settles is
  swallowed by the scroll→page sync and silently lands on page 1. The preference
  machinery ships and works; only the default is off. Held back rather than delay
  the security fixes in this release. Tracked on `fix/continuous-nav-race` with
  failing regression tests that pin the contract.

### Changed
- Continuous-scroll page rendering now coalesces render passes and de-duplicates
  in-flight tile requests, so fast scrolling through large documents no longer
  queues and cancels a render for every intermediate scroll position. Tiles are
  quantized and rendered with overscan so nearby scroll offsets reuse one cache
  entry. Adds a `gui.render` benchmark workload covering visible-page settle time.

### Fixed
- View and Tools menu checkmarks (Show Outline, Show Thumbnails, Show Clipboard
  History, Continuous Scroll, Reveal Hidden Text, Reveal Rasterized Hidden Text)
  stayed permanently checked and did nothing when clicked. They bound `IsChecked`
  two-way with no `Command`, so a click never reached the ViewModel. They now
  mutate state through a ViewModel command with a one-way `IsChecked` binding,
  and the macOS native menu drives its check state from `PropertyChanged` instead
  of owning it.
- Leaving an editing mode (redaction, text selection, form authoring, typewriter)
  now restores the saved continuous-scroll preference. Previously these modes
  forced single-page view on entry and never restored it, stranding the session in
  single-page for the rest of its life.
- Suppressed the tooltip on the status-bar page arrows, whose popup made the small
  footer targets hard to click while the status bar was re-measuring.

## [2.27.1] - 2026-07-08

macOS bundle identity correction release. No intended public API break.

### Changed
- Changed the macOS app bundle identifier from `com.marcjones.excise` to
  `cl.skpt.excise` so LaunchServices, Finder/Open With, and packaged GUI smoke
  target the skpt-owned excise app identity.
- Updated packaged GUI smoke shutdown to address the new bundle identifier.

### Tests
- Release smoke passed for `2.27.1` with the quick, package, and packaged-GUI
  gates: `logs/release-smoke_20260708_021515`.

## [2.27.0] - 2026-07-08

GUI search responsiveness and release-gate hardening release. No intended
public API break.

### Changed
- **Search and indexing hot paths.** Reused the page letter cache for page text
  and word extraction, made document text-index builds single-flight, skipped
  annotation search work on pages without annotations, and removed per-match word
  list allocations from search result bounds calculation.
- **Background indexing responsiveness.** Delayed search-index startup after
  document open and page mutations so first-page interaction stays responsive,
  while keeping the index available for fast repeated searches.
- **Search result publication.** Batched search-match publication to the UI,
  deferred first-match navigation behind the result update, and recorded worker,
  UI queue, UI publish, and total search timings for hotspot reports.
- **Status-message accuracy.** Cleared `Opening PDF…` once the document is
  usable and hardened search cancellation/close paths so stale `Searching…`
  status and inline progress text do not remain visible.

### Added
- **Status-message regression audit.** Added UI tests that verify document-open
  and cleared-search status transitions remain accurate.
- **Icon resource regression audit.** Added a main-shell `PathIcon`
  `StaticResource` sweep so toolbar and menu icon references fail tests if an
  icon resource is missing.
- **Search subphase hotspot reporting.** Added `gui.search.worker`,
  `gui.search.ui-queue`, `gui.search.ui-publish`, and `gui.search.total` to the
  GUI workflow performance reports.

### Tests
- Stabilized headless GUI fixture checks and encrypted redaction fixture skips on
  machines where optional encrypted fixtures are unavailable.
- Aligned the core coverage gate with the current baseline so CI fails on real
  regressions instead of stale thresholds.

## [2.26.0] - 2026-07-07

Native AOT and GUI hot-path responsiveness release. Additive public API change
in `Excise.Avalonia`; no intended breaking change.

### Added
- **Native AOT release lane (#590-#595).** Added
  `scripts/run-aot-smoke.sh` and wired `scripts/release-smoke.sh --only=aot`
  so the GUI AOT build can be published, packaged, warning-audited, and
  optionally exercised with packaged GUI smoke evidence.
- **GUI hotspot regression reporting (#596, #601).** Added structured GUI
  workflow hotspot reports for document open, continuous scroll, page jumps,
  search, annotation, forms, redaction, save, and close workflows.
- **Full GUI responsiveness coverage (#601).** Added end-to-end responsiveness
  tests and catalog coverage for the long-document and broad workflow phases
  that should stay below human-visible interaction budgets.

### Changed
- **Viewer-owned display rendering (#601).** Shifted display rendering
  ownership into the viewer, cached rendered pages as bitmaps, and exposed the
  additive `PdfViewerControl.RenderVersion` API so hosts can explicitly
  invalidate viewer caches after visual document changes.
- **Continuous-view hot path (#601).** Cached continuous page layout positions
  and optimized visible-page lookup for long-document scrolling.

### Tests
- Regenerated the `Excise.Avalonia` public API approval baseline for the
  intentional `RenderVersion` addition.
- Redaction gates remain required for this release line:
  `dotnet test ... --filter "FullyQualifiedName~Redaction"`.

## [2.25.0] - 2026-07-04

Benchmarking and renderer-performance release. No intended public API break.

### Added
- **Benchmark suite (#344, #357).** Added `Excise.RenderTools benchmark-suite`
  and wired `scripts/run-benchmarks.sh` so one command emits
  `benchmark-report.json`, `benchmark-pages.csv`, and `benchmark-report.md`
  covering excise parse/text/render speed, external-reference fidelity,
  RMSE/SSIM metrics, tool availability, and subprocess-only license isolation.
- **Benchmark regression gate (#344, #357).** Added a release-smoke benchmark
  gate plus a deterministic CI gate that runs the benchmark suite in synthetic
  no-oracle mode and fails on excise parse/render/redaction regressions.
- **Redaction-completeness signal (#357).** The benchmark report now includes a
  synthetic glyph-level redaction check so speed reporting does not drift away
  from excise's security-critical differentiator.

### Changed
- **Benchmark wrapper (#344).** `scripts/run-benchmarks.sh` now runs the
  benchmark suite by default, keeps `corpus-hotspots` and
  `gui-display-hotspots`, and exposes `benchmarkdotnet` for the isolated
  `Excise.Benchmarks` microbenchmark project.
- **RenderTools exit codes (#344).** Utility commands now normalize handler
  `Environment.ExitCode` the same way the public CLI does, so failed benchmark
  gates return a non-zero process exit.

### Tests
- `BenchmarkSuiteTests` covers oracle parsing, report generation, license
  metadata, redaction-completeness reporting, and non-zero regression exits.
- Local reference smoke passed with MuPDF, Poppler, and Ghostscript available:
  `logs/benchmarks/v2.25-reference-smoke`.

## [2.24.0] - 2026-07-04

UX, icon, and visual-polish audit release. No intended public API break.

### Changed
- **Vector shell icons (#559).** Replaced the main menu, toolbar, and empty
  state emoji icon affordances with local vector `StreamGeometry` resources so
  the shell no longer depends on platform emoji fonts for core commands.
- **Toolbar layout (#559).** Reserved the right side of the toolbar for zoom
  controls and placed the main action strip in a horizontal scroll region. The
  default 1280px workflow screenshot now keeps zoom controls visible and avoids
  clipped toolbar labels by making secondary actions icon-only with explicit
  tooltips and accessibility names.

### Added
- **Screenshot-backed UX/icon audit (#559).** Added
  `VisualPolishAuditTests` and `scripts/run-ux-icon-audit.sh`, which capture
  headless screenshots for empty/open, document navigation/page organization,
  search, redaction, forms, typewriter/annotation, and preferences states and
  write `ux-icon-audit.json` plus a markdown report.
- **UX release gate (#559).** Added
  `scripts/release-smoke.sh --quick --only=ux` and release-checklist coverage
  so design-quality review stays separate from renderer/display parity.

### Tests
- v2.24 UX/icon audit passed:
  `logs/ux-icon-audit/v2.24-local` (`VisualPolishAuditTests`, screenshots, and
  manifest).
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.23.0] - 2026-07-04

Automation API and platform integration release. Additive public API change in
`Excise.Core.Automation`; no intended breaking change.

### Added
- **Stable CLI automation contract (#561).** Added `excise batch` for JSON
  workflows with structured final reports, optional report files, progress
  NDJSON on stderr, documented exit codes, relative-path resolution, and
  password-aware document open without writing passwords to reports.
- **JSON CLI output (#561).** Added `--json` output to `excise info`,
  `excise text`, and `excise render`, and added `--password` handling to
  `info` and `text` to match the render command.
- **Automation command metadata (#561).** Added `automation.batch` to the
  shared command registry and corrected hidden-text audit metadata to point at
  the existing `audit` CLI command.
- **Platform examples (#564, #567, #568, #574).** Added AppleScript,
  Shortcuts, PowerShell, Power Automate Desktop, and Linux/GNOME examples that
  call the CLI/batch JSON contract instead of clicking the GUI.
- **Automation release gate (#561, #574).** Added
  `scripts/run-automation-smoke.sh` and wired it into
  `scripts/release-smoke.sh --only=automation`.

### Security
- **Automation boundary (#565).** Documented the CLI-first threat model:
  no background GUI automation listener is enabled by default, Release builds
  still exclude Roslyn GUI scripting unless explicitly enabled, mutating batch
  commands require explicit output paths, in-place overwrite is refused, and
  redaction requires `confirmDestructive: true`.

### Tests
- Focused gates passed:
  `BatchAutomationCommandTests`, `CommandMetadataCommandTests`,
  `PdfCommandRegistryTests`, and `PublicApiApprovalTests`.
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.22.0] - 2026-07-04

Accessibility and assistive-technology readiness release. Additive public API
change in `Excise.Core.Automation`; no intended breaking change.

### Added
- **Shared semantic command metadata (#562).** Added `Excise.Core.Automation`
  with stable command IDs, labels, descriptions, shortcuts, CLI verbs,
  parameters, result fields, disabled reasons, and destructive/security flags.
- **CLI command metadata (#562).** Added `excise commands` and
  `excise commands <id> --json` so automation and batch workflows can query the
  same command model used by the GUI.
- **Accessibility command binding (#569).** Added the Avalonia
  `CommandAccessibility.CommandId` attached property, binding command metadata
  into accessible names, help text, unavailable status, and tooltips across the
  main menu, toolbar, search bar, page controls, redaction controls, and status
  surfaces.
- **Accessibility release gate (#570, #573).** Added
  `scripts/run-accessibility-smoke.sh` and wired it into
  `scripts/release-smoke.sh --only=accessibility`, producing a JSON report with
  automated check status and platform accessibility-tree probe status.
- **Accessibility checklist (#566, #570).** Added
  `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md` for macOS AX/VoiceOver, Windows UI
  Automation, and Linux/GNOME AT-SPI verification on dedicated runners.

### Changed
- **Keyboard-only and dialog semantics (#572).** Preferences, Save Redacted
  Version, About, and dynamically-created message/prompt dialogs now expose
  accessible names/help text plus default/cancel button semantics. The main
  status bar exposes current mode, operation status, and document status for
  assistive technology.
- **Release checklist.** Accessibility is now reported separately from GUI
  display parity and packaged-app smoke.

### Tests
- v2.22 accessibility smoke passed:
  `logs/release-smoke_20260704_135843` (`accessibility` gate PASS).
- Focused gates passed:
  `PdfCommandRegistryTests`, `CommandMetadataCommandTests`,
  `AccessibilityRegressionTests`, `GuiWorkflowCoverageMatrixTests`,
  `DocumentationClaimTests`, and `PublicApiApprovalTests`.
- Full Debug build passed: `dotnet build excise.sln -c Debug`.

## [2.21.0] - 2026-07-04

GUI responsiveness and packaged-app release-gate hardening release. No intended
API break.

### Added
- **GUI responsiveness reporting (#577, #581, #582).** The desktop app records
  open-to-first-page-visible timing, background phase ordering, render cache
  stats, and PASS/WARN/FAIL budget status in a JSON report that release smoke
  can consume.
- **Packaged app responsiveness smoke (#582).** `scripts/release-smoke.sh`
  now supports a packaged-GUI direct-exec mode that launches the built macOS
  app with a real PDF, captures app stdout/stderr, validates the first-page
  report, and avoids taking keyboard or mouse focus by default.
- **Interaction latency coverage (#578, #583).** Focused GUI tests cover direct
  input paths for search typing, text selection feedback, redaction preview,
  form authoring, and form edits, plus first-page-before-background-work
  ordering.

### Changed
- **Render scheduling and cache behavior (#575, #579).** Visible page renders
  cancel/drop stale work, adjacent-page prefetch is sequenced behind the visible
  page, lazy thumbnail placeholders avoid front-loading all thumbnail renders,
  and responsiveness reports include cache-hit/miss and cache-size signals.
- **macOS packaged smoke stability.** The packaged-GUI smoke now wakes the
  active display briefly before launching the app, avoiding Avalonia native
  render-timer startup failures when the laptop display is asleep. Avalonia
  packages were updated from 12.0.4 to 12.0.5.
- **Benchmark wrapper cleanup (#536).** `scripts/run-benchmarks.sh` now routes
  through the maintained render-tooling entry points so corpus hotspot reports
  can separate excise render cost from reference-render and comparison overhead.

### Tests
- Focused responsiveness and scheduling gate passed:
  `dotnet test Excise.App.Tests/Excise.App.Tests.csproj -c Debug --filter "FullyQualifiedName~GuiResponsivenessBudgetTests|FullyQualifiedName~MainWindowRenderSchedulingTests|FullyQualifiedName~PdfRenderServiceCacheTests|FullyQualifiedName~ResponsivenessReportTests|FullyQualifiedName~GuiWorkflowCoverageMatrixTests"`.
- Packaged release smoke passed:
  `logs/release-smoke_20260704_133123` (package and packaged-GUI direct-exec
  gate; app first-page visible in `108ms` on the generated six-page smoke PDF).
- Broader cross-library benchmark epics (#344, #357) remain open; this release
  ships the GUI responsiveness gate and hotspot aggregation cleanup, not the
  full future benchmarking system.

## [2.20.0] - 2026-07-04

GUI interaction and redaction hardening release. No intended API break.

### Added
- **Adversarial redaction regression coverage (#555).** Added generated tests
  for AcroForm values and appearances, annotations and appearance streams,
  partial glyph overlaps, rotated text, hidden optional-content layers,
  password-protected fixtures with documented passwords, incremental-update
  previous revisions, and OCR/scanned-image recovery cases.
- **Packaged GUI smoke evidence (#558, #571).** Added
  `scripts/run-packaged-gui-smoke.sh` and wired it into
  `scripts/release-smoke.sh --packaged-gui`, producing JSON/markdown reports,
  launch logs, and screenshot artifacts for the packaged macOS `.app`.

### Changed
- **Redaction save safety.** Saved redacted copies now serialize only objects
  reachable from the current trailer roots, which prevents stale previous
  revisions, annotation appearances, and orphaned image/form content from being
  re-emitted.
- **Scanned-image redaction.** Named image XObjects removed from redacted page
  content are pruned from page resources when no surviving page content uses
  them, so object bytes do not remain reachable after save.
- **Redacted-copy safety report.** The GUI safety report now includes a raster
  redaction audit that warns/fails closed when raster image content still
  overlaps requested redaction areas.
- **GUI input coverage.** Previously skipped headless keyboard/mouse tests now
  use Avalonia Headless input injection, and release docs distinguish those
  routed-event tests from packaged-app launch evidence and opt-in native
  System Events key/mouse smoke.

### Tests
- Required redaction gate passed after redaction changes:
  `dotnet test --no-restore --filter "FullyQualifiedName~Redaction"`.
- Focused OCR/image redaction and redacted-copy safety tests passed.
- v2.20 release smoke passed:
  `logs/release-smoke_20260704_124540` (docs, build, redaction, signature, UI
  workflow, macOS package, packaged-GUI evidence, and diffcheck).

## [2.19.0] - 2026-07-04

Everyday PDF workbench final release gate. No intended API break.

### Changed
- **Release rendering dashboard (#491, #535, #546).** The current full
  contract-driven rendering report classifies `14,979/14,979` scanned pages as
  release `PASS`, with `0` missing contract pages, `0` failed expectations, and
  `0` unreviewed or rejected `PASS_ONE` rows. Remaining low-impact reference
  disagreements stay visible as `MATCHES_ACCEPTED_REFERENCE`,
  `REFERENCE_REFUSAL_ACCEPTED`, `NON_RENDERABLE_ACCEPTED`, or a named accepted
  limitation instead of generic failures.
- **CMYK, ICC, and transparency rendering.** DeviceCMYK transparency-group
  preview now uses document output-intent information where available, ICCBased
  CMYK and `/DefaultCMYK` paths use the managed ICC preview evaluator, and
  CMYK soft-mask/screen-blend and knockout cases from the release corpus are
  classified against accepted reference targets.
- **GUI display parity (#537, #541).** The headless GUI display suite now checks
  that the displayed Avalonia bitmap matches the renderer output, including the
  ACC compensation-report cover page. Representative renderer-contract GUI
  coverage and pdf.js/Poppler shards are release evidence rather than manual
  spot checks.
- **Corpus tooling and progress reporting.** Long rendering runs write
  incremental/progress JSON, support large-PDF page sharding, use documented
  passwords from rendering contracts, and reclassify existing raw reports
  against current contract expectations without rerendering reference pages.
- **Release scope.** Broad font-model completion (#512, #513, #514, #515,
  #532), renderer performance optimization (#536), and narrower future
  renderer-quality issues remain tracked, but are explicitly deferred from this
  tag because the current release dashboard is clean.

### Tests
- Rendering quality reclassification:
  `logs/render-quality/release-prep-20260704/full-current-quality.json`
  reports `14,979 PASS`, `0` missing contracts, and `14,979` expectation passes.
- `dotnet test Excise.Cli.Tests/Excise.Cli.Tests.csproj --filter "FullyQualifiedName~CorpusScanClassificationTests"`
  passed: `42` passed, `0` failed.
- Release smoke evidence:
  - `logs/release-smoke_20260704_035730`: docs, build, redaction,
    signature, UI workflow, and PDF 2.0 renderer-conformance gates passed.
  - `logs/release-smoke_20260704_033109`: sequential project test gate passed,
    including the 144-page GUI display sweep with `0` failures and `0`
    non-pass display comparisons.
  - `logs/release-smoke_20260704_035238`: visual regression, macOS package
    build, and `git diff --check` gates passed.

## [2.15.0] - 2026-06-11

Form workflow hardening release. Additive; no breaking changes.

### Added
- **Explicit flattened form copy workflow (#457, #459, #460).** The desktop
  app now exposes **Flatten Form** / **Save Flattened Form Copy...** so users can
  choose between preserving interactive form fields and baking values into
  static page content.
- **Form widget metadata API (#459).** `PdfField` now exposes effective `/Ff`
  flags, checkbox/radio/choice helpers, and `PdfFieldWidget` metadata so
  consumers can distinguish checkboxes, radio groups, combo boxes, push buttons,
  and per-widget export values.

### Changed
- **Filled-form saves now persist the edited values (#460).** The desktop form
  overlay synchronizes edits and authored fields into the service-owned document
  before save, so interactive filled forms round-trip correctly through Save As.
- **Form field keyboard workflow is more deterministic (#458).** Fields are
  ordered top-to-bottom/left-to-right for tab traversal, focus styling is
  clearer, single-line fields commit on Enter, multiline fields commit on
  Ctrl+Enter, focus loss commits, and Escape restores the last committed value.
- **Flattened form appearances are stronger (#459).** Text is clipped/wrapped
  within widget bounds, radio groups draw only the selected widget, and
  `/NeedAppearances` is parsed using the spec key while remaining compatible
  with older pluralized fixtures.
- **Save labeling is clearer for original documents (#460).** Original PDFs with
  form edits now advertise **Save Filled Copy** rather than the generic
  **Save a Copy** label.

### Tests
- Build remains warning-free.
- Focused core AcroForm/public-API tests passed: 44 passed.
- Focused desktop form/viewmodel workflow tests passed: 157 passed.
- Required redaction filter passed after touching shared save workflow code.
- Full built test suite passed locally: 7034 passed, 53 skipped.

## [2.14.0] - 2026-06-11

Flat-PDF typewriter editing release. Additive; no breaking changes.

### Added
- **Typewriter flat text editing (#453, #454, #455, #456).** The desktop app
  now has a Typewriter mode for placing, editing, moving, resizing, and deleting
  pending text boxes on ordinary PDF pages. Saving flattens non-empty typewriter
  text into the page content stream instead of creating annotations, so output
  remains interoperable with basic PDF readers.
- **Core typewriter operation model.** `PdfTypewriterTextOperation`,
  `PdfTypewriterTextStyle`, and `PdfTypewriterTextApplier` provide a small
  immutable operation model and flattening service on top of `PdfGraphics`.
- **Viewer typewriter overlay API.** `PdfViewerControl` exposes
  `TypewriterTextOperations` plus created/edited/bounds/deleted events so hosts
  can keep pending flat-text edits in their own view models.

### Changed
- **Save state distinguishes redaction from ordinary edits.** Original files
  with pending redactions still use the redacted-copy workflow; original files
  with typewriter/form/page edits now advertise **Save a Copy** instead of the
  redaction-specific save label.
- The macOS native menu and in-window Edit menu now include Typewriter Mode.

### Tests
- Build remains warning-free.
- Core typewriter/edit/public-API tests passed: 11 passed.
- Avalonia public-API tests passed: 2 passed.
- Focused desktop viewmodel/viewer/typewriter workflow tests passed: 172 passed.
- Required redaction filter passed after touching the redaction save path.
- Full built test suite passed locally: 7025 passed, 53 skipped.

## [2.13.0] - 2026-06-10

Architecture hardening checkpoint release. No intended PDF behavior changes.

### Changed
- **MainWindowViewModel workflow split (#449).** Command initialization,
  form-authoring, hidden-text reveal, and redaction workflow code now live in
  focused partial modules, reducing the size and review risk of the main desktop
  view model while keeping the existing command and binding surface intact.
- **Renderer component split (#450).** `SkiaRenderer` path rendering and
  rendering state types were moved into focused renderer files without changing
  the public rendering API.
- **Viewer-control type split (#451).** `PdfViewerControl` event argument types
  and view/interaction enums now live in a separate partial file, keeping the
  control implementation more focused while preserving API compatibility.
- **Edit-operation foundation (#452).** Added a small immutable
  `PdfEditOperation` model for future typewriter, form, page-organization,
  redaction, and annotation workflows without enabling new editing behavior yet.
- **Dictionary optional-read helpers (#427).** `PdfDictionary` now exposes
  explicit `TryGetString` and `TryGetArray` helpers, and the document writer uses
  `TryGetArray` when preserving trailer `/ID` values.

### Tests
- Build remains warning-free.
- Focused core public API/edit/dictionary tests passed: 87 passed.
- Avalonia public API tests passed: 7 passed.
- Focused desktop viewmodel/keyboard/redaction tests passed: 238 passed, 4
  skipped.
- Focused rendering/operator/differential tests passed: 222 passed, 2 skipped.
- Full built test suite passed locally: 7011 passed, 53 skipped.

## [2.12.2] - 2026-06-10

macOS integration checkpoint release. No PDF behavior changes.

### Fixed
- **macOS native menu integration (#447).** The desktop app now installs a
  native macOS menu bar and hides the in-window menu on macOS, while keeping the
  in-window menu visible on Windows and Linux.
- **macOS titlebar spacing (#447).** The custom title label is shifted away from
  the traffic-light window controls on macOS so the title text no longer
  overlaps the close/minimize/zoom buttons.

### Tests
- Build remains warning-free.
- Focused GUI/viewmodel slice passed: 176 passed, 3 skipped.
- Full built test suite passed locally: 7001 passed, 53 skipped.

## [2.11.0] — 2026-06-08

Archival conformance + viewer-quality release. Additive; no breaking changes.

### Added
- **PDF/A-1b conformance (#425).** Embedded subset CID fonts now emit a `/CIDSet`
  in the FontDescriptor (covering all glyph slots of the retain-gid subset, as
  PDF/A-2 §6.2.11.4.2 requires it be complete). `PdfDocumentBuilder.PdfA(PdfA1B)`
  and `PdfA(PdfA2B)` output now both validate as conformant under veraPDF 1.30.2.
  A veraPDF conformance gate test covers both flavours.
- **Sharp high-zoom in the continuous reading view (#371 pt1).** Continuous mode
  now renders each page at a zoom-aware DPI (scaling with zoom, capped to bound
  memory) and caches by `(page, dpi)`, so zoomed reading stays crisp instead of
  upscaling a fixed-DPI bitmap. (Full visible-region tiling remains a future
  refinement.)

### Developer tooling
- **`Excise.Benchmarks` (#344).** A BenchmarkDotNet project measuring parse /
  render / text-extract (replacing the orphaned `run-benchmarks.sh` target);
  kept out of the shippable graph.

## [2.10.0] — 2026-06-08

Library DX + authoring-correctness release. Additive; no breaking changes
(public-API gates confirmed).

### Added
- **Public-API gate for the viewer libraries (#384).** A new lightweight,
  non-GUI `Excise.Avalonia.Tests` project snapshots the public surface of
  `Excise.Avalonia` and `Excise.Rendering` against committed baselines (same
  treatment `Excise.Core` got in #383) — any API change now fails CI until the
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
(public-API gate confirmed for `Excise.Core`).

### Added
- **Continuous (reading) view mode for `Excise.Avalonia` (#371).** New
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
  `com.adobe.pdf` so excise registers as a PDF handler. README documents setting it
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

Completes the **PromptResponse writer epic (#382)** — excise can now author
accessible, fillable, Unicode PDFs from structured content. All additive; the
public-API gate confirms no breaking changes.

### Added
- **Unicode text + embedded fonts (#378).** `PdfFont.FromFile(path, size)` /
  `FromTrueType(bytes|Stream, size)` embed a TrueType font as a Type0 /
  Identity-H composite font with a ToUnicode CMap, so arbitrary Unicode (CJK,
  Arabic, accented Latin, Greek, Cyrillic, …) both renders and stays
  extractable. Backed by a new dependency-free sfnt reader
  (`Excise.Core.Fonts.TrueTypeFontFile`). Full-font embedding; subsetting and CFF
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
- Raised `Excise.Core` CI line coverage to ~93% and ratcheted the gate to 92.5%
  (#351); CI installs `fonts-dejavu-core` so the embedding tests run
  deterministically. The macOS `.app` is now built and attached by CI.

## [2.4.1] — 2026-06-06

Packaging, API-stability, and CI hardening on top of v2.4.0. No public-API
changes (enforced by the new gate) — a pure patch.

### Added
- **Public-API gate (#383).** `PublicApiApprovalTests` snapshots the full
  `Excise.Core` public surface against a committed baseline
  (`Excise.Core.Tests/PublicApi/Excise.Core.approved.txt`); any public-API change
  fails CI until intentionally re-approved (`APPROVE_PUBLIC_API=1`). Makes every
  API change a deliberate SemVer decision.
- **SourceLink + symbols.** The three publishable libraries (`Excise.Core`,
  `Excise.Rendering`, `Excise.Avalonia`) now ship portable `.snupkg` symbol packages
  with SourceLink and deterministic CI builds (shared `Packaging.props`), so
  consumers can step into the source while debugging.
- README "Versioning & API stability" section documenting the SemVer policy,
  the `Excise.Core.Authoring.*` stable writer surface, and local-feed (not
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
- Headless GUI tests (`Excise.App.Tests`) now run only when GUI-relevant paths
  change (or on `main`), so library-only PRs aren't gated on the slow GUI suite.
- Quarantined the flaky `KeyboardShortcutTests.CtrlS_SavesFile` on headless CI
  (#363) — it intermittently deadlocked the Avalonia dispatcher and crashed the
  test host. Still runs locally; the save path stays covered elsewhere.

## [2.4.0] — 2026-06-05

Adds a friendly, high-level **PDF authoring** API so third-party .NET apps can
generate PDFs from structured content without touching coordinates — the
writer-side facade tracked by #383 (PromptResponse writer epic #382).

### Added
- **`Excise.Core.Authoring.PdfDocumentBuilder` — high-level writer facade (#383).**
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
  extracts all text. 17 new tests; full `Excise.Core` suite green (2744 passing).

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
  `Excise.Core` ships as a NuGet package.

## [2.3.0] — 2026-06-04

Turns excise's engine into reusable libraries for the wider .NET/Avalonia ecosystem.

### Added
- **`Excise.Avalonia` — reusable Avalonia PDF viewer control (#365).** The
  `PdfViewerControl` (zoom/pan, navigation, text selection, search highlights,
  annotations, links, form-field overlays) is extracted from the `Excise.App`
  app into a standalone, dependency-light library (depends only on `Excise.Core`
  + `Excise.Rendering` + Avalonia + SkiaSharp). Any Avalonia app can now drop in a
  pure-managed, SkiaSharp-based PDF viewer — a gap the ecosystem lacked. The
  app consumes it as the reference implementation; a minimal `Excise.Avalonia.Sample`
  shows the dependency-light usage.
- **Framework-neutral render API (#366).** `Excise.Rendering.SkiaRenderer` gains
  `RenderPage(page, options, CancellationToken)` (cancellable between
  content-stream operators, companion to #346) and `RenderPageToPng(page, Stream, …)`
  for non-Skia consumers.
- **NuGet-packable trio.** `Excise.Core`, `Excise.Rendering`, and `Excise.Avalonia`
  carry package metadata + per-package READMEs; `dotnet pack` produces three
  valid `.nupkg`s (attached to this release; not pushed to nuget.org).

### Changed
- `Excise.App` now consumes `Excise.Avalonia` rather than embedding the control;
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
  points at `Excise.Core` (not the removed `Excise.App/Services/Redaction/`), and
  the frozen "Current Status (v1.4.0)" block now points at `CHANGELOG.md` /
  GitHub Releases so the version no longer goes stale in-file. (#349)

## [2.2.0] — 2026-06-03

Redaction-security release: closes the remaining content-type and
coordinate gaps so redaction reliably removes — not merely covers — every
way content can land under the redaction area. Also restores a working CI
gate (it had been silently broken) and raises Excise.Core coverage.

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
- Raised Excise.Core coverage and set the enforced gate to the level CI meets.

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
- ReadyToRun for `Excise.Cli`. (#334)
- Moved off preview packages and bumped to latest stable: **Avalonia 12.0.4, ReactiveUI 23.2.27, SkiaSharp 3.119.4, .NET 10.0.8**. (#340)
- Removed the IdlerGear integration; refreshed stale docs (versions/architecture) and archived obsolete plan docs. (#349)

### Fixed (stability & security hardening)
- Parser **recursion-depth guard** — deeply nested hostile PDFs throw instead of StackOverflow. (#346)
- Inline-image **`/L` length** used to avoid false-positive `EI` in binary data. (#347)
- **Redaction re-encodes kept CID/CJK text** with original codes instead of unrenderable Unicode. (#353)
- ToUnicode CMap parse no longer swallows fatal exceptions. (#345)
- Headless test harness wires ReactiveUI to the Avalonia dispatcher — fixes a cross-thread `CanExecute` crash. (#358)

### Tests
- +18 tests: parser recursion limits, inline-image `/L`, CID-redaction pipeline, and previously-untested operators (`sh`, marked content, `BX`/`EX`, `d0`/`d1`). Full Excise.Core suite: 2562 passing.

### Known limitations / deferred
- Inline-image redaction round-trip (#354), Form XObject redaction (#355, flatten-then-redact), and rotated-page redaction (#356) remain open.

## [2.0.0] — 2026-04-25

The headline of v2.0 is **a complete rewrite of the PDF stack**. v1.0 sat on
top of PdfPig + PDFsharp + PDFtoImage (PDFium) + Tesseract.NET; v2.0 ships a
pure-.NET stack of excise-owned libraries — Excise.Core (parser/writer),
Excise.Rendering (SkiaSharp renderer), and Excise.Ocr (system tesseract shell) —
with no external PDF dependencies remaining. Same redaction guarantee, same
GUI, fewer moving parts, and the renderer now handles real-world PDFs from
WeasyPrint, Word, XEP, and CJK toolchains without falling back to garbage.

### Added

#### Excise.Core — pure-.NET PDF parser, writer, and content-stream library
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

#### Excise.Rendering — SkiaSharp-based renderer
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

#### Excise.Ocr — OCR via system `tesseract` CLI
- New project. Shells out to the system tesseract binary, parses TSV
  output, returns `OcrResult` with per-word bounding boxes.
- Differential OCR auditor: render the page twice (once with overlays
  stripped, once without), OCR both, diff the word sets — surfaces text
  hidden inside rasters by overlay, the rasterized analogue of structural
  redaction.
- Replaces the previous Tesseract.NET nuget binding (which pinned to a
  leptonica version no longer shipping on modern Linux).

#### Excise.Cli — `excise` command-line tool
- `excise render <file> -o out.png [--page N] [--dpi N]`
- `excise redact <file> -o out.pdf --text "PHRASE"` — glyph-level removal.
- `excise audit <file> [--deep] [--json]` — structural and (with `--deep`)
  differential-OCR audit of hidden text.
- `excise ocr <file>` — OCR the page and emit TSV.

#### GUI — Excise.App
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
  Excise.Core / Excise.Rendering: `PdfRenderService`, `PdfTextExtractionService`,
  `PdfSearchService`, `SignatureVerificationService`, `PdfDocumentService`,
  `BatesNumberingService`, `RedactionService`.
- `RedactionService` unified — `RedactArea` (mouse marquee) and `RedactText`
  (find-and-redact) now share a single Excise.Core pipeline; the previous
  parallel PdfSharp+PdfPig path is gone.
- The legacy `Excise.App.Redaction` library (and its `pdfer` CLI) deleted —
  glyph-level redaction lives in Excise.Core; the Excise.Cli `redact` command
  replaces `pdfer`.
- System-font fallback widened: strip the 6-letter PDF subset prefix,
  match by family prefix instead of exact name, and recognize Semibold /
  Medium as Bold. `TimesNewRomanPS-BoldMT` now correctly maps to Times New
  Roman instead of Sans-Serif; `BookmanStd` to Times; `ZapfDingbatsStd` to
  Noto Sans Symbols2.
- Build is clean — 0 warnings, 0 errors across all projects.

### Removed

- **PdfPig 0.1.11** — replaced by `Excise.Core.Text`.
- **PDFsharp 6.2.2** — replaced by `Excise.Core.Document` + `Excise.Core.Writing`.
- **PDFtoImage 4.0.2** + native PDFium — replaced by `Excise.Rendering` (Skia).
- **Tesseract.NET nuget** — replaced by `Excise.Ocr` (CLI shell).
- **Excise.App.Redaction** project + **`pdfer` CLI** — replaced by
  Excise.Core glyph-level redaction + `excise redact`.
- **Excise.App.Demo** + Validator tools — superseded by Excise.Cli + the new
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
level. `Excise.Rendering.Tests/Visual/` and `Excise.App.Tests/UI/baselines/`
keep PNG baselines for regression detection.

### Migration

The architectural change is mostly transparent for end users — the desktop
app, the redaction guarantee, and the file format are unchanged. For
embedders moving off the v1.0 surface:

- `Excise.App.Redaction` (library) → `Excise.Core.Text.Segmentation` —
  use `page.RedactArea(rect)` / `page.RedactAreas(rects)` /
  `document.RedactText("phrase")` from `PdfPageRedactionExtensions` /
  `PdfDocumentRedactionExtensions`.
- `pdfer` CLI → `excise redact` — same options.
- PdfPig text extraction → `Excise.Core.Text` — `PdfDocument.GetText(page)`
  and `PdfDocument.GetLetters(page)`.
- PDFsharp `PdfDocument` → `Excise.Core.Document.PdfDocument` — note that
  `PdfDocument.Open(stream)` now takes ownership semantics via
  `Open(stream, ownsStream)`.
- PDFtoImage → `Excise.Rendering.SkiaRenderer.RenderPage(page, options)`.

### Known gaps deferred to v2.1+

- PDF encryption / password handling (#237) — v2.1.
- Partial glyph rasterization for redaction cuts that bisect a glyph
  (#278). Current full-glyph removal is conservative-safe.
- PDF Annotations (#271), Interactive Forms (#272), Tagged PDF (#275),
  Advanced Transparency (#274), Multimedia (#273) — v2.2.
- Compass-image-style inline-image-with-Smask cases that still fall back
  to placeholder rendering (covered indirectly by #274).

### Test counts at release

- Excise.Core.Tests: 442 passing, 2 skipped
- Excise.Rendering.Tests: 175 passing
- Excise.Cli.Tests: 7 passing
- Excise.App.Tests: 221 passing, 2 skipped (require Tesseract installed)

**Total: 845 tests, 0 failing**

---

## [1.0.0] — 2026-01-11

First major stable release. Cross-platform PDF editor with **true
glyph-level redaction** — content removed from the PDF structure, not just
visually covered. Built on PdfPig + PDFsharp + PDFtoImage + Tesseract.NET.

See the GitHub release for full v1.0.0 notes:
https://github.com/marctjones/excise/releases/tag/v1.0.0
