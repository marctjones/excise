#!/bin/bash

# PDF Redaction Library Test Script
# This script provides an easy command-line interface for testing the PDF redaction functionality

set -e  # Exit on error

echo "========================================="
echo "PDF Redaction Library - Test Script"
echo "========================================="
echo ""

# Find .NET SDK
if command -v dotnet &> /dev/null; then
    DOTNET_CMD="dotnet"
elif [ -f "$HOME/.dotnet/dotnet" ]; then
    DOTNET_CMD="$HOME/.dotnet/dotnet"
    export PATH="$HOME/.dotnet:$PATH"
else
    echo "ERROR: .NET SDK not found"
    echo "Please install .NET 8.0 SDK from: https://dotnet.microsoft.com/download"
    exit 1
fi

echo "✓ .NET SDK found: $($DOTNET_CMD --version)"
echo ""

# Parse command-line arguments
TEST_MODE="all"
VERBOSE=false
OUTPUT_DIR="./RedactionTestOutput"

while [[ $# -gt 0 ]]; do
    case $1 in
        --demo)
            TEST_MODE="demo"
            shift
            ;;
        --tests)
            TEST_MODE="tests"
            shift
            ;;
        --integration)
            TEST_MODE="integration"
            shift
            ;;
        --verbose|-v)
            VERBOSE=true
            shift
            ;;
        --output|-o)
            OUTPUT_DIR="$2"
            shift 2
            ;;
        --help|-h)
            echo "Usage: $0 [OPTIONS]"
            echo ""
            echo "Options:"
            echo "  --demo          Run demonstration program (creates sample PDFs and redacts them)"
            echo "  --tests         Run unit tests only"
            echo "  --integration   Run integration tests only"
            echo "  --verbose, -v   Show detailed output"
            echo "  --output DIR    Specify output directory for demo files (default: ./RedactionTestOutput)"
            echo "  --help, -h      Show this help message"
            echo ""
            echo "Default: Runs both demo and tests"
            exit 0
            ;;
        *)
            echo "Unknown option: $1"
            echo "Use --help for usage information"
            exit 1
            ;;
    esac
done

# Function to run demo program
run_demo() {
    echo "========================================="
    echo "Running Redaction Demo Program"
    echo "========================================="
    echo ""

    # Build demo project
    echo "Building demo program..."
    cd Excise.App.Demo

    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD build -c Release
    else
        $DOTNET_CMD build -c Release > /dev/null 2>&1
    fi

    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to build demo program"
        exit 1
    fi

    echo "✓ Demo program built successfully"
    echo ""

    # Run demo
    echo "Running redaction demonstrations..."
    echo "Output will be saved to: $OUTPUT_DIR"
    echo ""

    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD run -c Release --no-build
    else
        $DOTNET_CMD run -c Release --no-build | grep -E "^(===|---|Test [0-9]|✓|✗|Content)"
    fi

    cd ..

    echo ""
    echo "✓ Demo completed!"
    echo "Check the PDFs in: $OUTPUT_DIR"
    echo ""
}

# Function to run tests
run_tests() {
    echo "========================================="
    echo "Running Redaction Tests"
    echo "========================================="
    echo ""

    # Build test project
    echo "Building test project..."
    cd Excise.App.Tests

    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD build -c Release
    else
        $DOTNET_CMD build -c Release > /dev/null 2>&1
    fi

    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to build test project"
        exit 1
    fi

    echo "✓ Test project built successfully"
    echo ""

    # Run tests
    echo "Running tests..."
    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD test -c Release --no-build --logger "console;verbosity=detailed"
    else
        $DOTNET_CMD test -c Release --no-build --logger "console;verbosity=normal"
    fi

    TEST_RESULT=$?
    cd ..

    echo ""
    if [ $TEST_RESULT -eq 0 ]; then
        echo "✓ All tests passed!"
    else
        echo "✗ Some tests failed (exit code: $TEST_RESULT)"
        exit $TEST_RESULT
    fi
    echo ""
}

# Function to run integration tests only
run_integration_tests() {
    echo "========================================="
    echo "Running Integration Tests Only"
    echo "========================================="
    echo ""

    # Build test project
    echo "Building test project..."
    cd Excise.App.Tests

    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD build -c Release
    else
        $DOTNET_CMD build -c Release > /dev/null 2>&1
    fi

    if [ $? -ne 0 ]; then
        echo "ERROR: Failed to build test project"
        exit 1
    fi

    echo "✓ Test project built successfully"
    echo ""

    # Run integration tests only
    echo "Running integration tests..."
    if [ "$VERBOSE" = true ]; then
        $DOTNET_CMD test -c Release --no-build \
            --filter "FullyQualifiedName~Integration" \
            --logger "console;verbosity=detailed"
    else
        $DOTNET_CMD test -c Release --no-build \
            --filter "FullyQualifiedName~Integration" \
            --logger "console;verbosity=normal"
    fi

    TEST_RESULT=$?
    cd ..

    echo ""
    if [ $TEST_RESULT -eq 0 ]; then
        echo "✓ All integration tests passed!"
    else
        echo "✗ Some integration tests failed (exit code: $TEST_RESULT)"
        exit $TEST_RESULT
    fi
    echo ""
}

# Main execution
case $TEST_MODE in
    demo)
        run_demo
        ;;
    tests)
        run_tests
        ;;
    integration)
        run_integration_tests
        ;;
    all)
        run_demo
        run_tests
        ;;
esac

echo "========================================="
echo "✓ All operations completed successfully!"
echo "========================================="
echo ""
echo "Summary:"
case $TEST_MODE in
    demo)
        echo "  - Demo PDFs created in: $OUTPUT_DIR"
        echo "  - Open the PDFs to visually verify redactions"
        ;;
    tests)
        echo "  - All tests passed"
        echo "  - Redaction library is functioning correctly"
        ;;
    integration)
        echo "  - All integration tests passed"
        echo "  - Content-level redaction verified"
        ;;
    all)
        echo "  - Demo PDFs created in: $OUTPUT_DIR"
        echo "  - All tests passed"
        echo "  - Redaction library is fully functional"
        ;;
esac
echo ""
