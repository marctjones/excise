#!/bin/bash
# Run veraPDF corpus tests with logging
# These tests process thousands of PDFs and can take several minutes

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_ROOT/logs"
TEST_PDF_DIR="$PROJECT_ROOT/test-pdfs"

# Create logs directory
mkdir -p "$LOG_DIR"

# Generate log filename with timestamp
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/corpus_test_$TIMESTAMP.log"

echo "================================================="
echo "veraPDF Corpus Test Runner"
echo "================================================="
echo ""
echo "Log file: $LOG_FILE"
echo ""

# Check if corpus exists
if [ ! -d "$TEST_PDF_DIR/verapdf-corpus" ]; then
    echo "ERROR: veraPDF corpus not found!"
    echo ""
    echo "Please run the download script first:"
    echo "  ./scripts/download-test-pdfs.sh"
    echo ""
    exit 1
fi

# Count PDFs in corpus
PDF_COUNT=$(find "$TEST_PDF_DIR/verapdf-corpus" -name "*.pdf" 2>/dev/null | wc -l)
echo "Found $PDF_COUNT PDFs in corpus"
echo ""

# Run tests
echo "Running corpus tests..."
echo "This may take several minutes. Output is being logged."
echo ""
echo "To view live output, run:"
echo "  tail -f $LOG_FILE"
echo ""

cd "$PROJECT_ROOT"

# Run tests with Corpus category, capturing all output
{
    echo "=========================================="
    echo "Corpus Test Run: $TIMESTAMP"
    echo "=========================================="
    echo ""
    echo "PDF Count: $PDF_COUNT"
    echo "Test Project: Pdfe.Rendering.Tests"
    echo ""

    dotnet test Pdfe.Rendering.Tests --filter "FullyQualifiedName~Corpus" --logger "console;verbosity=detailed" 2>&1

    echo ""
    echo "=========================================="
    echo "Test Run Complete: $(date)"
    echo "=========================================="
} | tee "$LOG_FILE"

echo ""
echo "Results saved to: $LOG_FILE"
echo ""
