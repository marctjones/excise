# Release Checklist

Use this checklist before tagging any `v*` release.

## Tagging

```bash
git tag -a v<version> -m "excise v<version>"
git push origin v<version>
```

That is all. There is no evidence-checking wrapper and no enforced trailer.

There used to be: `scripts/tag-release.sh` wrote `Release-Evidence:` trailers
into the annotated tag, and the pre-push hook refused any `v*` tag that lacked
them. Both are deleted. The reasons, so this does not get rebuilt:

- **No tag ever had one.** v3.6.0, v3.7.0 and v3.8.0 all carry zero
  `Release-Evidence` trailers — every real release was tagged the way the hook
  forbade, so the guard only ever blocked the correct path.
- **The script's happy path never ran.** Two bugs were found in it by reading
  rather than running, including one where it could record
  `Release-Evidence-Steps: 0` as its own evidence.
- **Nobody knew what a trailer was.** Including the person the receipts were
  for. A record no one reads is not a record.

What was actually worth keeping is the checklist below: run the gates before
you tag, and look at the results yourself.

## Documentation Accuracy

- Run `scripts/check-doc-claim-freshness.sh` (re-derives the numeric claims in CLAUDE.md; the `doc-claim-freshness` row, so a green `t0` already covers it).
- Confirm README feature bullets match implemented commands, menu items, CLI commands, and public APIs.
- Confirm `Excise.Core/README.md`, `Excise.Rendering/README.md`, and `Excise.Avalonia/README.md` describe the current library APIs.
- Confirm release notes do not imply future issue scope is already shipped.
- If a behavior change touches redaction, signatures, metadata, attachments, or forms, update implementation, UI text, tests, and docs in the same change.

## Validation

Every gate excise runs is a row of `tests/gates.tsv` (`LOCAL_GATES.md`), and
**the rows a release candidate runs come from the manifest** —
`scripts/test-tier.sh --list t2` prints them. This document does not repeat
the list: it drifted every time it did, and the last copy had one row's
verdict wrong. What is left here is the procedure a human runs, in order, and
the decisions no row can make.

1. **`t1` green on the candidate commit.** `t2` is a curated Release-config
   set, NOT a superset of `t1` — compare `scripts/test-tier.sh --list t1` with
   `--list t2`. A row that lives only in `t1` is implied neither by a `t2`
   pass nor by a `full` pass older than the candidate. The blast-radius rule
   and the tier table are in `CLAUDE.md`, "Test Tiers".

   ```bash
   scripts/test-tier.sh t1
   ```

2. **The release-candidate run, on an otherwise idle machine.**

   ```bash
   scripts/release-smoke.sh --release-tests --visual --package --packaged-gui --aot --version <version>
   ```

   `scripts/test-tier.sh t2` is `release-smoke.sh --release-tests` and accepts
   only `--resume`, so the candidate run is the wrapper directly.
   `--release-tests` is Release configuration, which the `signature` and `ui`
   rows are declared for; without it the build and test rows run in Debug
   (Release excludes the developer scripting surface by default) — fine for a
   quick investigation, not release evidence. The `build` row
   (`dotnet build excise.sln` in the tier's configuration) is first in every
   tier that builds, restores packages so it is reliable after
   configuration-changing package builds, and is never checkpointed.

   The flag-gated rows declare `opt:NAME` as a prerequisite, so a run without
   `--visual`, `--package`, `--packaged-gui` or `--aot` shows `visual`,
   `package`, `packaged-gui` and `aot` as **SKIPPED**, never as silence
   (`--package` implies `aot` unless `--no-aot`). `--quick` skips only the
   `tests` row, visibly; every other `t2` row still runs. `--only=a,b` runs
   named rows (a flag-gated row named there runs as if its flag had been
   passed) and reports the run PARTIAL. Neither is a candidate run.

   Idle machine, because `Excise.App.Tests` is serial by design (SkiaSharp's
   process-wide native font manager, #363) and its 144-page display sweep is
   load-sensitive: concurrent work — or a bloated `logs/` + `artifacts/` tree
   — has produced **false reds** with zero page failures (#619). A DEADLINE
   from the sweep is a TIME limit, not a correctness failure; shard it rather
   than ignore it (`scripts/run-gui-display-sweep.sh 4`, tooling, not a row).

3. **Read the report.** Every runner ends with `scripts/report-gates.sh`, and
   its exit code is the runner's. Re-print the candidate run with every row:

   ```bash
   scripts/test-tier.sh --report --latest --full
   ```

   The verdict must be bare `PASS`: no NEW red, no STALE acceptance (a
   `knownIssue` whose GitHub issue has since closed), no NOT RUN (an
   interrupted run), no SKIPPED. A `visual` group that skipped for a missing
   prerequisite exits 77, and because the row is `prereqPolicy=skip` that is a
   visible SKIPPED row, counted as `PASS with N SKIPPED` — not release
   evidence; the candidate run must show every flag-gated row PASS. The
   `accessibility` row (tier full,t2) is SKIPPED by default too: its platform
   probe runs only with `EXCISE_ACCESSIBILITY_ALLOW_PLATFORM_PROBE=1` set AND
   macOS Accessibility permission granted to the terminal running it (System
   Settings → Privacy & Security → Accessibility), so set both for the
   candidate run. A KNOWN
   row is an accepted, OPEN issue — the current acceptances are the
   KNOWN-ISSUE column of `--list <tier>`; list each in the release notes as a
   known limitation. The run directory (`logs/release-smoke_<stamp>/` with
   `plan.tsv`, `ledger.jsonl`, `report.json` and every row's log) is the
   release evidence; keep it. Verdicts, exit codes and the report layout:
   `LOCAL_GATES.md`, "The report".

4. **The manual Acrobat step** under "Encryption Evidence" below — Acrobat is
   not scriptable here and is deliberately not faked in the automated gate.

5. **Tag** ("Release" below).

The decisions no row can make:

- **AOT support matrix (#595, decided 2026-07-20)** — release notes and docs
  must not claim AOT targets beyond this table; update the table (and file the
  probe evidence) before promoting any RID. The `aot` row
  (`scripts/run-aot-smoke.sh`) runs in `full` and in `t2` with `--aot`; on
  2026-08-31 it FAILED with no `knownIssue`, and it reads NEW in every `full`
  report until it is fixed or an issue is filed and cited —
  `scripts/test-tier.sh --report --latest` has the current verdict.

  | RID | Status | Reason |
  |-----|--------|--------|
  | `osx-arm64` | **Shipped** | Validated by the `aot` row (`run-aot-smoke.sh` evidence); the per-PR Native AOT CI lane that used to corroborate it was removed with Actions on 2026-09-04. |
  | `win-x64` | Deferred (#703) | Not yet probed; needs a Windows publish + native-asset load check, and there is no Windows runner. |
  | `linux-x64` | Shipped through v3.8.0; **unverifiable since 2026-09-04** | The evidence was the deleted release workflow's Native AOT `.deb` build plus CLI smoke and a `0` managed `.dll` sidecars check. There is no Linux runner today (`LOCAL_GATES.md`) — do not claim it in new release notes until the packaging issue lands. |
  | `osx-x64` | Deferred (#705) | Not yet probed; needs an Intel-mac (or Rosetta-verified) publish + smoke. |
- **macOS only.** This release is validated on this machine; Linux and
  Windows are untested this release. `t3` prints that reminder after `t2`;
  restoring Actions for Linux/Windows packaging only is a separate issue
  (`LOCAL_GATES.md`).
- **Coverage is an OBSERVATION, not a gate**, and deliberately not a release
  blocker: blocking a tag on a coverage number invites lowering the number to
  ship — the same asymmetry `check-gate-asymmetry.sh` exists to prevent for
  perf-vs-correctness. There is one profile, `full` (corpora and reference
  tools present, unfiltered); the `ci` profile was deleted with GitHub Actions
  on 2026-09-04, and the lesson it taught survives it: a filter is not an
  environment, so never read a number for one environment while standing in
  another. `scripts/check-coverage-floor.sh` is tooling
  (`tests/gates-tooling.txt`), `coverage-floor-selftest` in `t0` keeps the
  never-lowers ratchet mechanism honest (#909), and wiring the floors into a
  tier is #1359. To look:

  ```
  dotnet test Excise.Rendering.Tests -c Debug --collect:"XPlat Code Coverage" \
      --results-directory cov/
  scripts/check-coverage-floor.sh $(find cov -name coverage.cobertura.xml | head -1) \
      full Excise.Rendering
  ```
- **Rendering quality is declared final only after `full`.** The four
  `corpus-scan-*` rows are the conformance GRADE the report prints; review
  `PASS` / `PASS_ONE` / `DIFF` / classified non-fidelity counts and ensure the
  remaining blockers are fixed, issue-linked, or documented as accepted
  limitations. Never pin a scan result without
  `scripts/triage-corpus-nonpass.sh` first (CLAUDE.md); the tmux wrapper
  (`scripts/run-exploratory-corpus-tmux.sh -- --page-mode all
  --pdf-timeout-ms 120000 --chunk-parallel 2 --per-chunk-parallel 1`) remains
  for a human watching one corpus.
- **A font or text-extraction change (#513–#515)** goes through
  `extraction-parity` before merging — a font-resolver change either improves
  the delta or it is rejected. `--update` on either parity script rewrites its
  baseline from the current measurement; review the diff before committing.
- **The focused tests for the changed area**, ad hoc:
  `scripts/t.sh <project> --filter …`.

## Everyday PDF Workbench RC Matrix

This matrix is the final-release gate for issue #490. Every row needs at least
one automated gate, scripted smoke, or explicit manual packaged-app step with a
named fixture. If a row fails during release-candidate testing, create or link a
GitHub issue and either fix it or list it in final release notes as an accepted
limitation before tagging.

| Workflow | Automated or scripted gate | Fixture/manual RC step |
| --- | --- | --- |
| Open PDFs from Finder/Explorer/open-with and from the app | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `GuiWorkflowCoverageMatrixTests`; packaging file-association doc claim tests | Packaged app: open `test-pdfs/smoke/irs-w9.pdf` by app picker/open-with and from File > Open. |
| Navigate long PDFs, thumbnails, page labels, zoom, fit width/page | `PdfViewerControlTests`; `ThumbnailCacheTests`; `OutlineTreeNavigationTests`; `PdfPageLabelTests` | Packaged app: open `test-pdfs/smoke/irs-1040-instructions.pdf`, jump first/middle/last pages, toggle thumbnails/outline, verify page labels, Fit Width, Fit Page, zoom in/out. |
| Search, select text, copy text | `GoldenPathTests.GoldenPath_OpenSearchNavigateClose`; `TextSelectionDragTests`; `PdfSearchServiceTests`; `SearchHighlightOverlayTests`; `RealWorldSearchTests` | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, search `syllabus`, select a sentence, copy, and paste into a plain-text editor. |
| Fill common forms, save filled copy, reopen, verify values persisted | `FormWorkflowTests`; `FormFieldsOverlayTests`; `PdfDocumentServiceTests` save/load coverage | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, fill text fields and a checkbox/radio where available, Save Filled Copy, reopen in excise, verify field values remain editable. |
| Flatten form copy, reopen, verify static output | `FormWorkflowTests`; `Excise.Core.Tests.Document.AcroFormReadOnlyTests`; form flattening core tests | Packaged app: use `test-pdfs/smoke/irs-w9.pdf`, Flatten Form, reopen in excise, verify values are visible static page content and no inline field editor appears for flattened values. |
| Add typewriter text to flat PDF, save copy, reopen | `TypewriterWorkflowTests`; typewriter service tests; `GoldenPathTests` save workflow coverage | Packaged app: open `test-pdfs/smoke/scotus-trump-v-anderson.pdf`, add typewriter text on page 1, Save Copy, reopen, verify text is visible and extractable. |
| Highlight selected text and add sticky notes, save, reopen | `AnnotationAuthoringWorkflowTests`; `AnnotationWorkflowServiceTests`; annotation default-appearance rendering tests | Packaged app: open `test-pdfs/smoke/scotus-trump-v-us.pdf`, select text and highlight it, add a sticky note, Save Copy, reopen, verify highlight and note persist. |
| Reorder, rotate, extract, remove, and combine pages | `PageOrganizationWorkflowTests`; `PageOrganizationWorkflowServiceTests`; `PdfDocumentServiceTests` page operations | Packaged app: use `test-pdfs/smoke/scotus-trump-v-us.pdf` plus `test-pdfs/smoke/irs-w4.pdf`, rotate page 1, reorder pages, extract a page, remove a page, combine another PDF, save and reopen. |
| Redact text/area, save redacted copy, verify text removal plus metadata/attachment scrub status | `RedactionMouseWorkflowTests`; `RedactionServiceTests`; `RedactedCopySafetyPolicyTests`; `dotnet test --filter "FullyQualifiedName~Redaction"` | Packaged app: open `test-pdfs/smoke/irs-w9.pdf`, redact a visible phrase and an area, save redacted copy, reopen, verify copied/extracted text no longer contains the phrase and safety summary reports metadata/attachment scrub status. |
| Audit hidden text and signatures with clear user-facing states | `RevealHiddenTextTests`; `HiddenTextDetectorTests`; `SignatureVerificationServiceTests`; `SignatureVerificationWorkflowServiceTests` | Packaged app: run hidden-text reveal on a generated black-box-redaction fixture; open a signed fixture when available or a generated invalid-signature fixture, verify the signature panel clearly distinguishes valid/invalid/unsupported trust states. |
| Accessibility names, command metadata, keyboard-only reachability, and status announcements | `AccessibilityRegressionTests`; `PdfCommandRegistryTests`; `CommandMetadataCommandTests`; `scripts/run-accessibility-smoke.sh` | Platform review: follow `docs/ACCESSIBILITY_RELEASE_CHECKLIST.md` for macOS AX/VoiceOver on this machine (macOS only; other platforms untested this release). |
| CLI automation, batch JSON, progress events, and platform wrappers | `BatchAutomationCommandTests`; `CommandMetadataCommandTests`; `scripts/run-automation-smoke.sh` | Platform review: follow `docs/AUTOMATION_API.md` examples for AppleScript/Shortcuts on this machine (macOS only; other platforms untested this release). |
| UX/icon visual polish, toolbar/menu affordances, and design-quality screenshots | `VisualPolishAuditTests`; `scripts/run-ux-icon-audit.sh` | Review the generated `ux-icon-audit.md`, PNG screenshots, and `ux-icon-audit.json` before closing visual-polish issues. |
| Benchmark speed, reference fidelity, redaction completeness, and renderer hotspot evidence | `BenchmarkSuiteTests`; `scripts/run-benchmarks.sh suite`; `Excise.RenderTools benchmark-suite`; `Excise.Rendering.Tests` performance/memory tests | Review `benchmark-report.md`, `benchmark-report.json`, `benchmark-pages.csv`, `benchmark-hotpaths.json`, `latest-performance-baseline.md`, and aggregate `corpus-hotspots`, `gui-display-hotspots`, and `gui-workflow-hotspots` reports before closing performance issues. |
| Native AOT app packaging, warning budget, and symbol split | `scripts/run-aot-smoke.sh`; `scripts/release-smoke.sh --quick --only=aot`; optional `scripts/run-aot-smoke.sh --gui-smoke` on an interactive macOS runner | Review `aot-smoke.md`, `aot-smoke.json`, `aot-warnings.txt`, package size, symbol archive size, and any packaged GUI smoke evidence before shipping an AOT artifact. |

These classes run inside the `t1` `app-tests-unchunked-evidence` row (the
unfiltered `Excise.App.Tests` pass), so a green `t1` on the candidate commit
is the repeatable gate for this table. Ad hoc:

```bash
scripts/t.sh Excise.App.Tests/Excise.App.Tests.csproj --filter "FullyQualifiedName~GuiWorkflowCoverageMatrix|FullyQualifiedName~GoldenPath|FullyQualifiedName~Workflow|FullyQualifiedName~RevealHiddenText|FullyQualifiedName~SignatureVerification"
```

## Encryption Evidence (#644)

The encryption writer's release evidence is the interop gate suite — excise
must never be its own oracle for "this file is actually protected." A
mis-emitted `/Encrypt` dictionary that some reader silently ignores (opening
the "protected" file without a password) is the catastrophic failure mode
this section exists to catch.

- Run the automated gate on a machine with the reference tools installed
  (mutool, qpdf, ghostscript, pdftoppm). It is the `encryption-interop-gate`
  row in `t1`, so a green `t1` on the candidate commit already covers it; by
  hand:

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
