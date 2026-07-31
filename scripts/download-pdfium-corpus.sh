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
# Complements, rather than duplicates, what we already mirror:
#   pdf.js  (Mozilla)          — 685 files, a different engine's bug history
#   veraPDF (PDF Association)  — 2694 files, PDF/A and PDF/UA conformance
#   Isartor (PDF Association)  — 205 files, PDF/A-1 violation suite
#
# Source: https://pdfium.googlesource.com/pdfium/+/refs/heads/main/testing/resources
# Licence: PDFium is BSD-3-Clause (see the repo's LICENSE).
#
# Files land in test-pdfs/pdfium/ which is gitignored, like every other corpus.
#
# Usage:
#   ./scripts/download-pdfium-corpus.sh
#   ./scripts/download-pdfium-corpus.sh --force     # re-fetch even if present
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

REF="${PDFIUM_REF:-refs/heads/main}"
OUT="$ROOT/test-pdfs/pdfium"
URL="https://pdfium.googlesource.com/pdfium/+archive/${REF}/testing/resources.tar.gz"

FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

existing=0
[ -d "$OUT" ] && existing="$(find "$OUT" -name '*.pdf' 2>/dev/null | wc -l | tr -d ' ')"

if [ "$FORCE" = "0" ] && [ "${existing:-0}" -gt 0 ]; then
    echo "✓ already present: $OUT ($existing PDFs)"
    echo "  re-fetch with --force"
    exit 0
fi

echo "▶ fetching PDFium testing/resources ($REF)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if ! curl -fsSL "$URL" -o "$TMP/resources.tar.gz"; then
    echo "✗ download failed: $URL" >&2
    exit 1
fi

# googlesource serves a directory archive with no top-level folder, so extract
# into a staging dir and copy only the PDFs — the archive also carries .in
# fixture sources, expected-image files and JS, none of which we render.
mkdir -p "$TMP/x"
tar -xzf "$TMP/resources.tar.gz" -C "$TMP/x"

# Preserve the archive's subdirectory layout. Flattening silently loses files
# whose basenames collide across subdirectories (observed: 331 found, 330
# landed), and the scan's expectation manifest keys on the filename, so a
# collision would be invisible in both places.
mkdir -p "$OUT"
count=0
while IFS= read -r f; do
    rel="${f#"$TMP/x/"}"
    mkdir -p "$OUT/$(dirname "$rel")"
    cp "$f" "$OUT/$rel" 2>/dev/null && count=$((count+1))
done < <(find "$TMP/x" -name '*.pdf' -type f)

if [ "$count" -eq 0 ]; then
    echo "✗ archive contained no PDFs — layout may have changed upstream" >&2
    exit 1
fi

echo "✓ $OUT ($count PDFs)"
echo ""
echo "Include it in a rendering scan with:"
echo "    scripts/run-exploratory-corpus.sh --corpus test-pdfs/pdfium --page-mode first"
