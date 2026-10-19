#!/bin/bash
# Download PDF Association test suites for visual rendering and redaction testing
# These are NOT checked into the repository, only downloaded on demand
#
# Both corpora are pinned to exact bytes (#1727). The expectation manifests
# tests/corpus-expectations-verapdf.tsv (2,694 rows) and
# tests/corpus-expectations-isartor.tsv (205 rows) were generated against
# these bytes; a re-pin is ONE commit that changes the pin below and
# regenerates those manifests with scripts/update-corpus-expectations.sh.
#
# Each archive is downloaded to a .part file, its SHA-256 is checked, and only
# then is it extracted. A mismatch deletes the download and exits non-zero;
# nothing is extracted from bytes that are not the pinned ones.
#
# EXCISE_TEST_PDF_DIR overrides the target directory (default: test-pdfs/).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TEST_PDF_DIR="${EXCISE_TEST_PDF_DIR:-$PROJECT_ROOT/test-pdfs}"

# veraPDF corpus: a commit, not a branch. This is the `master` head the
# expectations were generated against (master has not moved since 2025-04-29;
# the repo's default branch is now `staging`). The archive extracts to
# veraPDF-corpus-<sha>/ and is renamed to veraPDF-corpus-master/, the path the
# expectations and tests use.
VERAPDF_COMMIT="377728596647e40edf16003d68d620773ce96ec5"
VERAPDF_SHA256="65dbc174ec3741596fa7f12559f8c1a1599e581cdd48fe386adf01e3444f7742"
VERAPDF_URLS=("https://github.com/veraPDF/veraPDF-corpus/archive/$VERAPDF_COMMIT.zip")

# Isartor: a dated release file. pdfa.org answers scripted requests with 403,
# so the Internet Archive copy is the fallback; its capture digest has been
# identical in every snapshot from 2014 to 2025.
ISARTOR_SHA256="ba19e1319547ced813310c3e492ce0fcfad3d6afb96b093ec57f63aca52cb98d"
ISARTOR_URLS=(
    "https://www.pdfa.org/wp-content/uploads/2011/08/isartor-pdfa-2008-08-13.zip"
    "https://web.archive.org/web/20250113162057id_/https://pdfa.org/wp-content/uploads/2011/08/isartor-pdfa-2008-08-13.zip"
)

echo "================================================="
echo "PDF Test Suite Downloader"
echo "================================================="
echo ""

mkdir -p "$TEST_PDF_DIR"
cd "$TEST_PDF_DIR"

echo "Target directory: $TEST_PDF_DIR"
echo ""

sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | awk '{print $1}'
    else shasum -a 256 "$1" | awk '{print $1}'; fi
}

# fetch_verified <archive> <sha256> <url>...: leave <archive> in place only if
# its SHA-256 matches. A present archive is re-verified, never trusted.
fetch_verified() {
    local archive=$1 want=$2; shift 2
    local got url
    if [ -f "$archive" ]; then
        got="$(sha256_of "$archive")"
        if [ "$got" = "$want" ]; then return 0; fi
        echo "✗ $archive is present but does NOT match its pinned sha256" >&2
        echo "    expected $want" >&2
        echo "    actual   $got" >&2
        rm -f "$archive"
        return 1
    fi
    for url in "$@"; do
        echo "  URL: $url"
        rm -f "$archive.part"
        if ! curl -fsSL --retry 3 -o "$archive.part" "$url"; then
            echo "  (download failed, trying next source)"
            continue
        fi
        got="$(sha256_of "$archive.part")"
        if [ "$got" != "$want" ]; then
            echo "✗ sha256 mismatch for $archive from $url" >&2
            echo "    expected $want" >&2
            echo "    actual   $got" >&2
            rm -f "$archive.part"
            return 1
        fi
        mv "$archive.part" "$archive"
        return 0
    done
    echo "✗ no source served $archive" >&2
    return 1
}

# download_suite <name> <extract_dir> <archive> <sha256> <rename_from> <rename_to> <url>...
# rename_from/rename_to map the archive's top-level directory onto the layout
# the expectations use; pass "" "" when the archive already has that layout.
download_suite() {
    local name=$1 extract_dir=$2 archive=$3 want=$4 from=$5 to=$6; shift 6

    if [ -d "$extract_dir" ]; then
        echo "✓ $name already downloaded (found $extract_dir)"
        return 0
    fi

    echo "→ Downloading $name..."
    if ! fetch_verified "$archive" "$want" "$@"; then
        echo "✗ $name NOT extracted: the archive is not the pinned one (#1727)" >&2
        exit 1
    fi
    echo "  sha256 OK: $want"

    echo "→ Extracting $name..."
    local tmp="$extract_dir.extracting"
    rm -rf "$tmp"
    unzip -q "$archive" -d "$tmp"
    if [ -n "$from" ]; then
        mv "$tmp/$from" "$tmp/$to"
    fi
    mv "$tmp" "$extract_dir"

    echo "✓ $name downloaded and extracted to $extract_dir"
    echo ""
}

echo "1. veraPDF Corpus (PDF/A test files)"
echo "-----------------------------------"
download_suite "veraPDF Corpus" "verapdf-corpus" "veraPDF-corpus-$VERAPDF_COMMIT.zip" \
    "$VERAPDF_SHA256" "veraPDF-corpus-$VERAPDF_COMMIT" "veraPDF-corpus-master" \
    "${VERAPDF_URLS[@]}"

echo "2. Isartor Test Suite (PDF/A-1 conformance)"
echo "--------------------------------------------"
download_suite "Isartor Test Suite" "isartor" "isartor-pdfa-2008-08-13.zip" \
    "$ISARTOR_SHA256" "" "" \
    "${ISARTOR_URLS[@]}"

# Download PDF Sample Files (from PDF Association if available)
echo "3. Additional PDF Samples"
echo "-------------------------"
# Create a samples directory with some common challenging PDFs
mkdir -p samples

echo ""
echo "================================================="
echo "Download Complete!"
echo "================================================="
echo ""
echo "Test PDFs are located in: $TEST_PDF_DIR"
echo ""
echo "Directory structure:"
echo "  - verapdf-corpus/    : PDF/A test files (veraPDF-corpus @ ${VERAPDF_COMMIT:0:12})"
echo "  - isartor/           : PDF/A-1 conformance tests (2008-08-13 release)"
echo "  - samples/           : Additional test samples"
echo ""
echo "This script fetches the two PDF Association corpora only. The corpus"
echo "rendering scan covers four; the other two have their own scripts:"
echo "  ./scripts/download-pdfjs-corpus.sh    (Mozilla, 685 files)"
echo "  ./scripts/download-pdfium-corpus.sh   (Chrome, 331 files)"
echo "Run ./scripts/check-test-prereqs.sh to see which are present."
echo ""
echo "Note: These files are gitignored and not committed to the repository"
echo ""
