# Release Checklist

Use this checklist before tagging any `v*` release.

## Tagging

```bash
scripts/set-version.sh <version>          # Directory.Build.props + CHANGELOG
git commit -am "chore: <version>"
git tag -a v<version> -m "excise v<version>"
git push origin v<version>
```

`set-version.sh` is the only thing that changes the version, and it writes both
places a human would otherwise edit by hand. Skipping it is not a shortcut: the
pre-push hook refuses a `v*` tag whose version disagrees with
`Directory.Build.props`, and `release.yml`'s preflight checks the same thing before
it builds anything — because a tagged build stamps the assemblies from the TREE, so a
mismatch ships a binary whose About window names a different release. That is
#1627, which shipped "version 1.0.0" for a whole release cycle.

**What a tag builds.** Pushing the tag runs `.github/workflows/release.yml`, which
builds the macOS arm64 zip, the Windows installer and portable zip, and the Linux
`amd64` and `arm64` `.deb` and `.tar.gz` packages, checks each one is what it claims
(a `.deb` is installed and run, the Windows installer is installed silently and launched,
the macOS bundle is inspected, and every package is proven Native AOT by unpacking it and
running `scripts/check-aot-payload.py`), verifies every asset against its `.sha256`, and attaches them to
a **draft** release with notes taken from `CHANGELOG.md`. Nothing is published:
publishing is a manual click. The builds are unsigned by choice (#1597; #1698 is the
open re-decision), and the release notes say so. `workflow_dispatch` is a dry run: it
builds everything for the version in the tree and never touches a release, so run it
before tagging. The v3.15.0 tag workflow completed successfully on all four
packaging legs (run 37044718840, 2026-10-02). That is historical packaging
evidence, not a substitute for inspecting this candidate's dry run and draft.

Beyond that there is no evidence-checking wrapper and no enforced trailer.

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

### Scope the release gate

This checklist is evidence and release mechanics, not a reason to delay
shipping over every available gate. Agree on the candidate, previous release
baseline, and critical user workflows before testing. Reuse current evidence
already collected for that exact candidate. Run the smallest checks that cover
those workflows and the required push/package/version gates; reserve `full` for
when a concrete risk or explicit release decision calls for it.

Block only on a reproducible major user-facing regression, a concrete packaging
or version error, or a data-integrity/security issue in behavior changed by the
candidate. A benchmark regression alone is not a blocker while the app remains
responsive. Redaction suites are mandatory when changing redaction code, but are
not a general release gate for candidates that do not change redaction behavior.
Treat host/tooling failures as validation limitations, use valid focused evidence
to answer the release question, and do not spend time repairing unrelated gates.
Record minor issues in GitHub Issues and proceed once the agreed checks pass.

Every automated gate is declared in `tests/gates.tsv` and described in
`LOCAL_GATES.md`. Choose checks based on the candidate's changed behavior and
the major user workflows at risk; the existence of a gate does not make it a
release blocker.

1. **Check candidate regressions.** Reuse a current passing `t1` run for the
   exact clean candidate when available. Otherwise run focused tests for the
   changed critical workflows and the required pre-push `t0`. Run
   `scripts/test-tier.sh full --fresh` only when a concrete major-risk question
   cannot be answered by focused checks or the user explicitly requests it.
   Redaction suites remain mandatory when changing redaction code, not for
   unrelated releases. Keep the report and state clearly what it proves.

2. **Validate release packages before tagging.** Run the workflow's
   `workflow_dispatch` dry run on the exact pushed candidate SHA and version.
   It builds all supported platform packages and validates their AOT payloads
   and checksums without creating a release. If a local Release-config smoke
   is useful for an affected packaged-app workflow, select only relevant rows
   from `scripts/test-tier.sh --list t2`; the release-smoke runner supports
   `--only` and reports a partial run as such. Do not claim skipped or unrun
   rows passed.

   Example for a local targeted run (replace `<rows>` with relevant names):

   ```bash
   scripts/release-smoke.sh --release-tests --only=<rows> --version <version>
   ```

   `scripts/test-tier.sh t2` is `release-smoke.sh --release-tests` and accepts
   only `--resume`, so the candidate run is the wrapper directly.
   `--release-tests` is Release configuration, which the `signature` and `ui`
   rows are declared for; without it the build and test rows run in Debug —
   fine for a quick investigation, not release evidence. The `build` row
   (`dotnet build excise.sln` in the tier's configuration) is first in every
   tier that builds, restores packages so it is reliable after
   configuration-changing package builds, and is never checkpointed.

3. **Read the report.** Every runner ends with `scripts/report-gates.sh`, and
   its exit code is the runner's. Review every selected row and confirm the
   evidence covers the agreed user workflows:

   ```bash
   scripts/report-gates.sh <candidate-log-directory> --full
   ```

   Do not describe SKIPPED, NOT RUN, or incomplete selected evidence as a pass.
   A relevant row must pass or be resolved with a focused alternative;
   unrelated rows do not expand the scope. A KNOWN row is an accepted, OPEN
   issue; list it in release notes when relevant to shipped workflows. Keep
   the selected run's plan, ledger, report, and logs. See `LOCAL_GATES.md`,
   "The report" for verdict definitions.

4. **Tag** ("Release" below).

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
  | `win-x64` | **Shipped; v3.15.0 packaging verified** | Release run 37044718840 built the Native AOT installer and portable zip, checked unpacked/installed payloads, and passed installed CLI/GUI smoke. Re-run on every candidate. |
  | `linux-x64` | Shipped through v3.8.0; **re-verified 2026-09-20** | Native AOT GUI publish, launch and scenario on a hosted x64 runner, 0 managed `.dll` sidecars (run 35550350097, #1594). `release.yml` rebuilds the AOT `.deb` on every tag, installs it and smokes the CLI. Not verified before that workflow first runs: the `.deb` itself on a runner. |
  | `linux-arm64` | **Shipped; v3.15.0 packaging verified** | Release run 37044718840 built Native AOT GUI/CLI packages natively on arm64 and passed payload/install/CLI smoke. Re-run on every candidate. |
  | `osx-x64` | Deferred (#705) | Not yet probed; needs an Intel-mac (or Rosetta-verified) publish + smoke. |
- **Separate local-suite and hosted-package evidence.** Local full-suite and
  interactive evidence on this machine cover macOS. Hosted Windows/Linux
  package, payload, install and launch checks cover those packaging workflows,
  not the complete test suite or physical-platform manual workflows.
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

### Changed-symbol code-health review (optional, informational)

Not a gate and not in any tier. To see which symbols got worse since a baseline,
capture the candidate commit and run the review as described in
`tests/code-health/review-policy.md` ("Baseline candidates and adoption"). Read the
`reviewRequired` list by rule and symbol; file issues for real findings. Do not
cite the output as a score or as release approval.

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

These classes run in `t1`'s chunked `Excise.App.Tests` row. The separate
`app-tests-unchunked-evidence` row runs only in `full`, retaining serial
cross-test-contamination evidence. Neither replaces the packaged-app review
column. Ad hoc:

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
- **No manual Acrobat step.** It used to be here: produce an R6 and an R4
  sample and open each in Acrobat by hand. Dropped 2026-09-17 (Marc's call),
  because the gate above already covers the catastrophic direction — the wrong
  password AND the absent password are rejected by mutool, qpdf, Ghostscript
  and pdftoppm, and qpdf decodes the `/P` mask independently — so Acrobat added
  a fifth opinion on a property four independent readers already agree on, at
  the cost of the release's only manual step. Spot-check in Acrobat if a user
  ever reports that a protected file opens without its password; do not gate
  the release on it.
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
  `main` is the stable release pointer, nothing more: it only ever advances to
  a release tag, by fast-forward. The pre-push hook enforces exactly that (the
  pushed commit must carry a `v*` tag, the push must be a fast-forward, and
  `main` cannot be deleted) and does not apply the gate-asymmetry range to it,
  because that range is every commit since the last release. If `main` is not
  an ancestor of the release (its history was rewritten before v3.10.0), make
  it one without changing any file, on `develop` before tagging:
  `git merge -s ours origin/main`. That was done for v3.12.0; after it every
  release fast-forwards `main`.
- Create or verify the GitHub Release.
- Verify `.sha256` files are present for each release artifact.
