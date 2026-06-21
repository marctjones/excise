# Exploratory Differential Reports

This directory holds merged JSON reports from running real-world corpus
PDFs through pdfe with primary reference oracles (`mutool draw` and
`pdftocairo`) plus optional escalation oracles (`Ghostscript`/GhostPDF,
Apache PDFBox, and PDFium's `pdfium_test`).

```bash
./scripts/run-exploratory-corpus.sh --page-mode first
```

Each entry records pdfe's render result for one page, plus the per-oracle
diff metrics and oracle statuses. Page-1 reports therefore have one entry per PDF;
sampled and exhaustive reports have multiple entries per PDF.

Entries also include timing diagnostics when produced by current tooling:
`elapsedMs` for the page, `pdfElapsedMs` for the whole PDF, and per-phase
`renderMs`, `mutoolMs`, `cairoMs`, `ghostscriptMs`, `pdfboxMs`, and
`pdfiumMs` when those oracles run. Failures include `errorPhase`
(`open`, `render`, `oracle`, `compare`, or `scan`) and may
include a `diagnostic` snapshot for wall-clock timeouts. The merge script
prints the slowest pages and failure diagnostics into the run log.

| Status | Meaning |
|---|---|
| `PASS` | pdfe matches both mutool and pdftocairo within thresholds |
| `PASS_ONE` | pdfe matches at least one available oracle, but not both primary oracles (engine-specific quirk or unsettled reference split, not a clear pdfe bug) |
| `DIFF` | pdfe disagrees with every available rendered oracle |
| `PARSE_ERROR` | pdfe can't open the PDF (parser limitation) |
| `DECODE_ERROR` | pdfe opened the PDF but a stream, image, or content decode path failed during rendering |
| `TIMEOUT` | per-PDF wall-clock budget exceeded |
| `ALL_ORACLES_REFUSED` | reference engines cannot render the page |
| `RECOVERED_MALFORMED_CONTENT` | pdfe rendered bounded valid content after skipping malformed page-content streams; tracked as robustness coverage, not focused visual-fidelity work |
| `EMPTY_DOC` | 0 pages |
| `COMPARE_ERROR` | bitmap-comparison crashed |
| `SCANNER_CRASH` | a one-PDF recovery subprocess exited before writing a report |

## Test layers

The rendering harness deliberately has separate layers:

| Layer | Command | What it answers |
|---|---|---|
| Fast trend scan | `./scripts/run-exploratory-corpus.sh --page-mode first` | Broad corpus signal, one page per PDF |
| Sampled multi-page scan | `./scripts/run-exploratory-corpus.sh --page-mode sample` | Common later-page regressions without full corpus cost |
| Exhaustive release scan | `./scripts/run-exploratory-corpus.sh --page-mode all --extra-oracles all` | Every page of every corpus PDF against the primary visual oracles, with expanded oracle evidence on unsettled pages |

The default remains `first` so local iteration stays fast. Release
validation should use `sample` while investigating and `all` before
declaring rendering quality final.

`--extra-oracles ghostscript` is the default. Use `--extra-oracles none` to
reproduce the older MuPDF+Poppler-only reports, or `--extra-oracles all` to add
Ghostscript, PDFBox, and PDFium where locally configured. PDFBox can be enabled
with `PDFE_PDFBOX_JAR=/path/to/pdfbox-app.jar`; PDFium can be enabled with
`PDFE_PDFIUM_TEST=/path/to/pdfium_test`.

## Corpus Tiers

| Corpus | Downloader | Default path | Purpose |
|---|---|---|---|
| Smoke government corpus | `scripts/download-smoke-corpus.sh` | `test-pdfs/smoke` | Small gating set used by unit/differential tests. Keep this stable so normal test runs do not gain surprise fixtures. |
| Federal everyday corpus | `scripts/download-federal-corpus.sh` | `test-pdfs/federal` | Manifest-driven release-quality corpus with source URL, category, agency, license basis, page count, and SHA-256 for each official `.gov` PDF. |
| pdf.js corpus | `scripts/download-pdfjs-corpus.sh` | `test-pdfs/pdfjs` | Broad bug-reproduction corpus for exploratory fidelity scans. |
| Poppler corpus | `scripts/download-poppler-corpus.sh` | `test-pdfs/poppler` | Poppler's public regression test-data repository, including rendering fixtures and reference assets. |

The federal everyday corpus manifest lives at
`test-pdfs/manifests/federal-everyday-corpus.json`. It currently includes IRS
tax forms/publications, State Department passport forms, USCIS I-9, CMS-40B,
CDC public-health material, and SCOTUS opinions. The legal basis is recorded per
entry as official U.S. federal government work under 17 USC 105.

Download or refresh it with:

```bash
./scripts/download-federal-corpus.sh
```

Download or refresh Poppler's public regression corpus with:

```bash
./scripts/download-poppler-corpus.sh
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

See `Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-all.json` for
the full current data; below is the headline breakdown from the 2026-06-18
release-quality run against the current pdf.js corpus download. This snapshot
used `--page-mode all`, so the count is one result per rendered page.

Command:

```bash
scripts/run-exploratory-corpus-tmux.sh --session pdfe-corpus-release-20260618-cff-all -- \
    --page-mode all \
    --pdf-timeout-ms 120000 \
    --chunk-parallel 2 \
    --per-chunk-parallel 1
```

Run log: `logs/exploratory-corpus_20260618_142358/run.log`.

The run merged all 14 expected chunk reports. One native chunk process exited
with `SIGBUS` before writing its slice; serial retry reproduced the failure,
then isolated one-PDF recovery rebuilt the missing chunk and produced a complete
report with no `SCANNER_CRASH` entries.

| Status | Count | % of 1,275 pages |
|---|---|---|
| PASS | 830 | 65.1% |
| PASS_ONE | 353 | 27.7% |
| DIFF | 57 | 4.5% |
| MALFORMED_PDF | 21 | 1.6% |
| DECODE_ERROR | 6 | 0.5% |
| UNSUPPORTED_ENCRYPTED | 3 | 0.2% |
| UNSUPPORTED_COMPRESSION | 2 | 0.2% |
| RESOURCE_LIMIT | 1 | 0.1% |
| ALL_ORACLES_REFUSED | 1 | 0.1% |
| INVALID_PAGE_GEOMETRY | 1 | 0.1% |

At the PDF level, 601 of 682 files have only `PASS`/`PASS_ONE` visual results,
46 files have at least one `DIFF`, and 35 files are classified non-visual
failures only.

### Post-#503 focused validation

After the Type1C/CFF custom glyph-name fix for #503, a focused all-pages scan
over the three affected PDFs (`TAMReview.pdf`, `issue11878_reduced.pdf`, and
`freeculture.pdf`) completed 385 page results:

| Status | Before | After |
|---|---:|---:|
| PASS | 80 | 97 |
| PASS_ONE | 275 | 276 |
| DIFF | 30 | 12 |

`TAMReview.pdf` improved from 18 `DIFF` pages to 23 `PASS` pages. The remaining
12 focused `DIFF` pages are unchanged: all 10 pages of the 3x3 pt
`issue11878_reduced.pdf` fixture plus pages 171 and 255 of `freeculture.pdf`.
The full 2026-06-18 all-pages run confirmed that `TAMReview.pdf` no longer has
`DIFF` pages in the release corpus.

The 2026-06-18 sampled gate also completed after isolated recovery of native
chunk crashes:

| Status | Count | % of 753 sampled page results |
|---|---:|---:|
| PASS | 620 | 82.3% |
| PASS_ONE | 51 | 6.8% |
| DIFF | 47 | 6.2% |
| MALFORMED_PDF | 21 | 2.8% |
| DECODE_ERROR | 6 | 0.8% |
| UNSUPPORTED_ENCRYPTED | 3 | 0.4% |
| UNSUPPORTED_COMPRESSION | 2 | 0.3% |
| RESOURCE_LIMIT | 1 | 0.1% |
| TIMEOUT | 1 | 0.1% |
| INVALID_PAGE_GEOMETRY | 1 | 0.1% |

**Current `DIFF` clusters (57 pages across 46 PDFs):**

* **21 — single-page pdf.js issue repros.** Mixed isolated rendering edge cases
  from pdf.js issue fixtures; the top outliers include large color/shape misses
  such as `issue12007_reduced.pdf`, `issue10339_reduced.pdf`,
  `issue14814.pdf`, `issue13931.pdf`, and `issue15716.pdf`.
* **10 — tiny reduced fixture.** `issue11878_reduced.pdf` remains `DIFF` on all
  10 pages, but the PDF is only 3x3 pt and is not representative of ordinary
  human-readable documents.
* **7 — transparency, mask, and color residuals.** Includes `S2.pdf`,
  `alphatrans.pdf`, `160F-2019.pdf`, `issue1905.pdf`, and related color/image
  cases. The DCT/JPEG soft-mask release blocker is fixed, but this cluster still
  needs targeted follow-up before claiming high-fidelity image/transparency
  parity.
* **7 — JBIG2 refinement fixtures.** The large missing-JBIG2 bucket is gone;
  remaining JBIG2 diffs are concentrated in symbol/text refinement edge cases.
* **6 — Mozilla Bugzilla repros.** Mixed single-page PDF engine edge cases.
* **4 — forms/annotation appearance.** `issue1350.pdf`, `issue269_1.pdf`, and
  `issue269_2.pdf` remain as focused appearance/fidelity work.
* **2 — isolated `freeculture.pdf` pages.** Pages 171 and 255 remain `DIFF` out
  of 352 pages.

**Non-visual failures (35):**

* 21 `MALFORMED_PDF` — malformed headers/xrefs, invalid seek offsets,
  generation-number parsing, overflow, and fuzzed stream syntax.
* 6 `DECODE_ERROR` — render-time parser/decode limitations such as unresolved
  stream `/Length` references and malformed content streams.
* 3 `UNSUPPORTED_ENCRYPTED` — password-protected files or encryption variants
  outside current unattended rendering support.
* 2 `UNSUPPORTED_COMPRESSION` — compressed archive methods outside the current
  input reader.
* 1 each: `RESOURCE_LIMIT` and `INVALID_PAGE_GEOMETRY`. The previous
  `ALL_ORACLES_REFUSED` entry (`bomb_giant.pdf`) is now classified as
  `RECOVERED_MALFORMED_CONTENT`: pdfe renders the valid prefix and skips the
  malformed JBIG2 page-content streams.

**`PASS_ONE` (353) — pdfe sides with one oracle:**

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
