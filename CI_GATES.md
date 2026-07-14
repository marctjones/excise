# CI Gates for pdfe

This document describes the automated CI gates that protect the quality and performance of the pdfe project.

## Overview

Every push to `main` or `develop` and every pull request is subject to automated quality gates:

1. **Build Gate** - All projects must build with zero errors (warnings are tolerated)
2. **Test Gates** - All unit and integration tests must pass
3. **Coverage Gate** - Pdfe.Core must maintain the line-coverage threshold set in `.github/workflows/ci.yml`'s "Coverage Gate" step. Don't hard-code the percentage here — it ratchets over time (most recently tracked in #603) and this doc goes stale; that file does not.
4. **Performance Signal** - Performance benchmarks run for visibility; release candidates use local long-running gates

## CI Workflows

### Main CI Workflow: `.github/workflows/ci.yml`

**Trigger**: Every push to `main`/`develop`, every pull request

Don't hand-copy its step list here — it has drifted from the real file
before (this doc once listed 20 steps and was missing three real gates:
`check-gate-asymmetry.sh`, `check-skip-budget.sh`,
`verify-true-redaction.sh`). Read `.github/workflows/ci.yml` directly, or run
the equivalent locally with `scripts/test-tier.sh t1` (#646) — that script
and the workflow are kept in step by construction (`scripts/ci-test.sh` is
now a thin wrapper around `test-tier.sh t1`, not a second hand-maintained
copy). See `CLAUDE.md`'s "Test Tiers" section for the full T0-T3 table.

CI currently runs on Linux only (`ubuntu-latest`) — macOS- and Windows-specific
code (`PdfEditor/Views/MacNativeMenuBuilder.cs`, `.app` bundle behavior,
Windows installer/file associations) is untested in CI (#647, tracked and
in progress). Check `.github/workflows/ci.yml`'s `jobs:` keys for the
current, authoritative platform coverage rather than trusting this note.

### PDF 2.0 Renderer Conformance Gate

**Trigger**: Every CI run, and release smoke via the `pdf20` gate

**Command**:
```bash
scripts/run-pdf20-renderer-conformance.sh --run-tests
```

This fixture-based gate is deterministic in CI. It regenerates tiny PDF 2.0
image/filter fixtures, verifies the full image/filter matrix, validates the
curated PDF 2.0 renderer matrix, checks explicit rendering-contract corpus
evidence, and runs focused matrix guard tests. Large local corpus inventories
can be merged manually with:

```bash
scripts/run-pdf20-renderer-conformance.sh --include-base-image-inventory
```

### Local Visual Regression Runner: `scripts/run-visual-regression-local.sh`

**Trigger**: Manual, before release candidates or while investigating rendering changes

**Duration**: Environment-dependent

**Purpose**: Detect environment-sensitive rendering and UI baseline regressions without spending paid GitHub-hosted runner time or making PR checks flaky

**Steps**:
1. Build locally in Debug by default, or Release with `--release`
2. Run Pdfe.Rendering.Tests with filter `FullyQualifiedName~Visual`
3. Run differential smoke tests when `mutool` and `test-pdfs/smoke` are present
4. Run PdfEditor.Tests baseline tests with filter `FullyQualifiedName~MatchesBaseline`
5. Collect PNG artifacts (`*-actual.png`, `*-diff.png`, `*-triptych.png`)
6. Write logs and copied artifacts under `logs/visual-regression_<timestamp>/`

```bash
scripts/run-visual-regression-local.sh
scripts/run-visual-regression-local.sh --release
scripts/run-visual-regression-local.sh --only=render-visual-baselines,gui-headless-baselines
```

## Local Testing

Before pushing, run the tier that matches your change's blast radius
(`CLAUDE.md`'s "Test Tiers" section, #646):

```bash
scripts/test-tier.sh t0   # ~30s, before every push
scripts/test-tier.sh t1   # ~10m, what CI blocks a PR on — scripts/ci-test.sh is a thin wrapper around this
scripts/test-tier.sh t2   # ~30m, release candidate (release-smoke.sh)
```

### Manual Coverage Check

To check coverage for a specific package (substitute the threshold from
`.github/workflows/ci.yml`'s "Coverage Gate" step — don't hard-code it here):

```bash
# Run tests and collect coverage
dotnet test Pdfe.Core.Tests --collect:"XPlat Code Coverage" --results-directory coverage/

# Install reportgenerator if not already done
dotnet tool install --tool-path ./tools dotnet-reportgenerator-globaltool

# Generate report
./tools/reportgenerator \
  -reports:coverage/*/coverage.cobertura.xml \
  -targetdir:coverage/report \
  -reporttypes:Cobertura

# Check coverage (reportgenerator's Cobertura output; note the filename is
# Cobertura.xml, not coverlet's raw coverage.cobertura.xml)
scripts/check-coverage.sh coverage/report/Cobertura.xml <threshold-from-ci.yml> Pdfe.Core
```

## Coverage Gate

The gate targets `Pdfe.Core` specifically (the core PDF parsing and
redaction engine), not the whole solution, because:
- It's the security-critical component.
- Pdfe.Cli, PdfEditor, and other components have different coverage profiles.
- New features may add untested code; coverage is a project-level metric, not a per-component lock.

The enforced threshold ratchets over time (most recently #603) — read it
from `.github/workflows/ci.yml`'s "Coverage Gate" step rather than trusting
a number in this doc; every prior version of this section has gone stale.

## Performance Benchmarks

The `PerformanceBenchmarkTests` class in `Pdfe.Rendering.Tests` defines performance thresholds for rendering operations:

```csharp
[Theory]
[InlineData("irs-w9.pdf", 1, 800)]     // max 800ms per render
[InlineData("irs-1040.pdf", 1, 800)]   // ...
// ... etc
```

These tests:
- Run on every CI build
- Fail the build if any benchmark exceeds its threshold
- Are calibrated for Ubuntu 26.04 / .NET 10 / Skia 2.88.9 reference hardware
- Thresholds are intentionally loose (2-3× margin) to avoid flakiness

If CI performance tests fail unexpectedly:
1. Check if the test agent is under heavy load
2. Check if hardware has changed
3. Adjust thresholds in the test file if needed (with justification in commit message)

## Headless GUI Testing

PdfEditor.Tests uses Avalonia UI and requires a display. On headless CI:

```bash
xvfb-run -a dotnet test PdfEditor.Tests --no-build -c Debug
```

**Installation requirement for local testing**:
```bash
# Ubuntu/Debian
sudo apt-get install -y xvfb

# Or use a virtual framebuffer directly
Xvfb :99 -screen 0 1024x768x24 > /dev/null 2>&1 &
export DISPLAY=:99
dotnet test PdfEditor.Tests --no-build -c Debug
```

## Test Project Summary

Don't hard-code test counts here — they go stale immediately (this table
previously said "~2400" for Pdfe.Core.Tests and "~600+" for PdfEditor.Tests;
both are now off by hundreds of tests). Run the suites, or see
`CLAUDE.md`'s "Test Infrastructure" section, which carries the same warning
rather than a number that will be wrong by the time you read it.

| Project | Purpose | CI Behavior |
|---------|---------|-------------|
| Pdfe.Core.Tests | Core PDF parsing, redaction | Full run, coverage collected |
| Pdfe.Cli.Tests | CLI application | Full run |
| Pdfe.Rendering.Tests | PDF rendering (Skia) | Deterministic filter only (Corpus/Differential/Benchmark/Visual excluded — see `ci.yml`'s comment for why) |
| Pdfe.Ocr.Tests | OCR integration (Tesseract) | Non-blocking |
| PdfEditor.Tests | GUI and integration tests | Headless via xvfb; serial by design (#363), skipped for library-only PRs, always run on `main` |

## Failure Diagnosis

### Build fails
- Check for CS8625 or other null reference warnings in error output
- May indicate code that needs null-coalescing or null-forgiving operators
- See `CLAUDE.md`'s "Build Warnings" section for build warning standards

### Coverage gate fails
- Coverage dropped below the threshold set in `.github/workflows/ci.yml`'s "Coverage Gate" step
- Check which files lost coverage with: `scripts/check-coverage.sh coverage/report/Cobertura.xml <threshold> Pdfe.Core`
- May need to add tests to new code or remove untested dead code

### Performance benchmarks fail
- A render operation exceeded its threshold
- Check if new feature was added that impacts rendering speed
- Increase threshold if intentional (with justification)
- Or optimize the code path to stay within threshold

### GUI tests fail (PdfEditor.Tests)
- May be display/threading issues in headless environment
- Check test output for "X11 connection refused" or "cannot open display"
- Try running locally with: `xvfb-run -a dotnet test PdfEditor.Tests --no-build -c Debug`

### Local visual regression fails
- A rendering output changed (possible regression)
- Check `logs/visual-regression_latest/`
- Review PNG diff files to see what changed
- If intentional, update baseline images and commit

## Artifacts

On each CI run, the following artifacts are uploaded:

- **coverage-report/** - HTML and Cobertura coverage report (30-day retention)
- **test-results/** - .trx files if tests fail (7-day retention)

These can be downloaded from the GitHub Actions Run summary page.

Local visual-regression runs store logs and copied PNG artifacts under
`logs/visual-regression_<timestamp>/` and refresh the
`logs/visual-regression_latest` symlink when supported by the filesystem.

## Scripts Reference

### `scripts/check-coverage.sh`

Parses a Cobertura XML coverage report and verifies line coverage meets a minimum threshold.

```bash
scripts/check-coverage.sh <path-to-Cobertura.xml> <min-line-rate> [package-filter]

# e.g. check Pdfe.Core against whatever ci.yml currently enforces
scripts/check-coverage.sh coverage/report/Cobertura.xml 0.93 Pdfe.Core
```

Exit codes:
- 0: Coverage met or exceeded threshold
- 1: Coverage below threshold, or file not found

### `scripts/test-tier.sh` (#646)

The single entry point for "what do I run before X" — see `CLAUDE.md`'s
"Test Tiers" section. `scripts/ci-test.sh` is a thin wrapper around
`test-tier.sh t1` kept for backward compatibility; both stay in sync by
construction rather than by hand.

## GitHub Integration

- CI status appears as a check on every PR
- Required status checks are configured in branch protection rules
- Failure blocks merge until all gates pass

## Corpus Testing Outside PR Gates

Full corpus runs (veraPDF, poppler, pdf.js, malformed/adversarial files —
#648) are too slow for every PR and stay in local/release-gate scripts
(`scripts/run-corpus-tests.sh`, `scripts/check-extraction-parity.sh`, and
similar) rather than `ci.yml`. See `docs/RELEASE_CHECKLIST.md` for what runs
at T2/T3 that doesn't run on every PR.
