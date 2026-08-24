# Test corpora

Every external corpus excise can use, why it is here, and how to get it.

**The data is never in git.** `test-pdfs/`, `tools/vendor/` and `tessdata/` are
gitignored; the *scripts* and the *registry* are tracked. A clean checkout can
rebuild every corpus it needs and store none of them. `scripts/corpus.sh
verify` enforces that — it fails if any registered destination is committable,
and runs in `t0`.

```bash
scripts/corpus.sh list                 # what exists, and what is here
scripts/corpus.sh fetch --tier core    # what the local suite needs
scripts/corpus.sh fetch pdfjs pdfium   # named
scripts/corpus.sh fetch --all          # core + extended + tool
scripts/corpus.sh du                   # disk used, and free space
scripts/corpus.sh remove <name> --yes  # guarded delete
```

The registry is [`tests/corpora.tsv`](../tests/corpora.tsv). Adding a row is
how a corpus becomes visible to the tool; there is no second list to update.

## Tiers

| tier | meaning |
|---|---|
| `core` | fetch these to run the suite as CI would |
| `extended` | larger or narrower; fetch when you need them |
| `tool` | oracle binaries and payloads, not PDFs |
| `planned` | recommended, no download script yet — the row cites the issue |

`planned` is deliberately an *error* to fetch, not a silent skip. A benchmark
that proceeds without the corpus it asked for reports coverage it does not
have.

## What we have

| corpus | size | what it catches |
|---|---:|---|
| **verapdf** | 156M | PDF/A + PDF/UA conformance; the broadest structural corpus |
| **isartor** | 8M | deliberate PDF/A-1b violations; malformed-input survival |
| **pdfjs** | 78M | Mozilla's regression history — fonts, encodings, encryption, forms |
| **pdfium** | 4M | Chrome's regression history |
| **smoke** | 11M | real government forms; the documents a user actually redacts |
| **poppler** | 14M | a fourth renderer's idea of hard |
| **federal** | 11M | everyday federal forms, AcroForm-heavy |
| **local-real-world** | 9M | long real documents |
| **altona / ghent** | 576M | print/colour and PDF/X; rendering fidelity only |

## What we should add, and why

Chosen from the [PDF Association's index of 46 corpora](https://github.com/pdf-association/pdf-corpora)
by what each would catch **for a redaction tool**, not by size or fame.

Each has a download-script issue in **RC14**; `tests/corpora.tsv` carries the
number, and `scripts/corpus.sh verify` fails a `planned` row that cites none.

| corpus | issue | why it matters here |
|---|---|---|
| **Digital Library of Slovenia** | #1108 | invisible text (render mode 3) over scans — a redaction *leak carrier*, and the natural corpus for `HiddenTextDetector` |
| **iText regression suite** | #1109 | ~4,000 classified PDFs including a good encrypted set — feeds #1048 and #1095 |
| **PDF/UA + Matterhorn** | #1110 | tagged PDFs; `/ActualText` and `/Alt`, the #636 carriers |
| **GWG Processing Steps** | #1111 | ISO 19593-1 optional content layers — the `includeHiddenLayers` path |
| **SafeDocs issue-tracker corpus** | #1112 | 32K unusual/malformed files; parser robustness |
| **GovDocs1** | #1113 | 231K real `.gov` PDFs; producer diversity, and the sweep target for finding real bad redactions |

Deliberately skipped: PDF/VT, 3D, HisDoc1B, the table-recognition sets. They
test things excise does not claim to do.

## What does not exist

There is **no public PDF redaction benchmark**, and no public collection of
paired original/redacted documents. Checked: the PDF Association index has
none; the [PETS 2023 de-redaction artifact](https://github.com/maxwell-bland/deredaction)
ships one sample PDF and withholds its corpus deliberately; the
[Free Law Project's x-ray](https://github.com/freelawproject/x-ray) is a
detector with no shipped dataset; TAB is NLP text-anonymisation annotation,
a different layer entirely.

This is why we build our own — **RC13**, which scores redaction on Leak /
Collateral / Fidelity and compares excise against PyMuPDF, iText pdfSweep and a
raster baseline. **RC15** covers the other direction: what excise can read back
out of a redaction, its own or someone else's.
