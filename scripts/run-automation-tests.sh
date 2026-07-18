#!/bin/bash
# Run automation script tests and provide concise summary
# This script is designed to minimize token usage when reviewing results
#
# Usage:
#   ./run-automation-tests.sh           # Build first, then test (with logging)
#   ./run-automation-tests.sh --no-build # Skip build, just test (with logging)
#
# Output is automatically logged to logs/automation_tests_TIMESTAMP.log
# Live output is shown on screen AND saved to log file

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_ROOT/logs"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/automation_tests_$TIMESTAMP.log"

# Parse arguments
SKIP_BUILD=false
if [[ "$1" == "--no-build" ]]; then
    SKIP_BUILD=true
fi

# Create logs directory BEFORE setting up redirection
mkdir -p "$LOG_DIR"

# Redirect all output to both console and log file using tee
exec > >(tee "$LOG_FILE")
exec 2>&1

echo "================================================="
echo "Automation Script Tests"
echo "================================================="
echo ""
echo "Log file: $LOG_FILE"
echo ""

cd "$PROJECT_ROOT"

# Build tests first (unless --no-build specified)
if [ "$SKIP_BUILD" = false ]; then
    echo "Building tests to pick up latest code changes..."
    dotnet build Excise.App.Tests/Excise.App.Tests.csproj --nologo -v quiet
    if [ $? -eq 0 ]; then
        echo "✅ Build successful"
    else
        echo "❌ Build failed"
        exit 1
    fi
    echo ""
fi

# Run tests (output already redirected to both console and log via exec above)
echo "Running: dotnet test --filter \"AutomationScript\" ..."
echo ""
echo "Live output below:"
echo "================================================="
echo ""

dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
    --filter "AutomationScript" \
    --logger "console;verbosity=normal" \
    --no-build

EXIT_CODE=$?

echo "================================================="
echo "Test Results Summary"
echo "================================================="
echo ""

# Extract summary from log
if [ $EXIT_CODE -eq 0 ]; then
    echo "✅ ALL TESTS PASSED"
    echo ""

    # Show passing tests count
    grep -E "Passed|Total" "$LOG_FILE" | tail -1

    # Show individual test results (concise)
    echo ""
    echo "Individual Results:"
    grep -E "AutomationScript.*\[PASS\]|AutomationScript.*PASS" "$LOG_FILE" || echo "  (See log for details)"
else
    echo "❌ TESTS FAILED"
    echo ""

    # Show failure summary
    grep -E "Failed|Passed|Skipped|Total" "$LOG_FILE" | tail -1

    # Show which tests failed
    echo ""
    echo "Failed Tests:"
    grep -E "AutomationScript.*\[FAIL\]|AutomationScript.*FAIL" "$LOG_FILE" || echo "  (See log for details)"

    # Show error messages (first 20 lines of errors)
    echo ""
    echo "Error Preview (first 20 lines):"
    grep -A 5 "Error Message:" "$LOG_FILE" | head -20

    echo ""
    echo "Full details in: $LOG_FILE"
fi

echo ""
echo "================================================="
echo ""

# Token-efficient summary for Claude
echo "SUMMARY FOR REVIEW:"
echo "-------------------"
echo "Exit code: $EXIT_CODE"
if [ $EXIT_CODE -eq 0 ]; then
    echo "Status: ✅ SUCCESS"
    grep -E "Total tests:|Passed:" "$LOG_FILE" | head -2
else
    echo "Status: ❌ FAILED"
    grep -E "Total tests:|Failed:|Passed:" "$LOG_FILE" | head -3

    echo ""
    echo "Failed test names:"
    grep -oE "Excise.App\.Tests\.UI\.AutomationScriptTests\.[A-Za-z_]+" "$LOG_FILE" | \
        grep -v "ExecutesSuccessfully" | head -5
fi

echo ""
echo "For detailed review, paste the LAST 100 LINES of: $LOG_FILE"
echo "Command: tail -100 $LOG_FILE"

exit $EXIT_CODE
