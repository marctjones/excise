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

## Latest snapshot

See `exploratory-report.json` for the full data; below is the headline
breakdown from the 2026-06-15 run against the current pdf.js corpus
download. This snapshot used `--page-mode first`, so the count is one
page result per PDF.

| Status | Count | % of 682 |
|---|---|---|
| PASS | 454 | 66.6% |
| DIFF | 146 | 21.4% |
| PASS_ONE | 45 | 6.6% |
| PARSE_ERROR | 34 | 5.0% |
| TIMEOUT | 2 | 0.3% |
| COMPARE_ERROR | 1 | 0.1% |

**Top failure clusters in `DIFF` (146):**

* **76 (51%) — `bitmap-*` JBIG2 fixtures.** pdfe doesn't have a JBIG2
  decoder yet, so these all render blank or near-blank. One missing
  feature → half the rendering gap. Implementing JBIG2 would close
  this entire cluster.
* **39 — `issue*` repros.** Assorted from pdf.js's GitHub issue
  tracker. Many are font/encoding edge cases, transparency
  combinations, or color-space ambiguities.
* **11 — `bug*` Mozilla Bugzilla repros.** Same shape as `issue*`.
* **20 other** — annotations, shading, masks, miscellaneous.

**`PARSE_ERROR` (34):**

* 25 `PdfParseException` — diverse: xref recovery, stream `/Length`
  resolution failures, generation-number parsing, etc.
* 3 `PdfEncryptionNotSupportedException` — encryption variants
  beyond AESV5/R6
* 2 `OverflowException` — Int32 overflow on malformed size fields
* 2 `InvalidDataException` — unsupported compressed archive method
* 2 others (`IndexOutOfRangeException`, bitmap allocation failure)

**`TIMEOUT` (2):**

* `bomb_giant.pdf`
* `issue14256.pdf`

**`PASS_ONE` (45) — pdfe sides with:**

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

The merge step at the end produces a fresh
`Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-<mode>.json`.
For compatibility, `--page-mode first` also writes
`exploratory-report.json`, which can be copied here to track progress
over time.
