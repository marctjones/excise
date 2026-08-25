#!/usr/bin/env bash
# PDF/UA Reference Suite + Matterhorn Protocol — tagged-PDF structure-tree
# fixtures for redaction (#1110).
#
# Tagged PDFs with a real structure tree are where /ActualText and /Alt live —
# the #636 carriers: text restated OUTSIDE the content stream that survives
# glyph removal untouched. The Matterhorn Protocol is 136 documented failure
# conditions, so this doubles as a structure-tree edge-case set.
#
# Source: the veraPDF corpus (github.com/veraPDF/veraPDF-corpus), PDF_UA-1 and
# PDF_UA-2 trees. Licence: Creative Commons Attribution 4.0 (CC BY 4.0) —
# permits use as test fixtures with attribution. Used here offline, never
# redistributed.
#
# Only the PDF_UA subtrees are kept — the same archive carries the PDF/A and
# ISO test files, which have their own corpora and are not what this row is for.
#
# Output:
#   test-pdfs/pdfua/<flattened>.pdf
#   test-pdfs/pdfua/.excise-manifest.tsv   (sha256<TAB>name<TAB>size)
set -euo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TARGET="$ROOT/test-pdfs/pdfua"
REF="${VERAPDF_CORPUS_REF:-staging}"
ZIP_URL="${VERAPDF_CORPUS_ZIP:-https://codeload.github.com/veraPDF/veraPDF-corpus/zip/refs/heads/$REF}"
FORCE=0

usage() {
    cat <<'EOF'
Fetch the PDF/UA (Matterhorn) tagged-PDF fixtures from the veraPDF corpus.
Usage: scripts/download-pdfua-matterhorn.sh [--force]
Requires: curl, unzip.
EOF
}
[ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ] && { usage; exit 0; }
[ "${1:-}" = "--force" ] && FORCE=1

if [ "$FORCE" = 0 ] && [ -d "$TARGET" ] && [ -n "$(find "$TARGET" -name '*.pdf' -print -quit 2>/dev/null)" ]; then
    echo "pdfua subset already present ($(find "$TARGET" -name '*.pdf' | wc -l | tr -d ' ') PDFs). --force to refetch."
    exit 0
fi

command -v curl >/dev/null || { echo "curl required" >&2; exit 1; }
command -v unzip >/dev/null || { echo "unzip required" >&2; exit 1; }

mkdir -p "$TARGET"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

echo "==> $ZIP_URL"
curl -fL --retry 3 --max-time 600 -o "$TMP/corpus.zip" "$ZIP_URL"

# Extract only the PDF_UA-1 / PDF_UA-2 PDFs, flattening paths.
unzip -o -q -j "$TMP/corpus.zip" '*/PDF_UA-1/*.pdf' '*/PDF_UA-1/*/*.pdf' \
    '*/PDF_UA-2/*.pdf' '*/PDF_UA-2/*/*.pdf' -d "$TARGET" 2>/dev/null || true

kept=$(find "$TARGET" -name '*.pdf' | wc -l | tr -d ' ')
if [ "$kept" -eq 0 ]; then
    echo "no PDF_UA files extracted — the archive layout may have changed; check $ZIP_URL" >&2
    exit 1
fi

manifest="$TARGET/.excise-manifest.tsv"
: > "$manifest"
sha() { command -v sha256sum >/dev/null && sha256sum "$1" | awk '{print $1}' || shasum -a 256 "$1" | awk '{print $1}'; }
for pdf in "$TARGET"/*.pdf; do
    [ -e "$pdf" ] || continue
    printf '%s\t%s\t%s\n' "$(sha "$pdf")" "$(basename "$pdf")" "$(wc -c < "$pdf" | tr -d ' ')" >> "$manifest"
done
echo "kept $kept PDF/UA (Matterhorn) fixtures in $TARGET (manifest: $manifest)"
