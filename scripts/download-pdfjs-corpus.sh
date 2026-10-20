#!/usr/bin/env bash
# Mirror Mozilla pdf.js's PDF test corpus (test/pdfs) into test-pdfs/pdfjs.
#
# pdf.js (mozilla/pdf.js, Apache-2.0) keeps its public regression PDFs in
# test/pdfs/. Each was contributed because it broke pdf.js or another renderer
# in some real-world way: fonts, encodings, encryption, forms, annotations,
# image XObjects, malformed-but-recoverable inputs, and more.
#
# Pinned to exact bytes (#1727). tests/corpus-pins/pdfjs.sha256 is the corpus:
# 685 PDFs, each byte-identical to test/pdfs at PDFJS_COMMIT below (the
# `master` head when the files on disk were last fetched, 2026-06-28).
# tests/corpus-expectations.tsv (1,279 per-page rows) and the pdfjs rows of
# tests/redaction-hard-cases.tsv were generated against exactly these files.
# A re-pin is ONE commit that changes PDFJS_COMMIT and the pin list and
# regenerates those expectations with scripts/update-corpus-expectations.sh.
#
# The corpus is a SUBSET of test/pdfs at that commit (964 PDFs there). The old
# downloader listed the directory with one GitHub contents-API call, which
# returns at most 1,000 entries; test/pdfs has ~1,420 (most of the rest are
# .link files), so only the PDFs sorting up to issue5010.pdf were ever fetched.
# The 279 later PDFs were never fetched or baselined. Adding them is a new
# expectations change, not a re-pin.
#
# Excluded: the .link files. Each names an external PDF URL that pdf.js's own
# test runner downloads; this mirror never fetched them, no expectation row
# refers to one, and the external URLs carry no digest.
#
# Each listed file is fetched from raw.githubusercontent.com at the pinned
# commit into a staging directory, then the whole set is verified against the
# pin list. Only then is it moved into place. On any mismatch or missing file
# the staging directory is deleted, nothing is installed, and the script exits 1.
#
# EXCISE_TEST_PDF_DIR overrides the parent directory (default: test-pdfs/).
#
# Usage:
#   ./scripts/download-pdfjs-corpus.sh            # verify what is present, or fetch
#   ./scripts/download-pdfjs-corpus.sh --force    # re-fetch and replace test-pdfs/pdfjs

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PDF_DIR="${EXCISE_TEST_PDF_DIR:-$ROOT/test-pdfs}"
OUT="$TEST_PDF_DIR/pdfjs"
PINS="$ROOT/tests/corpus-pins/pdfjs.sha256"

PDFJS_COMMIT="f2f3a7fdce7a595576cb25e203abc65d0cfb83f2"
RAW="https://raw.githubusercontent.com/mozilla/pdf.js/$PDFJS_COMMIT/test/pdfs"

FORCE=0
[[ "${1:-}" == "--force" ]] && FORCE=1

[[ -f "$PINS" ]] || { echo "✗ pin list missing: $PINS" >&2; exit 1; }

sha256_check() {  # sha256_check <dir> : verify the pin list against <dir>, quietly
    if command -v sha256sum >/dev/null 2>&1; then
        (cd "$1" && sha256sum --quiet -c "$PINS")
    else
        (cd "$1" && shasum -a 256 --quiet -c "$PINS")
    fi
}

# verify_tree <dir>: the PDFs in <dir> are exactly the pinned set, byte for byte.
verify_tree() {
    local dir=$1
    local extra
    extra="$(comm -13 <(awk '{print $2}' "$PINS" | LC_ALL=C sort) \
                      <(cd "$dir" && find . -name '*.pdf' -type f | sed 's#^\./##' | LC_ALL=C sort))"
    if [[ -n "$extra" ]]; then
        echo "✗ PDFs not in the pin list ($PINS):" >&2
        echo "$extra" | head -20 | sed 's/^/    /' >&2
        return 1
    fi
    if ! sha256_check "$dir" >&2 2>&1; then
        echo "✗ PDFs missing or not matching their pinned sha256 (lines above)" >&2
        return 1
    fi
}

pinned_count="$(wc -l < "$PINS" | tr -d ' ')"
existing=0
[[ -d "$OUT" ]] && existing="$(find "$OUT" -name '*.pdf' -type f 2>/dev/null | wc -l | tr -d ' ')"

if [[ "$FORCE" == "0" && "$existing" -gt 0 ]]; then
    if verify_tree "$OUT"; then
        echo "✓ already present and verified: $OUT ($existing PDFs, pdf.js@${PDFJS_COMMIT:0:12})"
        exit 0
    fi
    echo "✗ $OUT does not match the pin; re-fetch with --force" >&2
    exit 1
fi

echo "▶ fetching $pinned_count pdf.js test PDFs at $PDFJS_COMMIT"
mkdir -p "$TEST_PDF_DIR"
STAGE="$(mktemp -d "$TEST_PDF_DIR/.pdfjs-staging.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT
mkdir -p "$STAGE/tree"

# One curl process, parallel transfers, driven by a config file. The names are
# plain ASCII ([A-Za-z0-9._-], plus '#' in empty#hash.pdf, sent as %23); refuse
# anything else rather than guess at URL-encoding.
if awk '{print $2}' "$PINS" | grep -v '^[A-Za-z0-9._#-]*\.pdf$' >&2; then
    echo "✗ pin list has a name that needs URL-encoding (above)" >&2
    exit 1
fi
awk -v raw="$RAW" -v dir="$STAGE/tree" \
    '{ u = $2; gsub(/#/, "%23", u)
       printf "url = \"%s/%s\"\noutput = \"%s/%s\"\n", raw, u, dir, $2 }' "$PINS" > "$STAGE/curl.cfg"
if ! curl -fsSL --retry 3 --max-time 120 --parallel --parallel-max 8 -K "$STAGE/curl.cfg"; then
    echo "  (some transfers failed; the verification below names them)" >&2
fi

if ! verify_tree "$STAGE/tree"; then
    echo "✗ the fetched files do not match the pinned bytes; nothing installed" >&2
    exit 1
fi
echo "  verified: $pinned_count PDFs match tests/corpus-pins/pdfjs.sha256"

rm -rf "$OUT"
mv "$STAGE/tree" "$OUT"
echo "✓ $OUT ($pinned_count PDFs, pdf.js@${PDFJS_COMMIT:0:12})"
echo
echo "Run the differential harness over them with:"
echo "  dotnet test Excise.Rendering.Tests --filter \"FullyQualifiedName~Differential\""
