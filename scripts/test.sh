#!/bin/bash
# Run excise test suites
# Usage: ./test.sh [OPTIONS] [SUITE...]
#
# Suites: all, integration, redaction, metadata, forensic, external, unit

set -e

# Source common functions
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$SCRIPT_DIR/common.sh"

# Initialize logging
init_logging "test"

# Configuration
TEST_SUITES=()
VERBOSE=false
BUILD_FIRST=true
FILTER=""
LIST_ONLY=false

# Parse arguments
while [[ $# -gt 0 ]]; do
    case $1 in
        --verbose|-v)
            VERBOSE=true
            shift
            ;;
        --no-build)
            BUILD_FIRST=false
            shift
            ;;
        --filter|-f)
            FILTER="$2"
            shift 2
            ;;
        --list)
            LIST_ONLY=true
            shift
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS] [SUITE...]"
            echo ""
            echo "Test Suites:"
            echo "  all         Run all tests (default)"
            echo "  integration Run integration tests"
            echo "  redaction   Run redaction-related tests"
            echo "  excessive   Run ExcessiveRedactionTests"
            echo "  forensic    Run ForensicRedactionVerificationTests"
            echo "  metadata    Run metadata sanitization tests"
            echo "  external    Run external tool validation tests"
            echo "  unit        Run unit tests only"
            echo ""
            echo "Options:"
            echo "  --verbose, -v     Show detailed test output"
            echo "  --no-build        Skip building before testing"
            echo "  --filter, -f      Custom filter (passed to dotnet test)"
            echo "  --list            List available test classes"
            echo "  --help, -h        Show this help"
            echo ""
            echo "Examples:"
            echo "  $0                      # Run all tests"
            echo "  $0 redaction            # Run redaction tests"
            echo "  $0 forensic external    # Run forensic and external tests"
            echo "  $0 -f 'Manafort'        # Run tests containing 'Manafort'"
            echo ""
            exit 0
            ;;
        all|integration|redaction|excessive|forensic|metadata|external|unit)
            TEST_SUITES+=("$1")
            shift
            ;;
        *)
            log_error "Unknown option: $1"
            log_error "Use --help for usage"
            exit 1
            ;;
    esac
done

# Default to all if no suites specified
if [ ${#TEST_SUITES[@]} -eq 0 ]; then
    TEST_SUITES=("all")
fi

log_section "PDF Editor - Test Runner"

log_info "Test suites: ${TEST_SUITES[*]}"
if [ -n "$FILTER" ]; then
    log_info "Custom filter: $FILTER"
fi
log ""

# Find .NET
find_dotnet || exit 1

cd "$PROJECT_ROOT"

# List available tests
if [ "$LIST_ONLY" = true ]; then
    log_section "Available Test Classes"

    cd Excise.App.Tests

    # Build if needed
    if [ "$BUILD_FIRST" = true ]; then
        $DOTNET_CMD build -c Release > /dev/null 2>&1
    fi

    # List test classes
    $DOTNET_CMD test --list-tests 2>/dev/null | grep -E "^\s+\S+Tests\." | sed 's/^\s*//' | cut -d'.' -f1 | sort | uniq | while read class; do
        log "  $class"
    done

    exit 0
fi

# Build tests first
if [ "$BUILD_FIRST" = true ]; then
    log_section "Building Test Project"
    run_cmd "Building..." $DOTNET_CMD build Excise.App.Tests/Excise.App.Tests.csproj -c Release
fi

# Map suite names to filters
get_filter() {
    local suite="$1"
    case $suite in
        all)
            echo ""
            ;;
        integration)
            echo "FullyQualifiedName~Integration"
            ;;
        redaction)
            echo "FullyQualifiedName~Redaction"
            ;;
        excessive)
            echo "FullyQualifiedName~ExcessiveRedaction"
            ;;
        forensic)
            echo "FullyQualifiedName~ForensicRedaction"
            ;;
        metadata)
            echo "FullyQualifiedName~Metadata"
            ;;
        external)
            echo "FullyQualifiedName~ExternalTool"
            ;;
        unit)
            echo "FullyQualifiedName~Unit"
            ;;
        *)
            echo "FullyQualifiedName~$suite"
            ;;
    esac
}

# Run tests for a suite
run_suite() {
    local suite="$1"
    local filter=$(get_filter "$suite")

    if [ "$suite" != "all" ]; then
        log_section "Running $suite tests"
    else
        log_section "Running all tests"
    fi

    local test_args="-c Release --no-build"

    if [ -n "$filter" ]; then
        test_args="$test_args --filter \"$filter\""
    fi

    if [ "$VERBOSE" = true ]; then
        test_args="$test_args --logger \"console;verbosity=detailed\""
    else
        test_args="$test_args --logger \"console;verbosity=normal\""
    fi

    cd "$PROJECT_ROOT/Excise.App.Tests"

    # Run tests
    local exit_code=0
    if [ -n "$LOG_FILE" ]; then
        eval "$DOTNET_CMD test $test_args" 2>&1 | tee -a "$LOG_FILE" || exit_code=${PIPESTATUS[0]}
    else
        eval "$DOTNET_CMD test $test_args" || exit_code=$?
    fi

    cd "$PROJECT_ROOT"

    if [ $exit_code -eq 0 ]; then
        log_success "$suite tests passed"
    else
        log_error "$suite tests failed (exit code: $exit_code)"
    fi

    return $exit_code
}

# Main execution
main() {
    local failed_suites=()
    local passed_suites=()

    # Use custom filter if provided
    if [ -n "$FILTER" ]; then
        log_section "Running tests with custom filter"

        local test_args="-c Release --no-build --filter \"$FILTER\""
        if [ "$VERBOSE" = true ]; then
            test_args="$test_args --logger \"console;verbosity=detailed\""
        else
            test_args="$test_args --logger \"console;verbosity=normal\""
        fi

        cd "$PROJECT_ROOT/Excise.App.Tests"

        if eval "$DOTNET_CMD test $test_args" 2>&1 | tee -a "$LOG_FILE"; then
            passed_suites+=("custom")
        else
            failed_suites+=("custom")
        fi

        cd "$PROJECT_ROOT"
    else
        # Run each suite
        for suite in "${TEST_SUITES[@]}"; do
            if run_suite "$suite"; then
                passed_suites+=("$suite")
            else
                failed_suites+=("$suite")
            fi
        done
    fi

    # Summary
    log ""
    log_section "Test Summary"

    if [ ${#passed_suites[@]} -gt 0 ]; then
        log_success "Passed: ${passed_suites[*]}"
    fi

    if [ ${#failed_suites[@]} -gt 0 ]; then
        log_error "Failed: ${failed_suites[*]}"
        print_summary "test" "failed"
        exit 1
    fi

    print_summary "test" "success"
}

main "$@"
