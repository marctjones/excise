# Release Checklist

Use this checklist before tagging any `v*` release.

## Documentation Accuracy

- Run `scripts/verify-doc-claims.sh`.
- Confirm README feature bullets match implemented commands, menu items, CLI commands, and public APIs.
- Confirm `Pdfe.Core/README.md`, `Pdfe.Rendering/README.md`, and `Pdfe.Avalonia/README.md` describe the current library APIs.
- Confirm release notes do not imply future issue scope is already shipped.
- If a behavior change touches redaction, signatures, metadata, attachments, or forms, update implementation, UI text, tests, and docs in the same change.

## Validation

- Run `scripts/release-smoke.sh --visual --package --packaged-gui --version <version>` before tagging a release candidate.
- Run `dotnet build pdfe.sln --no-restore`.
- Run `dotnet test --no-build --filter "FullyQualifiedName~Redaction"` after any redaction-adjacent change.
- Run the signature verification and UI workflow gates in `scripts/release-smoke.sh`.
- Run the dedicated accessibility gate:
  `scripts/release-smoke.sh --quick --only=accessibility`. This uses
  `scripts/run-accessibility-smoke.sh` to verify semantic command metadata,
  accessible names/descriptions/shortcuts/states, status announcements, and
  representative keyboard-only reachability without taking keyboard or mouse
  focus. Platform AX/UIA/AT-SPI procedures are documented in
  `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md`.
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
- Run `dotnet test pdfe.sln --no-build --logger "console;verbosity=minimal"` before tagging.
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
`PDFE_RESPONSIVENESS_REPORT` is inherited. Focus-taking native keyboard/mouse
injection is opt-in because macOS requires Accessibility permission.

## Everyday PDF Workbench RC Matrix

This matrix is the final-release gate for issue #490. Every row needs at least
one automated gate, scripted smoke, or explicit manual packaged-app step with a
named fixture. If a row fails during release-candidate testing, create or link a
GitHub issue and either fix it or list it in final release notes as an accepted
limitation before tagging.

| Workflow | Automated or scripted gate | Fixture/manual RC step |
| --- | --- | --- |
| Open PDFs from Finder/Explorer/open-with and from the app | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `GuiWorkflowCoverageMatrixTests`; packaging file-association doc claim tests | Packaged app: open `test-pdfs/smoke/irs-w9.pdf` by app picker/open-with and from File > Open. On Windows, verify the installer registers pdfe in Default Apps without stealing the default unless selected. |
| Navigate long PDFs, thumbnails, page labels, zoom, fit width/page | `PdfViewerControlTests`; `ThumbnailCacheTests`; `OutlineTreeNavigationTests`; `PdfPageLabelTests` | Packaged app: open `test-pdfs/smoke/irs-1040-instructions.pdf`, jump first/middle/last pages, toggle thumbnails/outline, verify page labels, Fit Width, Fit Page, zoom in/out. |
| Search, select text, copy text | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `TextSelectionDragTests`; `PdfSearchServiceTests`; `SearchHighlightOverlayTests`; `RealWorldSearchTests` | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, search `syllabus`, select a sentence, copy, and paste into a plain-text editor. |
| Fill common forms, save filled copy, reopen, verify values persisted | `FormWorkflowTests`; `FormFieldsOverlayTests`; `PdfDocumentServiceTests` save/load coverage | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, fill text fields and a checkbox/radio where available, Save Filled Copy, reopen in pdfe, verify field values remain editable. |
| Flatten form copy, reopen, verify static output | `FormWorkflowTests`; `Pdfe.Core.Tests.Document.AcroFormReadOnlyTests`; form flattening core tests | Packaged app: use `test-pdfs/smoke/irs-w9.pdf`, Flatten Form, reopen in pdfe, verify values are visible static page content and no inline field editor appears for flattened values. |
| Add typewriter text to flat PDF, save copy, reopen | `TypewriterWorkflowTests`; typewriter service tests; `GoldenPathTests` save workflow coverage | Packaged app: open `test-pdfs/smoke/scotus-trump-v-anderson.pdf`, add typewriter text on page 1, Save Copy, reopen, verify text is visible and extractable. |
| Highlight selected text and add sticky notes, save, reopen | `AnnotationAuthoringWorkflowTests`; `AnnotationWorkflowServiceTests`; annotation default-appearance rendering tests | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, select text and highlight it, add a sticky note, Save Copy, reopen, verify highlight and note persist. |
| Reorder, rotate, extract, remove, and combine pages | `PageOrganizationWorkflowTests`; `PageOrganizationWorkflowServiceTests`; `PdfDocumentServiceTests` page operations | Packaged app: use `test-pdfs/smoke/scotus-trump-v-us.pdf` plus `test-pdfs/smoke/irs-w4.pdf`, rotate page 1, reorder pages, extract a page, remove a page, combine another PDF, save and reopen. |
| Redact text/area, save redacted copy, verify text removal plus metadata/attachment scrub status | `RedactionMouseWorkflowTests`; `RedactionServiceTests`; `RedactedCopySafetyServiceTests`; `dotnet test --filter "FullyQualifiedName~Redaction"` | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, redact a visible phrase and an area, save redacted copy, reopen, verify copied/extracted text no longer contains the phrase and safety summary reports metadata/attachment scrub status. |
| Audit hidden text and signatures with clear user-facing states | `RevealHiddenTextTests`; `HiddenTextDetectorTests`; `SignatureVerificationServiceTests`; `SignatureVerificationWorkflowServiceTests` | Packaged app: run hidden-text reveal on a generated black-box-redaction fixture; open a signed fixture when available or a generated invalid-signature fixture, verify the signature panel clearly distinguishes valid/invalid/unsupported trust states. |
| Accessibility names, command metadata, keyboard-only reachability, and status announcements | `AccessibilityRegressionTests`; `PdfCommandRegistryTests`; `CommandMetadataCommandTests`; `scripts/run-accessibility-smoke.sh` | Platform review: follow `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md` for macOS AX/VoiceOver, Windows UI Automation, and Linux/GNOME AT-SPI tree checks on dedicated runners. |

The repeatable automated gate for this table is:

```bash
dotnet test PdfEditor.Tests --filter "FullyQualifiedName~GuiWorkflowCoverageMatrix|FullyQualifiedName~GoldenPath|FullyQualifiedName~Workflow|FullyQualifiedName~RevealHiddenText|FullyQualifiedName~SignatureVerification" --logger "console;verbosity=normal"
```

## Issue Hygiene

- Every shipped issue has a completion comment with validation evidence.
- Remaining work stays in GitHub Issues, not TODO comments or roadmap prose.
- Broad epics stay open until all acceptance criteria are done; patch-release issues close when their concrete gate is implemented.

## Release

- Commit with a scoped message.
- Tag with an annotated `v*` tag.
- Push the commit to `origin/main`.
- Push the tag.
- Create or verify the GitHub Release.
- Verify `.sha256` files are present for each release artifact.
