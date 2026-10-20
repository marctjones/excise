#!/usr/bin/env bash
#
# Mirror PDFium's regression PDFs (testing/resources) into test-pdfs/pdfium.
#
# Same rationale as the pdf.js mirror: every file in there was contributed
# because it broke Chrome's PDF renderer, so it is adversarial input selected by
# someone else's bug reports rather than by our own assumptions. PDFium is also
# the most widely deployed PDF renderer there is, which makes its regression set
# a good proxy for "documents real users actually hit".
#
# Source: https://pdfium.googlesource.com/pdfium/+/<commit>/testing/resources
# Licence: PDFium is BSD-3-Clause (see the repo's LICENSE).
#
# Pinned to exact bytes (#1727). tests/corpus-expectations-pdfium.tsv (405
# per-page rows) and the pdfium rows of tests/redaction-hard-cases.tsv were
# generated against the 331 PDFs listed in tests/corpus-pins/pdfium.sha256,
# which are byte-identical to testing/resources at PDFIUM_COMMIT below (the
# `main` head when the corpus was fetched on 2026-07-31). A re-pin is ONE
# commit that changes PDFIUM_COMMIT, regenerates the pin list and regenerates
# those expectations with scripts/update-corpus-expectations.sh.
#
# gitiles builds the archive on demand, so its bytes are not stable and are not
# pinned. Instead the EXTRACTED tree is verified: every PDF in the archive must
# be in the pin list with the pinned SHA-256, and every listed PDF must be
# present. Only then is the tree moved into place. On any mismatch the staging
# directory is deleted, nothing is installed, and the script exits 1.
#
# Only *.pdf files are installed; the archive's .in fixture sources, expected
# images and JS are not rendered by anything here. The subdirectory layout
# (xfa/, pixel/, javascript/...) is preserved because the expectations key on
# the corpus-relative path.
#
# EXCISE_TEST_PDF_DIR overrides the parent directory (default: test-pdfs/).
#
# Usage:
#   ./scripts/download-pdfium-corpus.sh            # verify what is present, or fetch
#   ./scripts/download-pdfium-corpus.sh --force    # re-fetch and replace test-pdfs/pdfium
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TEST_PDF_DIR="${EXCISE_TEST_PDF_DIR:-$ROOT/test-pdfs}"
OUT="$TEST_PDF_DIR/pdfium"
PINS="$ROOT/tests/corpus-pins/pdfium.sha256"

PDFIUM_COMMIT="1125be2864fadd9ee39c34e53fc3fc08925152ef"
URL="https://pdfium.googlesource.com/pdfium/+archive/${PDFIUM_COMMIT}/testing/resources.tar.gz"

FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

[ -f "$PINS" ] || { echo "✗ pin list missing: $PINS" >&2; exit 1; }

sha256_check() {  # sha256_check <dir> : verify the pin list against <dir>, quietly
    if command -v sha256sum >/dev/null 2>&1; then
        (cd "$1" && sha256sum --quiet -c "$PINS")
    else
        (cd "$1" && shasum -a 256 --quiet -c "$PINS")
    fi
}

# verify_tree <dir>: the PDFs under <dir> are exactly the pinned set, byte for byte.
verify_tree() {
    local dir=$1
    local extra
    extra="$(comm -13 <(awk '{print $2}' "$PINS" | LC_ALL=C sort) \
                      <(cd "$dir" && find . -name '*.pdf' -type f | sed 's#^\./##' | LC_ALL=C sort))"
    if [ -n "$extra" ]; then
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
[ -d "$OUT" ] && existing="$(find "$OUT" -name '*.pdf' -type f 2>/dev/null | wc -l | tr -d ' ')"

if [ "$FORCE" = "0" ] && [ "${existing:-0}" -gt 0 ]; then
    if verify_tree "$OUT"; then
        echo "✓ already present and verified: $OUT ($existing PDFs, pdfium@${PDFIUM_COMMIT:0:12})"
        exit 0
    fi
    echo "✗ $OUT does not match the pin; re-fetch with --force" >&2
    exit 1
fi

echo "▶ fetching PDFium testing/resources at ${PDFIUM_COMMIT}"
mkdir -p "$TEST_PDF_DIR"
STAGE="$(mktemp -d "$TEST_PDF_DIR/.pdfium-staging.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT

if ! curl -fsSL "$URL" -o "$STAGE/resources.tar.gz"; then
    echo "✗ download failed: $URL" >&2
    exit 1
fi

# googlesource serves a directory archive with no top-level folder.
mkdir -p "$STAGE/x" "$STAGE/tree"
if ! tar -xzf "$STAGE/resources.tar.gz" -C "$STAGE/x"; then
    echo "✗ archive did not extract: $URL" >&2
    exit 1
fi
(cd "$STAGE/x" && find . -name '*.pdf' -type f) | while IFS= read -r rel; do
    rel="${rel#./}"
    mkdir -p "$STAGE/tree/$(dirname "$rel")"
    cp "$STAGE/x/$rel" "$STAGE/tree/$rel"
done

if ! verify_tree "$STAGE/tree"; then
    echo "✗ the fetched tree does not match the pinned bytes; nothing installed" >&2
    exit 1
fi
echo "  verified: $pinned_count PDFs match tests/corpus-pins/pdfium.sha256"

rm -rf "$OUT"
mv "$STAGE/tree" "$OUT"
echo "✓ $OUT ($pinned_count PDFs, pdfium@${PDFIUM_COMMIT:0:12})"
echo ""
echo "Include it in a rendering scan with:"
echo "    scripts/run-exploratory-corpus.sh --corpus test-pdfs/pdfium --page-mode first"
