# Exploratory Differential Reports

This directory holds merged JSON reports from running real-world corpus
PDFs through pdfe with two reference oracles (`mutool draw` and
`pdftocairo`).

```bash
./scripts/run-exploratory-corpus.sh --page-mode first
```

Each entry records pdfe's render result for one page, plus the
per-oracle diff metrics. Page-1 reports therefore have one entry per PDF;
sampled and exhaustive reports have multiple entries per PDF.

Entries also include timing diagnostics when produced by current tooling:
`elapsedMs` for the page, `pdfElapsedMs` for the whole PDF, and per-phase
`renderMs`, `mutoolMs`, and `cairoMs`. Failures include `errorPhase`
(`open`, `render`, `mutool`, `pdftocairo`, `compare`, or `scan`) and may
include a `diagnostic` snapshot for wall-clock timeouts. The merge script
prints the slowest pages and failure diagnostics into the run log.

| Status | Meaning |
|---|---|
| `PASS` | pdfe matches both mutool and pdftocairo within thresholds |
| `PASS_ONE` | pdfe matches one oracle but the oracles disagree with each other (engine-specific quirk, not a clear pdfe bug) |
| `DIFF` | pdfe disagrees with both oracles (real rendering bug) |
| `PARSE_ERROR` | pdfe can't open the PDF (parser limitation) |
| `DECODE_ERROR` | pdfe opened the PDF but a stream, image, or content decode path failed during rendering |
| `TIMEOUT` | per-PDF wall-clock budget exceeded |
| `MUTOOL_REFUSED` / `ALL_ORACLES_REFUSED` | reference engines can't render either |
| `EMPTY_DOC` | 0 pages |
| `COMPARE_ERROR` | bitmap-comparison crashed |
| `SCANNER_CRASH` | a one-PDF recovery subprocess exited before writing a report |

## Test layers

The rendering harness deliberately has separate layers:

| Layer | Command | What it answers |
|---|---|---|
| Fast trend scan | `./scripts/run-exploratory-corpus.sh --page-mode first` | Broad corpus signal, one page per PDF |
| Sampled multi-page scan | `./scripts/run-exploratory-corpus.sh --page-mode sample` | Common later-page regressions without full corpus cost |
| Exhaustive release scan | `./scripts/run-exploratory-corpus.sh --page-mode all` | Every page of every corpus PDF against both visual oracles |

The default remains `first` so local iteration stays fast. Release
validation should use `sample` while investigating and `all` before
declaring rendering quality final.

## Corpus Tiers

| Corpus | Downloader | Default path | Purpose |
|---|---|---|---|
| Smoke government corpus | `scripts/download-smoke-corpus.sh` | `test-pdfs/smoke` | Small gating set used by unit/differential tests. Keep this stable so normal test runs do not gain surprise fixtures. |
| Federal everyday corpus | `scripts/download-federal-corpus.sh` | `test-pdfs/federal` | Manifest-driven release-quality corpus with source URL, category, agency, license basis, page count, and SHA-256 for each official `.gov` PDF. |
| pdf.js corpus | `scripts/download-pdfjs-corpus.sh` | `test-pdfs/pdfjs` | Broad bug-reproduction corpus for exploratory fidelity scans. |

The federal everyday corpus manifest lives at
`test-pdfs/manifests/federal-everyday-corpus.json`. It currently includes IRS
tax forms/publications, State Department passport forms, USCIS I-9, CMS-40B,
CDC public-health material, and SCOTUS opinions. The legal basis is recorded per
entry as official U.S. federal government work under 17 USC 105.

Download or refresh it with:

```bash
./scripts/download-federal-corpus.sh
```

For release-candidate rendering checks against this tier, run the CLI scanner
directly so the output path and timeout are explicit:

```bash
dotnet run --project Pdfe.Cli/Pdfe.Cli.csproj -c Debug -- \
    corpus-scan test-pdfs/federal \
    --output logs/federal-corpus-report.json \
    --page-mode sample \
    --dpi 72 \
    --parallel 2 \
    --pdf-timeout-ms 30000
```

## Latest snapshot

See `exploratory-report.json` for the full data; below is the headline
breakdown from the 2026-06-15 run against the current pdf.js corpus
download. This snapshot used `--page-mode first`, so the count is one
page result per PDF.

| Status | Count | % of 682 |
|---|---|---|
| PASS | 468 | 68.6% |
| DIFF | 130 | 19.1% |
| PASS_ONE | 48 | 7.0% |
| MALFORMED_PDF | 21 | 3.1% |
| DECODE_ERROR | 6 | 0.9% |
| UNSUPPORTED_ENCRYPTED | 3 | 0.4% |
| UNSUPPORTED_COMPRESSION | 2 | 0.3% |
| TIMEOUT | 1 | 0.1% |
| INVALID_PAGE_GEOMETRY | 1 | 0.1% |
| ALL_ORACLES_REFUSED | 1 | 0.1% |
| RESOURCE_LIMIT | 1 | 0.1% |

**Top failure clusters in `DIFF` (130):**

* **76 (58%) — `bitmap-*` JBIG2 fixtures.** pdfe doesn't have a JBIG2
  decoder yet, so these all render blank or near-blank. One missing
  feature → half the rendering gap. Implementing JBIG2 would close
  this entire cluster.
* **36 — `issue*` repros.** Assorted from pdf.js's GitHub issue
  tracker. Many are font/encoding edge cases, transparency
  combinations, or color-space ambiguities.
* **8 — `bug*` Mozilla Bugzilla repros.** Same shape as `issue*`.
* **7 other** — mixed forms/text/vector cases that need fixture-level triage.
* **2 shading/gradient** and **1 mask/image** — narrow remaining rendering
  clusters after the JPX/Lab fixes.

**Non-visual failures (36):**

* 21 `MALFORMED_PDF` — malformed headers/xrefs, invalid seek offsets,
  generation-number parsing, overflow, and fuzzed stream syntax.
* 6 `DECODE_ERROR` — render-time parser/decode limitations such as unresolved
  stream `/Length` references and malformed content streams.
* 3 `UNSUPPORTED_ENCRYPTED` — password-protected files or encryption variants
  outside current unattended rendering support.
* 2 `UNSUPPORTED_COMPRESSION` — compressed archive methods outside the current
  input reader.
* 1 each: `TIMEOUT` (`bomb_giant.pdf`, pdftocairo oracle), `RESOURCE_LIMIT`,
  `INVALID_PAGE_GEOMETRY`, and `ALL_ORACLES_REFUSED`.

**`PASS_ONE` (48) — pdfe sides with:**

These are **not clear pdfe bugs** — they're cases where the two
reference engines disagree among themselves, or one reference renderer
refuses while pdfe can still be compared against the other.

## Re-running

The chunked driver runs in ~3 minutes wall-clock with conservative
parallelism on a 30 GB / 8-core machine:

```bash
./scripts/run-exploratory-corpus.sh \
    --chunks 14 \
    --chunk-parallel 2 \
    --per-chunk-parallel 4 \
    --pdf-timeout-ms 15000
```

If a high-parallelism chunk exits before writing its slice JSON, the driver
retries the missing chunk serially. If the chunk still cannot complete, it
falls back to isolated one-PDF subprocesses and records deterministic per-PDF
process exits as `SCANNER_CRASH` entries instead of silently dropping the
slice.

The merge step at the end produces a fresh
`Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-<mode>.json`.
For compatibility, `--page-mode first` also writes
`exploratory-report.json`, which can be copied here to track progress
over time.
