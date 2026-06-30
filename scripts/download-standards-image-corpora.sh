#!/usr/bin/env bash
# Download or refresh the public PDF corpuses used to challenge image/filter
# rendering conformance. The large downloaded PDFs remain under test-pdfs/ and
# are ignored by git; the coverage matrix is versioned separately under
# test-pdfs/manifests/.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
TEST_PDFS="$ROOT/test-pdfs"

INCLUDE_LARGE=0
SKIP_EXISTING=1

usage() {
    cat <<'EOF'
Download public PDF corpuses for image/filter rendering conformance.

Usage:
  scripts/download-standards-image-corpora.sh [options]

Options:
  --include-large       Also download large print/color PDFs such as Altona 2.0.
  --force               Re-download direct-file corpuses even if files exist.
  -h, --help            Show this help.

Always refreshes:
  - pdf.js corpus
  - Poppler corpus
  - veraPDF and Isartor corpuses
  - federal/smoke corpuses

Direct-download print/color corpuses:
  - Altona 1.2 files by default
  - Altona 2.0 Technical Page files with --include-large
  - Ghent PDF Output Suite V50 Test Pages and Patches
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-large) INCLUDE_LARGE=1; shift ;;
        --force) SKIP_EXISTING=0; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$TEST_PDFS"

download_file() {
    local name="$1"
    local url="$2"
    local dest="$3"

    mkdir -p "$(dirname "$dest")"
    if [[ "$SKIP_EXISTING" == "1" && -s "$dest" ]]; then
        echo "already current: $name -> ${dest#$ROOT/}"
        return 0
    fi

    echo "downloading: $name"
    echo "  $url"
    curl -fL --retry 3 --retry-delay 2 --connect-timeout 20 -o "$dest.tmp" "$url"
    mv "$dest.tmp" "$dest"
}

extract_zip() {
    local name="$1"
    local archive="$2"
    local dest="$3"

    if [[ "$SKIP_EXISTING" == "1" && -d "$dest" && -n "$(find "$dest" -type f -name '*.pdf' -print -quit)" ]]; then
        echo "already extracted: $name -> ${dest#$ROOT/}"
        return 0
    fi

    echo "extracting: $name -> ${dest#$ROOT/}"
    rm -rf "$dest"
    mkdir -p "$dest"
    unzip -oq "$archive" -d "$dest"
}

write_manifest() {
    local corpus_dir="$1"
    local manifest="$corpus_dir/.pdfe-manifest.tsv"
    [[ -d "$corpus_dir" ]] || return 0

    python3 - "$corpus_dir" "$manifest" <<'PY'
import hashlib
import sys
from pathlib import Path

root = Path(sys.argv[1])
manifest = Path(sys.argv[2])

def sha256(path: Path) -> str:
    h = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    return h.hexdigest()

with manifest.open("w", encoding="utf-8") as out:
    for pdf in sorted(root.rglob("*.pdf"), key=lambda p: p.relative_to(root).as_posix()):
        rel = pdf.relative_to(root).as_posix()
        out.write(f"{sha256(pdf)}\t{rel}\t{pdf.stat().st_size}\n")
print(f"manifest: {manifest}")
PY
}

echo "== Core public corpuses =="
"$SCRIPT_DIR/download-pdfjs-corpus.sh"
"$SCRIPT_DIR/download-poppler-corpus.sh"
"$SCRIPT_DIR/download-test-pdfs.sh"
"$SCRIPT_DIR/download-smoke-corpus.sh"
"$SCRIPT_DIR/download-federal-corpus.sh"

echo
echo "== Altona print/color PDFs =="
ALTONA_DIR="$TEST_PDFS/altona"
download_file "Altona Measure 1.1a" \
    "https://eci.org/lib/exe/altona_measure_1v1a.pdf" \
    "$ALTONA_DIR/altona_measure_1v1a.pdf"
download_file "Altona Visual 1.2a X-3" \
    "https://eci.org/lib/exe/altona_visual_1v2a_x3.pdf" \
    "$ALTONA_DIR/altona_visual_1v2a_x3.pdf"
download_file "Altona Technical 1.2 X-3" \
    "https://eci.org/lib/exe/altona_technical_1v2_x3.pdf" \
    "$ALTONA_DIR/altona_technical_1v2_x3.pdf"
download_file "Altona 1.2 documentation" \
    "https://eci.org/lib/exe/altonatestsuite_documentation_eng.pdf" \
    "$ALTONA_DIR/altonatestsuite_documentation_eng.pdf"

if [[ "$INCLUDE_LARGE" == "1" ]]; then
    download_file "Altona 2.0 Technical Page 2 X-4" \
        "https://eci.org/lib/exe/eci_altona-test-suite-v2_technical2_x4.pdf" \
        "$ALTONA_DIR/eci_altona-test-suite-v2_technical2_x4.pdf"
    download_file "Altona 2.0 Technical Page 2 one patch per page X-4" \
        "https://eci.org/lib/exe/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf" \
        "$ALTONA_DIR/eci_altona-test-suite-v2_technical2_one-patch-per-page_x4.pdf"
    download_file "Altona 2.0 Technical Page 2 documentation" \
        "https://eci.org/lib/exe/eci_altona-test-suite-v2_technical2_documentation_eng-4.pdf" \
        "$ALTONA_DIR/eci_altona-test-suite-v2_technical2_documentation_eng-4.pdf"
else
    echo "skipping Altona 2.0 large PDFs; pass --include-large to download them"
fi
write_manifest "$ALTONA_DIR"

echo
echo "== Ghent PDF Output Suite =="
GHENT_DIR="$TEST_PDFS/ghent"
GHENT_ARCHIVES="$GHENT_DIR/archives"
GHENT_EXTRACTED="$GHENT_DIR/extracted"
download_file "Ghent PDF Output Suite V50 Test Pages" \
    "https://gwg.org/?wpdmdl=9080" \
    "$GHENT_ARCHIVES/Ghent_PDF_Output_Suite_V50_Testpages.zip"
download_file "Ghent PDF Output Suite V50 Patches" \
    "https://gwg.org/?wpdmdl=9076" \
    "$GHENT_ARCHIVES/Ghent_PDF_Output_Suite_V50_Patches.zip"
extract_zip "Ghent PDF Output Suite V50 Test Pages" \
    "$GHENT_ARCHIVES/Ghent_PDF_Output_Suite_V50_Testpages.zip" \
    "$GHENT_EXTRACTED/testpages"
extract_zip "Ghent PDF Output Suite V50 Patches" \
    "$GHENT_ARCHIVES/Ghent_PDF_Output_Suite_V50_Patches.zip" \
    "$GHENT_EXTRACTED/patches"
write_manifest "$GHENT_DIR"

echo
echo "== Image feature inventory =="
"$SCRIPT_DIR/build-image-feature-inventory.py" \
    --corpus "$TEST_PDFS" \
    --matrix "$TEST_PDFS/manifests/pdf-image-feature-matrix.json" \
    --output "$ROOT/logs/image-conformance/inventory.json" \
    --page-manifest "$ROOT/logs/image-conformance/all-image-pdfs.tsv"

echo
echo "Done. Run focused conformance/quality checks with:"
echo "  scripts/run-image-conformance-suite.sh --feature filter:JBIG2Decode --page-mode all"
echo "  scripts/run-image-conformance-suite.sh --feature filter:DCTDecode --page-mode sample --oracles all"
