#!/bin/bash
# PDF 2.0 Phase 15: Conformance Validation Harness
#
# Runs the complete conformance test suite:
# - ConformanceTests (parse + render)
# - RoundTripTests (load → save → reload)
# - RedactionRegressionTests (glyph removal verification)
#
# Generates a summary report with pass rates by test class and corpus.
# For large corpus runs, set SKIP_LARGE_CORPUS=0 (default: 1 — skip for speed)

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOGS_DIR="$PROJECT_ROOT/logs"
TEST_PDF_DIR="$PROJECT_ROOT/test-pdfs"

# Create logs directory
mkdir -p "$LOGS_DIR"

# Generate timestamp for log files
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOGS_DIR/conformance_$TIMESTAMP.log"

# Color codes for output
COLOR_PASS='\033[0;32m'  # Green
COLOR_FAIL='\033[0;31m'  # Red
COLOR_SKIP='\033[0;33m'  # Yellow
COLOR_RESET='\033[0m'    # Reset

echo "================================================="
echo "PDF 2.0 Phase 15: Conformance Validation Harness"
echo "================================================="
echo ""
echo "Start time: $(date)"
echo "Log file: $LOG_FILE"
echo ""

# Function to count PDFs in corpus
count_pdfs() {
    local dir=$1
    if [ -d "$dir" ]; then
        find "$dir" -name "*.pdf" 2>/dev/null | wc -l
    else
        echo "0"
    fi
}

# Check corpus availability
SMOKE_COUNT=$(count_pdfs "$TEST_PDF_DIR/smoke")
VERAPDF_COUNT=$(count_pdfs "$TEST_PDF_DIR/verapdf-corpus")

echo "Corpus Status:"
echo "  Smoke corpus: $SMOKE_COUNT files"
if [ "$SMOKE_COUNT" -eq 0 ]; then
    echo "    (not found — run ./scripts/download-smoke-corpus.sh)"
fi
echo "  veraPDF corpus: $VERAPDF_COUNT files"
if [ "$VERAPDF_COUNT" -eq 0 ]; then
    echo "    (not found — run ./scripts/download-test-pdfs.sh)"
fi
echo ""

# Determine if we should skip large corpus
if [ -z "$SKIP_LARGE_CORPUS" ]; then
    SKIP_LARGE_CORPUS=1  # Default: skip large corpus for speed
fi

if [ "$SKIP_LARGE_CORPUS" -ne 0 ] && [ "$VERAPDF_COUNT" -gt 0 ]; then
    echo "Note: Large corpus skipping enabled. To run veraPDF corpus:"
    echo "  export SKIP_LARGE_CORPUS=0"
    echo "  ./scripts/run-conformance.sh"
    echo ""
fi

# Test configuration
cd "$PROJECT_ROOT"

# Build the test project first
echo "Building test project..."
if ! dotnet build Excise.Rendering.Tests/Excise.Rendering.Tests.csproj -c Debug > /dev/null 2>&1; then
    echo "ERROR: Build failed"
    exit 1
fi
echo "✓ Build successful"
echo ""

# Run tests and capture output
{
    echo "=========================================="
    echo "Conformance Test Run: $TIMESTAMP"
    echo "=========================================="
    echo ""

    # Track results
    CONFORMANCE_SMOKE_PASS=0
    CONFORMANCE_SMOKE_FAIL=0
    ROUNDTRIP_PASS=0
    ROUNDTRIP_FAIL=0
    REDACTION_PASS=0
    REDACTION_FAIL=0

    echo "1. Running Smoke Corpus Tests (Conformance)..."
    echo "   (8 real-world US government PDFs, ~10 seconds)"
    echo ""

    if dotnet test Excise.Rendering.Tests \
        --filter "FullyQualifiedName~ConformanceTests_SmokeCorpus" \
        --no-build -c Debug \
        --logger "console;verbosity=minimal" 2>&1; then
        CONFORMANCE_SMOKE_PASS=1
    else
        CONFORMANCE_SMOKE_FAIL=1
    fi

    echo ""
    echo "2. Running Round-Trip Tests..."
    echo "   (Load → Save → Reload verification, smoke corpus only)"
    echo ""

    if dotnet test Excise.Rendering.Tests \
        --filter "FullyQualifiedName~RoundTrip_LoadSaveReload" \
        --no-build -c Debug \
        --logger "console;verbosity=minimal" 2>&1; then
        ROUNDTRIP_PASS=1
    else
        ROUNDTRIP_FAIL=1
    fi

    echo ""
    echo "3. Running Redaction Regression Tests..."
    echo "   (Glyph-level removal verification, smoke corpus only)"
    echo ""

    if dotnet test Excise.Rendering.Tests \
        --filter "FullyQualifiedName~RedactionRegression" \
        --no-build -c Debug \
        --logger "console;verbosity=minimal" 2>&1; then
        REDACTION_PASS=1
    else
        REDACTION_FAIL=1
    fi

    # Run large corpus if enabled
    if [ "$SKIP_LARGE_CORPUS" -eq 0 ] && [ "$VERAPDF_COUNT" -gt 0 ]; then
        echo ""
        echo "4. Running veraPDF Corpus Tests (Large)..."
        echo "   (~2,694 ISO 32000-1/2 and PDF/A test files, ~20-30 minutes)"
        echo ""
        echo "   To monitor progress, run in another terminal:"
        echo "   tail -f $LOG_FILE"
        echo ""

        CONFORMANCE_VERAPDF_PASS=0
        CONFORMANCE_VERAPDF_FAIL=0

        if SKIP_LARGE_CORPUS=0 dotnet test Excise.Rendering.Tests \
            --filter "FullyQualifiedName~ConformanceTests_VeraPdfCorpus" \
            --no-build -c Debug \
            --logger "console;verbosity=minimal" 2>&1; then
            CONFORMANCE_VERAPDF_PASS=1
        else
            CONFORMANCE_VERAPDF_FAIL=1
        fi
    fi

    echo ""
    echo "=========================================="
    echo "Test Run Complete: $(date)"
    echo "=========================================="
    echo ""

    # Summary
    echo "SUMMARY"
    echo "======="
    echo ""

    if [ "$SMOKE_COUNT" -gt 0 ]; then
        echo "Smoke Corpus (8 files):"
        if [ "$CONFORMANCE_SMOKE_PASS" -eq 1 ]; then
            echo "  Conformance: ✓ PASS"
        else
            echo "  Conformance: ✗ FAIL"
        fi
        if [ "$ROUNDTRIP_PASS" -eq 1 ]; then
            echo "  Round-Trip:  ✓ PASS"
        else
            echo "  Round-Trip:  ✗ FAIL"
        fi
        if [ "$REDACTION_PASS" -eq 1 ]; then
            echo "  Redaction:   ✓ PASS"
        else
            echo "  Redaction:   ✗ FAIL"
        fi
        echo ""
    fi

    if [ "$SKIP_LARGE_CORPUS" -eq 0 ] && [ "$VERAPDF_COUNT" -gt 0 ]; then
        echo "veraPDF Corpus ($VERAPDF_COUNT files):"
        if [ "$CONFORMANCE_VERAPDF_PASS" -eq 1 ]; then
            echo "  Conformance: ✓ PASS"
        else
            echo "  Conformance: ✗ FAIL (see details above)"
        fi
        echo ""
    fi

    echo "Recommendations:"
    echo "  - Check log file for failed test details"
    echo "  - For smoke corpus: all tests should pass"
    echo "  - For veraPDF: expect ~85-95% pass rate (encrypted, XFA, malformed PDFs expected to fail)"
    echo ""

} 2>&1 | tee -a "$LOG_FILE"

# Also save structured results
RESULTS_FILE="$LOGS_DIR/conformance_results_$TIMESTAMP.json"
{
    echo "{"
    echo "  \"timestamp\": \"$TIMESTAMP\","
    echo "  \"smoke_corpus_count\": $SMOKE_COUNT,"
    echo "  \"verapdf_corpus_count\": $VERAPDF_COUNT,"
    echo "  \"skip_large_corpus\": $SKIP_LARGE_CORPUS,"
    echo "  \"log_file\": \"$LOG_FILE\""
    echo "}"
} > "$RESULTS_FILE"

echo ""
echo "=========================================="
echo "Results saved to:"
echo "  Log:     $LOG_FILE"
echo "  Results: $RESULTS_FILE"
echo "=========================================="
echo ""
