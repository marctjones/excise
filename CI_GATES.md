# CI Gates for pdfe

This document describes the automated CI gates that protect the quality and performance of the pdfe project.

## Overview

Every push to `main` or `develop` and every pull request is subject to automated quality gates:

1. **Build Gate** - All projects must build with zero errors (warnings are tolerated)
2. **Test Gates** - All unit and integration tests must pass
3. **Coverage Gate** - Pdfe.Core must maintain ≥94% line coverage (v2.1.0-rc baseline)
4. **Performance Signal** - Performance benchmarks run for visibility; release candidates use local long-running gates

## CI Workflows

### Main CI Workflow: `.github/workflows/ci.yml`

**Trigger**: Every push to `main`/`develop`, every pull request

**Duration**: ~5-8 minutes

**Steps**:
1. Checkout code
2. Setup .NET 10 SDK
3. Setup Java 17 (for veraPDF)
4. Install veraPDF for PDF/A compliance testing
5. Install xvfb for headless GUI tests
6. Restore NuGet packages
7. Verify true redaction implementation
8. **Build gate**: `dotnet build pdfe.sln -c Debug`
9. Run Pdfe.Core.Tests with XPlat Code Coverage collection
10. Install reportgenerator tool
11. Generate Cobertura coverage report
12. **Coverage gate**: Parse coverage XML and verify Pdfe.Core line-rate ≥ 0.94
13. Run Pdfe.Cli.Tests
14. Run Pdfe.Rendering.Tests (excluding slow Corpus tests)
15. Run PDF 2.0 renderer conformance gate
16. Run PerformanceBenchmarkTests and MemoryBenchmarkTests (informational)
17. Run Pdfe.Ocr.Tests (non-blocking)
18. Run PdfEditor.Tests with xvfb (GUI tests)
19. Upload coverage report as artifact
20. Upload test results on failure

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

Before pushing, simulate the CI gates locally:

```bash
./scripts/ci-test.sh
```

This runs:
- Build (Debug)
- Pdfe.Core.Tests with coverage
- Coverage gate check
- Other test projects
- PDF 2.0 renderer conformance gate
- Performance benchmarks

**Runtime**: ~3-5 minutes locally

**Output**: Colored pass/fail summary, detailed log saved to `ci-test.log`

### Manual Coverage Check

To check coverage for a specific package:

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

# Check coverage
scripts/check-coverage.sh coverage/report/coverage.cobertura.xml 0.94 Pdfe.Core
```

## Coverage Baseline (v2.1.0-rc)

**Pdfe.Core Line Coverage**: 94.41%

This is the baseline locked in v2.1.0-rc. The CI gate enforces ≥94% to prevent regressions while allowing minor fluctuations from test variations.

The gate specifically targets `Pdfe.Core` (the core PDF parsing and redaction engine) rather than the entire solution, because:
- This is the security-critical component
- Pdfe.Cli, PdfEditor, and other components have different coverage baselines
- New features may add untested code; coverage is a project-level metric, not per-component lock

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

| Project | Purpose | Count | Timeout | CI Behavior |
|---------|---------|-------|---------|-------------|
| Pdfe.Core.Tests | Core PDF parsing, redaction | ~2400 tests | 5s | Full run, coverage collected |
| Pdfe.Cli.Tests | CLI application | ~74 tests | 30s | Full run |
| Pdfe.Rendering.Tests | PDF rendering (Skia) | ~200 tests | 60s | Run except Corpus (slow) |
|  | Performance benchmarks | ~20 tests | 120s | Enforces thresholds |
| Pdfe.Ocr.Tests | OCR integration (Tesseract) | ~30 tests | 60s | Non-blocking |
| PdfEditor.Tests | GUI and integration tests | ~600+ tests | 120s (blame-hang) | Headless via xvfb |

## Failure Diagnosis

### Build fails
- Check for CS8625 or other null reference warnings in error output
- May indicate code that needs null-coalescing or null-forgiving operators
- See `/home/marc/Projects/pdfe/CLAUDE.md` for build warning standards

### Coverage gate fails
- Coverage dropped below 94% for Pdfe.Core
- Check which files lost coverage with: `scripts/check-coverage.sh coverage/report/coverage.cobertura.xml 0.94 Pdfe.Core`
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
# Check entire solution >= 85%
scripts/check-coverage.sh coverage/report/coverage.cobertura.xml 0.85

# Check Pdfe.Core >= 94%
scripts/check-coverage.sh coverage/report/coverage.cobertura.xml 0.94 Pdfe.Core
```

Exit codes:
- 0: Coverage met or exceeded threshold
- 1: Coverage below threshold, or file not found

### `scripts/ci-test.sh`

Simulates the full CI workflow locally.

```bash
./scripts/ci-test.sh
```

Output:
- Colored summary of each gate (PASSED / FAILED)
- Detailed log in `ci-test.log`
- Exit code 0 if all gates pass, 1 if any fail

## GitHub Integration

- CI status appears as a check on every PR
- Required status checks are configured in branch protection rules
- Failure blocks merge until all gates pass

## Next Steps / Future Work

- [ ] **Windows/Mac CI matrices** (v2.2 work) - Add `windows-latest` and `macos-latest` to CI matrix once platform-specific issues are resolved
- [ ] **Corpus testing outside PR gates** - Keep full corpus runs in local or dedicated infrastructure because they are too slow for every PR
- [ ] **Sonarqube integration** - Optional: code quality scanning
- [ ] **Flaky test detection** - Track test failure patterns across runs
