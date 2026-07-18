#!/bin/bash
# Run all atomic test suites and save results
# Usage: ./scripts/run-atomic-tests.sh
#
# Output: Concise summary to terminal, full details to log file.
# Results are saved to logs/atomic-tests-TIMESTAMP.log

# NOTE: Do NOT use "set -e" - we want to continue running all phases even if some fail

# Timeout for each test phase (120 seconds - enough for corpus tests)
PHASE_TIMEOUT=120

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_DIR/logs"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/atomic-tests-$TIMESTAMP.log"

mkdir -p "$LOG_DIR"

echo "Atomic Test Suite Runner"
echo "Log: $LOG_FILE"
echo "Timeout per phase: ${PHASE_TIMEOUT}s"
echo ""

cd "$PROJECT_DIR"

# Build first (quiet)
echo -n "Building... "
BUILD_START=$SECONDS
if dotnet build Excise.App.Redaction.Tests --no-restore -v q > "$LOG_FILE" 2>&1; then
    echo "OK ($(($SECONDS - $BUILD_START))s)"
else
    echo "FAILED (see log)"
    exit 1
fi

run_test() {
    local name="$1"
    local filter="$2"
    local phase="$3"
    local start_time=$SECONDS

    echo -n "[$phase/4] $name... "
    echo "======================================================" >> "$LOG_FILE"
    echo "[$(date +%H:%M:%S)] [$phase/4] $name - STARTED" >> "$LOG_FILE"
    echo "======================================================" >> "$LOG_FILE"

    # Run with minimal verbosity, capture output
    local output
    local exit_code
    output=$(timeout $PHASE_TIMEOUT dotnet test Excise.App.Redaction.Tests \
        --filter "FullyQualifiedName~$filter" \
        --no-build \
        --verbosity minimal 2>&1) || exit_code=$?

    # If timeout didn't set exit_code, it succeeded
    exit_code=${exit_code:-0}

    local elapsed=$(($SECONDS - $start_time))

    # Log full output with timing
    echo "$output" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"
    echo "[$(date +%H:%M:%S)] Completed in ${elapsed}s (exit code: $exit_code)" >> "$LOG_FILE"
    echo "" >> "$LOG_FILE"

    # Parse results for summary - handle missing values
    local passed=$(echo "$output" | grep -oP "Passed:\s*\K\d+" | head -1)
    local failed=$(echo "$output" | grep -oP "Failed:\s*\K\d+" | head -1)
    local skipped=$(echo "$output" | grep -oP "Skipped:\s*\K\d+" | head -1)

    # Default to 0 if not found
    passed=${passed:-0}
    failed=${failed:-0}
    skipped=${skipped:-0}

    if [ $exit_code -eq 124 ]; then
        echo "TIMEOUT (${elapsed}s)"
        echo "  (killed after ${PHASE_TIMEOUT}s)"
        return 124
    elif [ $exit_code -eq 0 ]; then
        echo "PASS ($passed passed, $skipped skipped) ${elapsed}s"
        return 0
    else
        echo "FAIL ($failed failed, $passed passed) ${elapsed}s"
        # Show first few failure messages
        echo "$output" | grep -E "^\s*Failed\s" | head -5 | sed 's/^/  /'
        return $exit_code
    fi
}

TOTAL_START=$SECONDS

# Run all test suites - capture exit codes but CONTINUE running all phases
run_test "Font Preservation" "FontPreservationTests" 1
FONT_EXIT=$?

run_test "Content Stream" "ContentStreamHandlingTests" 2
CONTENT_EXIT=$?

run_test "PDF/A Compliance" "PdfAComplianceTests" 3
PDFA_EXIT=$?

run_test "Form Field Handling" "FormFieldHandlingTests" 4
FORM_EXIT=$?

TOTAL_ELAPSED=$(($SECONDS - $TOTAL_START))

# Summary
echo ""
echo "====== SUMMARY ======"
echo "Font:     $([ $FONT_EXIT -eq 0 ] && echo 'PASS' || echo 'FAIL')"
echo "Content:  $([ $CONTENT_EXIT -eq 0 ] && echo 'PASS' || echo 'FAIL')"
echo "PDF/A:    $([ $PDFA_EXIT -eq 0 ] && echo 'PASS' || echo 'FAIL')"
echo "Form:     $([ $FORM_EXIT -eq 0 ] && echo 'PASS' || echo 'FAIL')"
echo ""
echo "Total time: ${TOTAL_ELAPSED}s"
echo "Full log: $LOG_FILE"

# Exit with failure if any test suite failed
if [ $FONT_EXIT -ne 0 ] || [ $CONTENT_EXIT -ne 0 ] || [ $PDFA_EXIT -ne 0 ] || [ $FORM_EXIT -ne 0 ]; then
    exit 1
fi
