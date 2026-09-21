# PDF 2.0 conformance testing (#1709)

How excise is tested against ISO 32000-2, what already exists for free, what is
wired, and what is still to build. Measurements are dated 2026-09-20; re-run
before quoting one.

This replaces the capability registry's percentage scorecard as the answer to
"how conformant is excise". That number counted claims we wrote and tests we
cited; the audit that re-scoped it moved it from 84.8% to 59.3% and it was
still measuring our own paperwork. Conformance is measured here against
structured data the PDF Association publishes and against tools that are not
excise.

## 1. What is being claimed

ISO 32000-2 distinguishes conforming **files**, **readers** and **writers**.
excise is a reader (open, render, extract, search, analyse) and a writer (save,
redact, fill, merge, encrypt). So three claims, tested three ways:

| claim | test | oracle |
|---|---|---|
| excise **writes** conforming files | run each operation, judge the output | an existing validator with its own parser |
| excise **reads** every conforming construct | feed it files whose content we know, compare what it exposes | the generator that wrote the bytes |
| excise **preserves** conformance through an edit | validate input and output, judge the difference | the same validator, on both sides |

A tool must not be its own oracle. The reader test needs no external tool
because we author the fixture bytes and so know the truth; the writer test
needs an outside parser because otherwise a writer bug and a matching reader
bug cancel.

## 2. Layers

The spec is not one thing. Arlington covers its object model and says plainly
what it does not (`MODEL_NOTES.md`): lexical rules, content streams, xref
structure and revisions, linearization, FDF, Annex L, reader behaviour.
`test-pdfs/manifests/iso32000-2-table-checklist.json` routes all 436 spec
tables to the source that must cover them. 36 are descriptive and are not
obligations, leaving 400, all of which are in the table below (331 + 6 + 21 +
35 + 3 + 4):

| layer | spec tables | oracle | have | missing |
|---|---:|---|---|---|
| Object model | 331 | Arlington, via veraPDF-Arlington | TSVs pinned; checker wired (§4); table checklist | fixture generator; writer-delta script; TSV loader with every column |
| Syntax, xref, object streams | 6 | none deterministic; `qpdf --check`, corpora | isartor 205, pdfium 331, pdf.js 685 | hand-authored fixtures for Tables 1–4 and the xref-stream layout |
| Content-stream operators | 21 | mutool and other renderers | `OperatorParseRecognitionTests` (70 operators), renderer differentials | none structural |
| Filters and image decode | (in Table 6, 8–13) | decoded bytes from an independent decoder | 25 tracked `pdf20/` image fixtures | JBIG2/JPX/CCITT have no free decoder oracle here |
| Rendering | — | mutool, pdftocairo, Ghostscript, pdftoppm, PDFium, PDFBox | six reference renderers; pdfium, pdf.js, poppler, altona, ghent corpora | — |
| Text extraction | — | mutool, pdftotext | `scripts/check-extraction-parity.sh` | — |
| Profiles (PDF/A, UA, X) | — | veraPDF; Matterhorn | verapdf 2,694, pdfua 428, ghent | — |
| Encryption | — | qpdf | iText fixtures 29 | — |
| Structure elements, linearization, FDF | 35 | Arlington marks these TBD | structure elements are partly covered in practice by veraPDF's PDF/UA validation and the 428 `pdfua` tagged PDFs; nothing for linearization or FDF | no Arlington-based check. Annex L is a machine-readable XLSX inside the ISO PDF, which we pin by hash and do not hold. FDF is out of scope for excise. |
| Name/number-tree primitives | 3 | Arlington built-ins (`name-tree`, `number-tree`) | exercised by the object-model layer | — |
| ICC profile data, and "any object can have Metadata" | 4 | no oracle chosen | nothing | unrouted in the checklist; decide whether they are obligations (open question 1 of the #1709 handoff) |
| Robustness | — | crash/hang is its own oracle | isartor, pdfium, safedocs (planned, absent) | deterministic corpus mutation |

## 3. Arlington, and how it is used

The Arlington PDF Model is a machine-readable definition of the PDF object
model derived from ISO 32000-2:2020 and its resolved errata, published by the
PDF Association under DARPA's SafeDocs program (Apache-2.0). One TSV per
object, one row per key: type, required (possibly conditional), version
introduced and deprecated, direct or indirect, allowed values, cross-key
predicates, and the object type a value links to. 611 objects, 3,973 rows.

It is a **grammar**. It says whether a file's structure is legal. It gives no
rendered result, no expected output and no behaviour. It is used three ways:

1. **The requirements list.** Every row is a testable statement.
2. **A file validator**, through existing implementations (§4).
3. **A fixture source.** No existing tool turns the model into test files. This
   is the gap worth building into.

72% of rows (2,867) carry no predicate at all; 921 keys are plainly required;
831 have a concrete list of allowed values. The remaining 28% use 45 predicate
functions, dominated by `Eval` (761), `ArrayLength` (353), `IsPresent` (305) and
`IsRequired` (192).

## 4. Wired now

Free tools only. Nothing is copied into git; each is fetched by a tracked
script into its own contained environment.

```bash
scripts/corpus.sh fetch arlington-model verapdf-arlington   # or run the two scripts directly
scripts/run-arlington-check.sh up
scripts/run-arlington-check.sh check --profile arlington2.0 test-pdfs/pdf20 path/to/output.pdf
scripts/run-arlington-check.sh down
```

veraPDF's Arlington checker runs as a container image pinned by digest
(`verapdf/arlington` v1.30.2, GPL/MPL, so invoked over HTTP and never linked,
the same boundary as `VeraPdfReferenceValidator`). Output is one JSON report per
file plus a `summary.tsv`. It is `linux/amd64` only, so on Apple Silicon it runs
emulated: 37 small files took 21 s, an ordinary form 4–17 s once warm. The
first file after a cold `up` is slow (26 s for a 100 KB form that later took 4 s),
so do not time a single run.

**Limits, from upstream's own notes and from running it.** It does not support
six predicates (`AlwaysUnencrypted`, `FontHasLatinChars`, `Ignore`,
`IsLastInNumberFormatArray`, `IsMeaningful`, `KeyNameIsColorant`), does not check
object numbers against `/Size`, and behaves as a PDF 1.5 processor. Its parser
requires `startxref` in the last 1024 bytes and does not reconstruct a damaged
xref, so a hand-built file like Arlington's `RuleBreaker-INVALID.pdf` reports
`PARSE_FAILED`. That means "this checker could not judge it", never "invalid".

**Pick the profile that matches the file.** Validating a PDF 1.7 document
against `arlington2.0` reports every 1.x-only feature as "deprecated since PDF
2.0" (`/ProcSet` alone is hundreds of checks). For an input/output comparison
use the same profile on both sides and judge the difference.

### What running it found (2026-09-20)

- **The tracked `pdf20/` fixtures are not valid PDF 2.0.** All 32 excise-authored
  fixtures veraPDF could open (25 in `pdf20/`, 7 in `generated-regressions/`)
  fail "Entry ID in FileTrailer is required" (`/ID` is required by 2.0); the
  three real-world documents in `sample-pdfs/` do not. Some also lack `/Widths`, `/FirstChar`, `/LastChar` and `/FontDescriptor` on Type 1
  fonts, or have annotations with no appearance stream. They are minimal render
  probes, not conformance claims, but the directory name says otherwise.
- **The writer test works with existing tools.** Six real documents run through
  `excise redact` (a no-match term, so a pure save) and judged on both sides:
  **0 rules introduced**, 3–10 rules per document no longer failing (the
  deprecated `/Info` fields and XFA that the Standard profile strips). Six
  documents proves the method, not the property, and the check has not yet been
  shown able to go red on a planted violation.
- **Corpus survey, not verdict.** The read-side key observation showed the ten
  smoke documents exercise 135 of 3,973 keys (3.4%), which is the argument for
  generated fixtures over real corpora as the reader test.

### Rejected and deferred

- **Arlington's `arlington.py`** (Python, pikepdf): evaluated and not adopted.
  Its PDF mode is a key-set walker (`+` key only in the PDF, `?` key present but
  mistyped). It checks no required keys and no predicates, so it is a weaker
  subset of what veraPDF already does.
- **Arlington's `TestGrammar`** (C++ reference implementation): deferred after
  three failures. The qpdf backend segfaults at startup on every input we tried:
  macOS against qpdf 12.3.2 (crash inside `libqpdf`'s `BaseDictionary`
  constructor), and Ubuntu 24.04 against qpdf 11.x on both a well-formed document
  and RuleBreaker. Upstream's README lists the QPDF binding as still being
  finished. The working backends are PDFium and PDFix; PDFix is commercial and
  excluded. Revisit if upstream finishes the QPDF binding, or by building the
  PDFium backend against the prebuilt library already in `tools/vendor/pdfium`
  (untested). Until then RuleBreaker has no checker that can both open it and
  check predicates.
- **Commercial**: PDFix, QualityLogic's PDF 2.0 Functional Test Suite. Excluded
  by the free-tools-only rule.

## 5. Corpora

Organised well: `tests/corpora.tsv` (25 rows) is the registry and
`scripts/corpus.sh` the dispatcher (`list`, `fetch`, `du`, `verify`, `remove`).
Every registered destination must be gitignored (`verify` enforces it), and
`build-pdf-corpus-governance.py` derives a provenance inventory from the
registry.

Gaps, found 2026-09-20:

- **No per-file index.** The registry and governance manifest are per corpus.
  Per-file SHA-256 exists only behind `build-pdf-corpus-governance.py
  --hash-files`, off by default and not committed. There is no single table of
  every PDF with its corpus, licence, PDF version, producer and validation
  result.
- **Five directories are unregistered:** `pdf20`, `generated-regressions`,
  `sample-pdfs` (all git-tracked, 36 files, 2.9 MB), plus `redaction-adversarial`
  (generated) and `reader-bench`. The registry cannot hold tracked directories
  (`verify` requires gitignored), so the excise-owned corpus has no index at all.
- **Pinning is uneven.** `download-test-pdfs.sh` fetches the veraPDF corpus from
  `refs/heads/master.zip` and Isartor by URL, with no revision pin and no
  checksum, while `tests/corpus-expectations-verapdf.tsv` pins per-file
  expectations for 2,694 of those files. A moving branch under fixed
  expectations is a reproducibility defect. `download-pdfium-corpus.sh` pins a
  ref but records no checksums; `download-pdfjs-corpus.sh` writes a per-file
  SHA-256 manifest and is the pattern to copy. `download-arlington-model.sh` is
  the pattern for revision plus tarball digest.
- **A possible licensing problem is tracked in git:** `sample-pdfs/
  acc-global-compensation-report.pdf` (2.6 MB) is a third-party report, not a
  public-domain document. `CLAUDE.md` states third-party corpora are not
  checked in for licensing reasons.

### What belongs in git

An excise-owned corpus should supplement the third-party ones with what they
cannot provide, and stay small and unambiguous:

| kind | in git? | why |
|---|---|---|
| Files we generate (from a script or from Arlington) | the generator and its expected results; the outputs if under ~1 MB in total | reproducible, and the generator is the source |
| Minimised regression files, one per bug | yes | authored by us, tiny, each names its issue |
| Public-domain documents under ~100 KB with clear provenance | yes, with source URL, SHA-256 and licence in a tracked manifest | otherwise fetched |
| Everything else, including all real-world documents | no. Manifest of URL, pinned revision, SHA-256, licence; fetched by script | licensing, size, and no history bloat |

## 6. To build, in order

Each item leads with what it costs and what failure it closes. Nothing below
starts until a decision in §7 is made.

1. **Pin the two unpinned core downloaders** (veraPDF corpus, Isartor). Small.
   Closes: baselines that silently drift when upstream `master` moves.
2. **Per-file corpus index** (`corpus.sh index`): path, SHA-256, bytes, corpus,
   licence, PDF version, producer, pages, encrypted, and the Arlington result.
   Small script, then a long emulated run (hours for ~4,800 files; a native
   veraPDF install would be far faster). Closes: not knowing which PDFs exercise
   which spec features, and the untracked owned corpus.
3. **Writer-delta script** (`arlington-delta`): run an excise operation over a set
   of inputs, validate both sides, fail on any introduced rule. Small, and mostly
   done by hand above. Closes: excise emitting non-conformant structure without
   anything noticing, the class of the PDF/UA `/Artifact` defect found by
   veraPDF in #1586. Must be shown to go red on a planted violation.
4. **The C# conformance tool**, separate from excise (own directory and solution,
   referencing excise only through its public API behind an adapter interface):
   a loader for every TSV column, a byte-level fixture emitter that is not
   excise's writer, valid and invalid variants per row, and a reader harness.
   Start with the predicate-free 72% and add predicates by frequency. Medium.
   Closes: the reader claim, which is untested at 3.4% key coverage.
5. **Report per ISO table**, replacing the corpus-observation input of
   `report-iso-table-status.py` with test results, and retiring the registry
   percentages.
6. **Hand-authored syntax fixtures** for the six lexical and xref tables.

Not planned: FDF; Annex L until the XLSX is in hand.

## 7. Decisions

1. Remove or replace `sample-pdfs/acc-global-compensation-report.pdf`?
2. Bulk runs of a `linux/amd64` container emulated on Apple Silicon are slow.
   For the per-file index, keep the container, or fetch veraPDF's native
   installer zip (GPG-signed, needs the Homebrew JDK)?
3. Where does the generated-fixture corpus live once it exists: tracked if under
   1 MB, regenerated otherwise?
