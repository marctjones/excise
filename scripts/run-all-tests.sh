#!/bin/bash
# Run ALL tests in the solution
#
# Usage:
#   ./run-all-tests.sh              # Default: show test class summaries
#   ./run-all-tests.sh --no-build   # Skip build
#   ./run-all-tests.sh -v           # Verbose (show each test)
#   ./run-all-tests.sh -q           # Quiet (project totals only)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
cd "$PROJECT_ROOT"

# Parse arguments
NO_BUILD_ARG=""
VERBOSE=""
QUIET=""
for arg in "$@"; do
    case $arg in
        --no-build) NO_BUILD_ARG="--no-build" ;;
        -v|--verbose) VERBOSE=1 ;;
        -q|--quiet) QUIET=1 ;;
    esac
done

echo "pdfe Test Suite"
echo "==============="

# Build if needed
if [[ -z "$NO_BUILD_ARG" ]]; then
    printf "Building... "
    dotnet build PdfEditor.Tests --nologo -v quiet >/dev/null 2>&1
    dotnet build Pdfe.Core.Tests --nologo -v quiet >/dev/null 2>&1
    dotnet build Pdfe.Rendering.Tests --nologo -v quiet >/dev/null 2>&1
    echo "done"
    NO_BUILD_ARG="--no-build"
fi

OVERALL_EXIT=0
TOTAL_PASSED=0
TOTAL_FAILED=0
TOTAL_SKIPPED=0

# Run tests for a project with live counter
run_tests() {
    local project=$1
    local name=$2

    echo ""
    echo "▶ $name"

    if [[ -n "$VERBOSE" ]]; then
        # Verbose: show all test output
        dotnet test "$project" $NO_BUILD_ARG --logger "console;verbosity=normal" 2>&1 | \
            grep -E "^\s+(Passed|Failed|Skipped)" | head -200
        return
    fi

    # Run tests and show live progress counter
    local tmpfile=$(mktemp)
    local countfile=$(mktemp)
    echo "0" > "$countfile"

    # Run dotnet test in background, tail the output for progress
    dotnet test "$project" $NO_BUILD_ARG --logger "console;verbosity=normal" > "$tmpfile" 2>&1 &
    local pid=$!

    # Monitor progress while test runs
    while kill -0 $pid 2>/dev/null; do
        if [[ -f "$tmpfile" ]]; then
            local count
            count=$(grep -cE "^\s*(Passed|Failed)" "$tmpfile" 2>/dev/null) || count=0
            printf "\r  [%d] Running..." "$count"
        fi
        sleep 0.3
    done
    wait $pid

    local final_count
    final_count=$(grep -cE "^\s*(Passed|Failed)" "$tmpfile" 2>/dev/null) || final_count=0
    printf "\r  [%d] Done       \n" "$final_count"

    local output
    output=$(tr -d '\0' < "$tmpfile")
    rm -f "$tmpfile" "$countfile"

    # Parse summary - format varies:
    # Console logger: "Total tests: 75\n     Passed: 75\n     Failed: 0"
    # Or: "Passed!  - Failed: 0, Passed: 1020, Skipped: 14"
    local passed failed skipped

    # Try format 1: "     Passed: N" on its own line
    passed=$(echo "$output" | grep -E "^[[:space:]]*Passed:" | tail -1 | sed 's/.*Passed:[[:space:]]*\([0-9]*\).*/\1/')
    failed=$(echo "$output" | grep -E "^[[:space:]]*Failed:" | tail -1 | sed 's/.*Failed:[[:space:]]*\([0-9]*\).*/\1/')
    skipped=$(echo "$output" | grep -E "^[[:space:]]*Skipped:" | tail -1 | sed 's/.*Skipped:[[:space:]]*\([0-9]*\).*/\1/')

    # Default to 0 if not found
    passed=${passed:-0}
    failed=${failed:-0}
    skipped=${skipped:-0}

    TOTAL_PASSED=$((TOTAL_PASSED + passed))
    TOTAL_FAILED=$((TOTAL_FAILED + failed))
    TOTAL_SKIPPED=$((TOTAL_SKIPPED + skipped))

    if [[ -n "$QUIET" ]]; then
        # Quiet: single line per project
        if [[ "$failed" -gt 0 ]]; then
            printf "  ✗ %d failed, %d passed\n" "$failed" "$passed"
            OVERALL_EXIT=1
        elif [[ "$skipped" -gt 0 ]]; then
            printf "  ✓ %d passed, %d skipped\n" "$passed" "$skipped"
        else
            printf "  ✓ %d passed\n" "$passed"
        fi
    else
        # Default: show test class summaries
        echo "$output" | grep -E "^\s*(Passed|Failed)" | \
            sed 's/^\s*//' | \
            while read line; do
                local status=$(echo "$line" | awk '{print $1}')
                local fullname=$(echo "$line" | awk '{print $2}')
                local classpart=$(echo "$fullname" | rev | cut -d'.' -f2- | rev)
                echo "$status $classpart"
            done | sort -u | \
            while read status classname; do
                local category=$(echo "$classname" | grep -oP '\.(Unit|Integration|UI|Security)\.' | tr -d '.')
                local shortclass=$(echo "$classname" | rev | cut -d'.' -f1 | rev)
                if [[ -n "$category" ]]; then
                    if [[ "$status" == "Passed" ]]; then
                        echo "  ✓ [$category] $shortclass"
                    else
                        echo "  ✗ [$category] $shortclass"
                    fi
                fi
            done

        # Show project summary
        echo "  ─────────────────────────────"
        if [[ "$failed" -gt 0 ]]; then
            echo "  Total: ✗ $failed failed, $passed passed"
            OVERALL_EXIT=1
        elif [[ "$skipped" -gt 0 ]]; then
            echo "  Total: ✓ $passed passed, $skipped skipped"
        else
            echo "  Total: ✓ $passed passed"
        fi
    fi
}

run_tests "PdfEditor.Tests" "PdfEditor.Tests"
run_tests "Pdfe.Core.Tests" "Pdfe.Core.Tests"
run_tests "Pdfe.Rendering.Tests" "Pdfe.Rendering.Tests"

echo ""
echo "═══════════════════════════════"
if [ $OVERALL_EXIT -eq 0 ]; then
    printf "TOTAL: ✓ %d passed" $TOTAL_PASSED
    if [[ $TOTAL_SKIPPED -gt 0 ]]; then
        printf ", %d skipped" $TOTAL_SKIPPED
    fi
    echo ""
    echo "All tests passed!"
else
    echo "TOTAL: ✗ $TOTAL_FAILED failed, $TOTAL_PASSED passed"
    echo "Some tests failed."
fi

exit $OVERALL_EXIT
