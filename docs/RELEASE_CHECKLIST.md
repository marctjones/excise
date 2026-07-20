# Release Checklist

Use this checklist before tagging any `v*` release.

## Documentation Accuracy

- Run `scripts/verify-doc-claims.sh`.
- Confirm README feature bullets match implemented commands, menu items, CLI commands, and public APIs.
- Confirm `Excise.Core/README.md`, `Excise.Rendering/README.md`, and `Excise.Avalonia/README.md` describe the current library APIs.
- Confirm release notes do not imply future issue scope is already shipped.
- If a behavior change touches redaction, signatures, metadata, attachments, or forms, update implementation, UI text, tests, and docs in the same change.

## Validation

- **This checklist is tier T2/T3** (`scripts/test-tier.sh t2`/`t3`, #646) — the
  release-candidate and third-party-distribution gates. T0 (pre-push) and T1
  (what CI blocks a PR on) are lighter and run far more often; see
  `CLAUDE.md`'s "Test Tiers" section for the full table and the blast-radius
  rule for picking one. Everything below this line is what T2/T3 actually run.
- Run `scripts/release-smoke.sh --visual --package --packaged-gui --version <version>` before tagging a release candidate.
- **Run it on an otherwise idle machine.** `Excise.App.Tests` is serial by design
  (SkiaSharp's process-wide native font manager, #363) and its 144-page display
  sweep is therefore load-sensitive. Concurrent work — or a bloated `logs/` +
  `artifacts/` tree — has produced **false reds** with zero page failures (#619).
  If the sweep reports DEADLINE, that is a TIME limit, not a correctness failure;
  shard it rather than ignore it:
  `scripts/run-gui-display-sweep.sh 4`
- **Run the extraction-parity gate**: `scripts/check-extraction-parity.sh`
  (requires `mutool` on `PATH` and `test-pdfs/smoke/` downloaded — it fails
  loudly rather than silently skipping when either is missing, which is the
  exact failure mode #645 exists to close). Compares excise-vs-mutool text
  extraction across the smoke corpus against the checked-in floors in
  `tests/extraction-parity/baseline.json`; fails on regression. Run any font
  or text-extraction change (#513–#515) through this before merging — a
  font-resolver change either improves the parity delta or it is rejected.
  `--update` rewrites the baseline from the current measurement; review the
  diff before committing.
- **Run the skip budget** for every suite you touched:
  `scripts/check-skip-budget.sh <project>.csproj`
  A test that silently stops running is coverage loss you cannot see — this is
  how a security-relevant assertion (`HiddenTextToggles_DoNotLoadOcrAssembly…`)
  quietly stopped executing while the suite stayed green (#619).
- Run `dotnet build excise.sln --no-restore`.
- Run `dotnet test --no-build --filter "FullyQualifiedName~Redaction"` after any redaction-adjacent change.
- Run the signature verification and UI workflow gates in `scripts/release-smoke.sh`.
- Run the dedicated accessibility gate:
  `scripts/release-smoke.sh --quick --only=accessibility`. This uses
  `scripts/run-accessibility-smoke.sh` to verify semantic command metadata,
  accessible names/descriptions/shortcuts/states, status announcements, and
  representative keyboard-only reachability without taking keyboard or mouse
  focus. Platform AX/UIA/AT-SPI procedures are documented in
  `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md`.
- Run the dedicated automation API gate:
  `scripts/release-smoke.sh --quick --only=automation`. This uses
  `scripts/run-automation-smoke.sh` to verify `excise commands`, JSON output,
  batch workflow reports, progress NDJSON, password redaction, destructive
  command refusal, and a real render workflow without taking keyboard or mouse
  focus. The platform contract and examples are documented in
  `docs/AUTOMATION_API.md`.
- Run the dedicated UX/icon audit gate:
  `scripts/release-smoke.sh --quick --only=ux`. This uses
  `scripts/run-ux-icon-audit.sh` to capture headless screenshots for empty/open,
  document navigation, search, redaction, forms, typewriter/annotation, and
  preferences states while verifying toolbar/menu vector icons, tooltips, and
  accessibility command IDs. This is separate from GUI display parity.
- Run the dedicated benchmark gate:
  `scripts/release-smoke.sh --quick --only=benchmark`. This uses
  `scripts/run-benchmarks.sh suite` to emit `benchmark-report.json`,
  `benchmark-pages.csv`, `benchmark-hotpaths.json`, and
  `benchmark-report.md` with excise parse/text/render timing, excise-owned hot-path
  buckets, reference-render fidelity when installed reference CLIs are present,
  RMSE/SSIM metrics, redaction-completeness evidence, and explicit license
  isolation for subprocess-only reference renderers. The default
  `scripts/run-benchmarks.sh` wrapper additionally writes
  `latest-performance-baseline.json` and `latest-performance-baseline.md` to
  index benchmark, corpus, GUI display, and GUI workflow hotspot artifacts.
- Run the dedicated Native AOT release-lane gate before shipping an AOT
  artifact:
  `scripts/release-smoke.sh --quick --only=aot`. This uses
  `scripts/run-aot-smoke.sh` to publish/package the GUI with
  `-p:PublishAot=true`, capture IL/AOT warning output, split `.dSYM`/`.pdb`
  symbols from the user-facing macOS app bundle, and write `aot-smoke.json` plus
  `aot-smoke.md`. Use `scripts/run-aot-smoke.sh --gui-smoke` on an interactive
  macOS runner for packaged AOT launch/open/render evidence.
- Run packaged-app GUI evidence when validating desktop packages:
  `scripts/release-smoke.sh --quick --package --packaged-gui --version <version>`.
  This writes JSON/markdown evidence for #558/#571 and responsiveness timing
  evidence for #577/#581/#582 without taking keyboard or mouse focus. The
  release wrapper uses the packaged executable directly so app-internal timing
  works even when Launch Services is constrained by a locked session; the
  explicit alias is `--packaged-gui-direct-exec`. Use
  `--packaged-gui-background-open` when specifically investigating
  open-with/file-activation behavior. On a dedicated runner, add
  `--packaged-gui-focus-input` to run the native System Events key/mouse smoke
  that requires macOS Accessibility permission and foreground focus.
- Run the focused tests for the changed area.
- Run the all-pages pdf.js rendering gate in tmux before declaring rendering
  quality final:
  `scripts/run-exploratory-corpus-tmux.sh -- --page-mode all --pdf-timeout-ms 120000 --chunk-parallel 2 --per-chunk-parallel 1`.
  Review `PASS` / `PASS_ONE` / `DIFF` / classified non-fidelity counts and
  ensure remaining blockers are fixed, issue-linked, or documented as accepted
  limitations.
- Run `dotnet test excise.sln --no-build --logger "console;verbosity=minimal"` before tagging.
- Run `scripts/run-visual-regression-local.sh --release` before tagging a release candidate.
- Run `git diff --check`.

`scripts/release-smoke.sh` is the repeatable wrapper for these gates. It does
not tag, push, create a GitHub Release, or upload artifacts. Its build gate
restores packages so it is reliable after configuration-changing package
builds. Its build and test gates use the same Debug configuration as CI because
Release excludes the developer scripting surface by default; `--package` is the
Release artifact check. Use `--quick` for a short documentation/build/redaction
check, and use `--visual --package --packaged-gui` for the full local
release-candidate pass. The packaged-GUI smoke differs from Avalonia.Headless
tests: headless tests prove routed events and view-model behavior in process,
while packaged-GUI smoke proves the built `.app` launches with a real PDF and
records screenshot/log/report artifacts plus app-first-page timing when
`EXCISE_RESPONSIVENESS_REPORT` is inherited. Focus-taking native keyboard/mouse
injection is opt-in because macOS requires Accessibility permission.

## Everyday PDF Workbench RC Matrix

This matrix is the final-release gate for issue #490. Every row needs at least
one automated gate, scripted smoke, or explicit manual packaged-app step with a
named fixture. If a row fails during release-candidate testing, create or link a
GitHub issue and either fix it or list it in final release notes as an accepted
limitation before tagging.

| Workflow | Automated or scripted gate | Fixture/manual RC step |
| --- | --- | --- |
| Open PDFs from Finder/Explorer/open-with and from the app | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `GuiWorkflowCoverageMatrixTests`; packaging file-association doc claim tests | Packaged app: open `test-pdfs/smoke/irs-w9.pdf` by app picker/open-with and from File > Open. On Windows, verify the installer registers excise in Default Apps without stealing the default unless selected. |
| Navigate long PDFs, thumbnails, page labels, zoom, fit width/page | `PdfViewerControlTests`; `ThumbnailCacheTests`; `OutlineTreeNavigationTests`; `PdfPageLabelTests` | Packaged app: open `test-pdfs/smoke/irs-1040-instructions.pdf`, jump first/middle/last pages, toggle thumbnails/outline, verify page labels, Fit Width, Fit Page, zoom in/out. |
| Search, select text, copy text | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `TextSelectionDragTests`; `PdfSearchServiceTests`; `SearchHighlightOverlayTests`; `RealWorldSearchTests` | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, search `syllabus`, select a sentence, copy, and paste into a plain-text editor. |
| Fill common forms, save filled copy, reopen, verify values persisted | `FormWorkflowTests`; `FormFieldsOverlayTests`; `PdfDocumentServiceTests` save/load coverage | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, fill text fields and a checkbox/radio where available, Save Filled Copy, reopen in excise, verify field values remain editable. |
| Flatten form copy, reopen, verify static output | `FormWorkflowTests`; `Excise.Core.Tests.Document.AcroFormReadOnlyTests`; form flattening core tests | Packaged app: use `test-pdfs/smoke/irs-w9.pdf`, Flatten Form, reopen in excise, verify values are visible static page content and no inline field editor appears for flattened values. |
| Add typewriter text to flat PDF, save copy, reopen | `TypewriterWorkflowTests`; typewriter service tests; `GoldenPathTests` save workflow coverage | Packaged app: open `test-pdfs/smoke/scotus-trump-v-anderson.pdf`, add typewriter text on page 1, Save Copy, reopen, verify text is visible and extractable. |
| Highlight selected text and add sticky notes, save, reopen | `AnnotationAuthoringWorkflowTests`; `AnnotationWorkflowServiceTests`; annotation default-appearance rendering tests | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, select text and highlight it, add a sticky note, Save Copy, reopen, verify highlight and note persist. |
| Reorder, rotate, extract, remove, and combine pages | `PageOrganizationWorkflowTests`; `PageOrganizationWorkflowServiceTests`; `PdfDocumentServiceTests` page operations | Packaged app: use `test-pdfs/smoke/scotus-trump-v-us.pdf` plus `test-pdfs/smoke/irs-w4.pdf`, rotate page 1, reorder pages, extract a page, remove a page, combine another PDF, save and reopen. |
| Redact text/area, save redacted copy, verify text removal plus metadata/attachment scrub status | `RedactionMouseWorkflowTests`; `RedactionServiceTests`; `RedactedCopySafetyServiceTests`; `dotnet test --filter "FullyQualifiedName~Redaction"` | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, redact a visible phrase and an area, save redacted copy, reopen, verify copied/extracted text no longer contains the phrase and safety summary reports metadata/attachment scrub status. |
| Audit hidden text and signatures with clear user-facing states | `RevealHiddenTextTests`; `HiddenTextDetectorTests`; `SignatureVerificationServiceTests`; `SignatureVerificationWorkflowServiceTests` | Packaged app: run hidden-text reveal on a generated black-box-redaction fixture; open a signed fixture when available or a generated invalid-signature fixture, verify the signature panel clearly distinguishes valid/invalid/unsupported trust states. |
| Accessibility names, command metadata, keyboard-only reachability, and status announcements | `AccessibilityRegressionTests`; `PdfCommandRegistryTests`; `CommandMetadataCommandTests`; `scripts/run-accessibility-smoke.sh` | Platform review: follow `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md` for macOS AX/VoiceOver, Windows UI Automation, and Linux/GNOME AT-SPI tree checks on dedicated runners. |
| CLI automation, batch JSON, progress events, and platform wrappers | `BatchAutomationCommandTests`; `CommandMetadataCommandTests`; `scripts/run-automation-smoke.sh` | Platform review: follow `docs/AUTOMATION_API.md` examples for AppleScript/Shortcuts, PowerShell/Power Automate, and Linux/GNOME CLI workflows. |
| UX/icon visual polish, toolbar/menu affordances, and design-quality screenshots | `VisualPolishAuditTests`; `scripts/run-ux-icon-audit.sh` | Review the generated `ux-icon-audit.md`, PNG screenshots, and `ux-icon-audit.json` before closing visual-polish issues. |
| Benchmark speed, reference fidelity, redaction completeness, and renderer hotspot evidence | `BenchmarkSuiteTests`; `scripts/run-benchmarks.sh suite`; `Excise.RenderTools benchmark-suite`; `Excise.Rendering.Tests` performance/memory tests | Review `benchmark-report.md`, `benchmark-report.json`, `benchmark-pages.csv`, `benchmark-hotpaths.json`, `latest-performance-baseline.md`, and aggregate `corpus-hotspots`, `gui-display-hotspots`, and `gui-workflow-hotspots` reports before closing performance issues. |
| Native AOT app packaging, warning budget, and symbol split | `scripts/run-aot-smoke.sh`; `scripts/release-smoke.sh --quick --only=aot`; optional `scripts/run-aot-smoke.sh --gui-smoke` on an interactive macOS runner | Review `aot-smoke.md`, `aot-smoke.json`, `aot-warnings.txt`, package size, symbol archive size, and any packaged GUI smoke evidence before shipping an AOT artifact. |

The repeatable automated gate for this table is:

```bash
dotnet test Excise.App.Tests --filter "FullyQualifiedName~GuiWorkflowCoverageMatrix|FullyQualifiedName~GoldenPath|FullyQualifiedName~Workflow|FullyQualifiedName~RevealHiddenText|FullyQualifiedName~SignatureVerification" --logger "console;verbosity=normal"
```

## Encryption Evidence (#644)

The encryption writer's release evidence is the interop gate suite — excise
must never be its own oracle for "this file is actually protected." A
mis-emitted `/Encrypt` dictionary that some reader silently ignores (opening
the "protected" file without a password) is the catastrophic failure mode
this section exists to catch.

- Run the automated gate on a machine with the reference tools installed
  (mutool, qpdf, ghostscript, pdftoppm):

  ```bash
  EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1 \
    dotnet test Excise.Rendering.Tests --filter "FullyQualifiedName~EncryptionInteropGateTests"
  ```

  `EncryptionInteropGateTests` covers, for BOTH AES-256 (R6) and AES-128
  (R4): correct user password opens (mutool extraction, qpdf `--check`,
  Ghostscript and pdftoppm pixel-identical renders vs. the plain baseline);
  the distinct owner password opens with full authority (qpdf reports
  "owner password", pdftoppm `-opw` renders); the wrong password and the
  ABSENT password are rejected by every tool; and qpdf's independent
  `--show-encryption` decode reports the `/P` mask semantically exactly as
  set. Unavailable tools skip loudly by name; the
  `EXCISE_REQUIRE_ENCRYPTION_INTEROP_TOOLS=1` env var makes an all-tools-missing
  (vacuously green) run a hard failure, which is what release evidence
  requires.
- **Manual Acrobat step** (Acrobat is not scriptable in this environment —
  it is deliberately not faked in the automated gate): produce one R6
  (AES-256) and one R4 (AES-128) sample encrypted by excise with a non-empty
  user password, and open each in Adobe Acrobat (Reader is fine):
  - the correct password must open the document;
  - the wrong password must be rejected;
  - dismissing the password prompt (no password) must not show any content;
  - File > Properties > Security must report the document as protected.
- Also relevant: `EncryptionWriterInteropTests` (per-writer-issue coverage,
  #639/#640) and `EncryptionPreservationInteropTests` (#643 round-trips);
  both run under `dotnet test Excise.Rendering.Tests --filter
  "FullyQualifiedName~Encryption"`.

## Issue Hygiene

- Every shipped issue has a completion comment with validation evidence.
- Remaining work stays in GitHub Issues, not TODO comments or roadmap prose.
- Broad epics stay open until all acceptance criteria are done; patch-release issues close when their concrete gate is implemented.

## Release

- Commit with a scoped message (on `develop` — the default branch where all
  work and PRs land).
- Tag with an annotated `v*` tag.
- Push the commit and the tag.
- **Move `main` to the release**: `git push origin v<X.Y.Z>^{commit}:main`.
  `main` is the stable release pointer, nothing more — it only ever advances
  to release tags. A stale `main` caused community PRs #673/#676 to target
  dead pre-rename code (fixed 2026-07-20: default branch switched to
  `develop`, `main` repointed to v3.1.0; the old v2.28-era `main` tip is
  preserved by the `v2.28.0` tag).
- Create or verify the GitHub Release.
- Verify `.sha256` files are present for each release artifact.
