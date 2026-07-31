#!/bin/bash
# Download PDF Association test suites for visual rendering and redaction testing
# These are NOT checked into the repository, only downloaded on demand

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TEST_PDF_DIR="$PROJECT_ROOT/test-pdfs"

echo "================================================="
echo "PDF Test Suite Downloader"
echo "================================================="
echo ""

# Create test-pdfs directory
mkdir -p "$TEST_PDF_DIR"
cd "$TEST_PDF_DIR"

echo "Target directory: $TEST_PDF_DIR"
echo ""

# Function to download and extract
download_suite() {
    local name=$1
    local url=$2
    local extract_dir=$3
    
    if [ -d "$extract_dir" ]; then
        echo "✓ $name already downloaded (found $extract_dir)"
        return 0
    fi
    
    echo "→ Downloading $name..."
    echo "  URL: $url"
    
    local filename=$(basename "$url")
    
    if [ ! -f "$filename" ]; then
        wget -q --show-progress "$url" || curl -L -o "$filename" "$url"
    fi
    
    echo "→ Extracting $name..."
    
    if [[ "$filename" == *.zip ]]; then
        unzip -q "$filename" -d "$extract_dir"
    elif [[ "$filename" == *.tar.gz ]] || [[ "$filename" == *.tgz ]]; then
        mkdir -p "$extract_dir"
        tar -xzf "$filename" -C "$extract_dir"
    fi
    
    echo "✓ $name downloaded and extracted to $extract_dir"
    echo ""
}

# Download veraPDF Corpus
echo "1. veraPDF Corpus (PDF/A test files)"
echo "-----------------------------------"
VERAPDF_URL="https://github.com/veraPDF/veraPDF-corpus/archive/refs/heads/master.zip"
download_suite "veraPDF Corpus" "$VERAPDF_URL" "verapdf-corpus"

# Download Isartor Test Suite
echo "2. Isartor Test Suite (PDF/A-1 conformance)"
echo "--------------------------------------------"
ISARTOR_URL="https://www.pdfa.org/wp-content/uploads/2011/08/isartor-pdfa-2008-08-13.zip"
download_suite "Isartor Test Suite" "$ISARTOR_URL" "isartor"

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
echo "  - verapdf-corpus/    : PDF/A test files"
echo "  - isartor/           : PDF/A-1 conformance tests"
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
