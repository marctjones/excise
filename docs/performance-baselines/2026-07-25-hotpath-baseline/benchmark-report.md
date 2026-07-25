# excise Benchmark Report

- Generated: `2026-07-25T06:29:24.8277530+00:00`
- Pages: `8`
- Regression gate: `PASS`
- excise render average: `23.9 ms`
- excise render p95: `82.0 ms`
- Reference pass rate: `100.0 %`
- Redaction completeness: `PASS`

## Tool Isolation

- `excise`: in-process, available=True, selected=True, MIT project code
- `excise-cli`: external-subprocess, available=True, selected=False, excise CLI invoked as a subprocess to test the shipped command route
- `mutool`: external-cli, available=True, selected=True, AGPL/GPL-family renderer invoked only as subprocess
- `pdftocairo`: external-cli, available=True, selected=True, GPL Poppler renderer invoked only as subprocess
- `ghostscript`: external-cli, available=True, selected=True, AGPL Ghostscript renderer invoked only as subprocess
- `pdfbox`: external-cli, available=False, selected=False, Apache PDFBox command invoked only as subprocess
- `pdfium`: external-cli, available=False, selected=False, BSD PDFium pdfium_test invoked only as subprocess

## Gate Checks

| Check | Actual | Threshold | Result |
|---|---:|---:|---|
| excise-render-average | 23.875 ms | 2500 ms | PASS |
| excise-parse-average | 20.5 ms | 750 ms | PASS |
| synthetic-redaction-completeness | 1 pass | 1 pass | PASS |
| reference-fidelity-pass-rate | 1 ratio | 0.6 ratio | PASS |

## Hot Path Buckets

| Bucket | Workload | Route | Scope | Count | Total ms | Avg ms | P95 ms | Issues |
|---|---|---|---|---:|---:|---:|---:|---|
| `renderer.page-render` | renderer.page-render | library | excise-owned | 8 | 191 | 23.9 | 82.0 | #598 #599 |
| `text.extract-search-input` | core.text-extract | library | excise-owned | 8 | 92 | 11.5 | 60.0 | #600 |
| `parser.document-open` | core.document-open | library | excise-owned | 2 | 32 | 16.0 | 25.0 | #597 |
| `redaction.synthetic-save` | redaction.synthetic-save | library | excise-owned-security-critical | 1 | 11 | 11.0 | 11.0 | #597 #602 |
| `reference.external-render` | reference.external-render | external-cli | external-reference | 24 | 2934 | 122.3 | 217.0 | #597 |

## Page Results

| PDF | Page | Status | excise render ms | CLI | References |
|---|---:|---|---:|---|---:|
| `cdc-vis-covid-19.pdf` | 1 | PASS_REFERENCE | 82 | not run | 3/3 |
| `cdc-vis-covid-19.pdf` | 2 | PASS_REFERENCE | 28 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 1 | PASS_REFERENCE | 33 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 2 | PASS_REFERENCE | 6 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 3 | PASS_REFERENCE | 8 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 4 | PASS_REFERENCE | 13 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 5 | PASS_REFERENCE | 6 | not run | 3/3 |
| `irs-1040-instructions.pdf` | 6 | PASS_REFERENCE | 15 | not run | 3/3 |
