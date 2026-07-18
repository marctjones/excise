# Changelog

All notable changes to pdfe are documented here. Format roughly follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); this project uses
semantic versioning.

## [Unreleased]

### Added
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
  to AES-256** — never downgraded, never silently decrypted. `pdfe redact`
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
  removing sensitive content from your own copy is pdfe's core purpose.

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
  found **33 skipped tests in Pdfe.Core alone** — including rotation tests in code
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
anyone using pdfe to redact sensitive documents.**

Both fixed leaks share one root cause: redaction was verified by asking pdfe's
own text extractor whether pdfe had removed the text. That extractor reads the
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
- **New: redaction is now verified by tools that are not pdfe** (#606, #607,
  #609) — independent extraction (mutool), independent rendering (Ghostscript)
  as a before/after ink differential, and the full corpus. Ink absence is the
  stronger claim: extraction cannot see text rendered as vector paths or raster
  pixels; a renderer can.

### Known security limitations (unchanged from 2.27.1 — not introduced here)

- **Redaction is silently incomplete where text extraction is blind (#637).**
  Where pdfe cannot read text, it cannot redact it, and it reports success
  anyway. Measured on `irs-1040-instructions.pdf` page 47: pdfe extracts 471
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
- Changed the macOS app bundle identifier from `com.marcjones.pdfe` to
  `cl.skpt.pdfe` so LaunchServices, Finder/Open With, and packaged GUI smoke
  target the skpt-owned pdfe app identity.
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
in `Pdfe.Avalonia`; no intended breaking change.

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
- Regenerated the `Pdfe.Avalonia` public API approval baseline for the
  intentional `RenderVersion` addition.
- Redaction gates remain required for this release line:
  `dotnet test ... --filter "FullyQualifiedName~Redaction"`.

## [2.25.0] - 2026-07-04

Benchmarking and renderer-performance release. No intended public API break.

### Added
- **Benchmark suite (#344, #357).** Added `Pdfe.RenderTools benchmark-suite`
  and wired `scripts/run-benchmarks.sh` so one command emits
  `benchmark-report.json`, `benchmark-pages.csv`, and `benchmark-report.md`
  covering pdfe parse/text/render speed, external-reference fidelity,
  RMSE/SSIM metrics, tool availability, and subprocess-only license isolation.
- **Benchmark regression gate (#344, #357).** Added a release-smoke benchmark
  gate plus a deterministic CI gate that runs the benchmark suite in synthetic
  no-oracle mode and fails on pdfe parse/render/redaction regressions.
- **Redaction-completeness signal (#357).** The benchmark report now includes a
  synthetic glyph-level redaction check so speed reporting does not drift away
  from pdfe's security-critical differentiator.

### Changed
- **Benchmark wrapper (#344).** `scripts/run-benchmarks.sh` now runs the
  benchmark suite by default, keeps `corpus-hotspots` and
  `gui-display-hotspots`, and exposes `benchmarkdotnet` for the isolated
  `Pdfe.Benchmarks` microbenchmark project.
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
- Full Debug build passed: `dotnet build pdfe.sln -c Debug`.

## [2.23.0] - 2026-07-04

Automation API and platform integration release. Additive public API change in
`Pdfe.Core.Automation`; no intended breaking change.

### Added
- **Stable CLI automation contract (#561).** Added `pdfe batch` for JSON
  workflows with structured final reports, optional report files, progress
  NDJSON on stderr, documented exit codes, relative-path resolution, and
  password-aware document open without writing passwords to reports.
- **JSON CLI output (#561).** Added `--json` output to `pdfe info`,
  `pdfe text`, and `pdfe render`, and added `--password` handling to
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
- Full Debug build passed: `dotnet build pdfe.sln -c Debug`.

## [2.22.0] - 2026-07-04

Accessibility and assistive-technology readiness release. Additive public API
change in `Pdfe.Core.Automation`; no intended breaking change.

### Added
- **Shared semantic command metadata (#562).** Added `Pdfe.Core.Automation`
  with stable command IDs, labels, descriptions, shortcuts, CLI verbs,
  parameters, result fields, disabled reasons, and destructive/security flags.
- **CLI command metadata (#562).** Added `pdfe commands` and
  `pdfe commands <id> --json` so automation and batch workflows can query the
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
- Full Debug build passed: `dotnet build pdfe.sln -c Debug`.

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
  can separate pdfe render cost from reference-render and comparison overhead.

### Tests
- Focused responsiveness and scheduling gate passed:
  `dotnet test PdfEditor.Tests/PdfEditor.Tests.csproj -c Debug --filter "FullyQualifiedName~GuiResponsivenessBudgetTests|FullyQualifiedName~MainWindowRenderSchedulingTests|FullyQualifiedName~PdfRenderServiceCacheTests|FullyQualifiedName~ResponsivenessReportTests|FullyQualifiedName~GuiWorkflowCoverageMatrixTests"`.
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
- `dotnet test Pdfe.Cli.Tests/Pdfe.Cli.Tests.csproj --filter "FullyQualifiedName~CorpusScanClassificationTests"`
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
- **`Pdfe.Benchmarks` (#344).** A BenchmarkDotNet project measuring parse /
  render / text-extract (replacing the orphaned `run-benchmarks.sh` target);
  kept out of the shippable graph.

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
