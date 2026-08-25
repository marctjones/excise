# Benchmark difficulty tiers (#1120)

Tier A (synthetic, `tests/redaction-corpus`, built by `gen-redaction-corpus.py`)
tells you what is **broken**. Tiers B/C/D tell you whether it **matters** on
documents people actually have — the benchmark's external validity.

## The reproducibility rule

Each tier is a **checked-in list of `(corpus, relative-path, sha256)`** in
`tests/bench-tiers/tier-{b,c,d}.tsv` — never "whatever was in the directory". A
benchmark whose input set drifts cannot be compared across runs. The corpora
themselves are gitignored and fetched on demand (`scripts/corpus.sh fetch …`);
the manifests are tracked.

- **Build/refresh:** `scripts/build-bench-tiers.py [--tier b|c|d|all]` — runs the
  selection over whatever corpora are present and rewrites the manifests
  deterministically (sorted, capped, content-seeded).
- **Verify:** `scripts/verify-bench-tiers.sh` (in t0) — every named file, *if its
  corpus is present*, must exist and its sha256 must match. A file whose corpus
  is absent is skipped, so this runs without the gitignored data and still
  catches a manifest that drifted from the bytes it claims.

## Tier B — real-world, producer-diverse

Selected for **producer diversity over volume** (Word, LaTeX, InDesign, Quartz,
scanner output, government forms), because every producer lays glyphs out
differently and that is where redaction geometry is exercised. Filter:
text-bearing, size-capped, at most a few files per distinct `/Producer`. Grows as
more real-world corpora are fetched (GovDocs1, #1113, is the big source).

## Tier C — adversarial / malformed-but-tolerated

`qpdf --check`-flagged files from Isartor, pdf.js, PDFium, SafeDocs (#1112):
damaged xref, unterminated `BT` (#1039's shape), object-stream edge cases. The
property here is **not** "redacts correctly" but "**fails safely**" — a tool that
cannot process a document must say so, not emit a file that looks redacted and is
not.

## Tier D — known-bad real redactions

Documents where **someone else's** redaction leaked, found by sweeping the
corpora with [x-ray](https://github.com/freelawproject/x-ray). This is the corpus
for RC15 and `excise audit`, not for scoring excise's own redaction. No public
collection exists, so we generate our own from the hits.

⚠️ **Currently empty.** The present corpora are clean references (test suites,
regression sets), not real redacted documents — x-ray finds nothing. Tier D fills
when a corpus that actually contains leaked redactions is fetched (GovDocs1,
which x-ray was built to sweep at scale on RECAP). The mechanism is in place; the
tier is honestly reported as 0 until then.

## OCG — optional-content coverage (#1111)

`tier-ocg.tsv`: documents with real `/OCProperties` / `/OCGs` structure — the
`includeHiddenLayers` path redaction defaults to. GWG's own Processing-Steps
sample set is membership-gated (its public URL 404s), so rather than chase it,
this takes the equivalent from corpora we already hold (pdf.js, veraPDF, Ghent,
Poppler, PDFium) — 40 optional-content documents, reproducibly selected like the
tiers above.

## Sizing, and the honest caveat

Target ~200–250 documents × 3–5 targets ≈ **800–1,000 cases**. Present manifests
are smaller — they reflect the corpora fetched, and grow on `--update` as more
arrive.

**10–20 cases per stratum reliably detects "this mechanism is broken." It does
NOT certify "99.9% safe."** A 1-in-1000 failure rate needs *thousands* of trials
per stratum to see at all. Quote the tier results as "we did / did not observe
this failure class on documents people have", never as an assurance of a rate the
sample size cannot support — the same trap CLAUDE.md records for the
extraction-parity gate ("a green gate means no worse than the floors, NOT good
enough").
