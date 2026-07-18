#!/bin/bash
# Run all tests with logging
# Use this for comprehensive test runs instead of running directly in Claude Code

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_ROOT/logs"

# Create logs directory
mkdir -p "$LOG_DIR"

# Generate log filename with timestamp
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/test_$TIMESTAMP.log"

echo "================================================="
echo "Test Runner"
echo "================================================="
echo ""
echo "Log file: $LOG_FILE"
echo ""

# Parse arguments
FILTER=""
PROJECT=""
VERBOSITY="normal"

while [[ $# -gt 0 ]]; do
    case $1 in
        --filter)
            FILTER="$2"
            shift 2
            ;;
        --project)
            PROJECT="$2"
            shift 2
            ;;
        --verbose)
            VERBOSITY="detailed"
            shift
            ;;
        --help)
            echo "Usage: $0 [options]"
            echo ""
            echo "Options:"
            echo "  --filter <filter>   Filter tests by pattern"
            echo "  --project <name>    Run specific test project"
            echo "  --verbose           Show detailed output"
            echo ""
            echo "Examples:"
            echo "  $0                              # Run all tests"
            echo "  $0 --filter Redaction           # Run tests matching 'Redaction'"
            echo "  $0 --project Excise.App.Tests    # Run specific project"
            echo "  $0 --verbose                    # Detailed output"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            exit 1
            ;;
    esac
done

cd "$PROJECT_ROOT"

# Build command
CMD="dotnet test"

if [ -n "$PROJECT" ]; then
    CMD="$CMD $PROJECT"
fi

if [ -n "$FILTER" ]; then
    CMD="$CMD --filter \"$FILTER\""
fi

CMD="$CMD --logger \"console;verbosity=$VERBOSITY\""

echo "Running: $CMD"
echo ""
echo "To view live output, run:"
echo "  tail -f $LOG_FILE"
echo ""

# Run tests
{
    echo "=========================================="
    echo "Test Run: $TIMESTAMP"
    echo "=========================================="
    echo ""
    echo "Command: $CMD"
    echo ""

    eval $CMD 2>&1

    echo ""
    echo "=========================================="
    echo "Test Run Complete: $(date)"
    echo "=========================================="
} | tee "$LOG_FILE"

echo ""
echo "Results saved to: $LOG_FILE"
echo ""
