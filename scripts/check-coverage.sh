#!/bin/bash
# check-coverage.sh: Verify that a Cobertura coverage report meets a minimum line coverage threshold
# Usage: scripts/check-coverage.sh <cobertura.xml> <minLineRate> [packageFilter]
#   minLineRate: decimal between 0.0 and 1.0 (e.g., 0.94 for 94%)
#   packageFilter: optional package name to filter to (e.g., "Excise.Core")

set -euo pipefail

if [[ $# -lt 2 ]]; then
    echo "Usage: $0 <cobertura.xml> <minLineRate> [packageFilter]"
    echo "  Example: $0 coverage/report/coverage.cobertura.xml 0.94"
    echo "  Example: $0 coverage/report/coverage.cobertura.xml 0.94 Excise.Core"
    exit 1
fi

COBERTURA_FILE="$1"
MIN_LINE_RATE="$2"
PACKAGE_FILTER="${3:-}"

if [[ ! -f "$COBERTURA_FILE" ]]; then
    echo "Error: Coverage file not found: $COBERTURA_FILE"
    exit 1
fi

# Extract line-rate from root coverage element
# If packageFilter is provided, filter to that package first
if [[ -n "$PACKAGE_FILTER" ]]; then
    # Extract coverage for specific package
    LINE_RATE=$(grep -o "package name=\"$PACKAGE_FILTER\"[^>]*line-rate=\"[^\"]*\"" "$COBERTURA_FILE" | grep -o 'line-rate="[^"]*"' | head -1 | cut -d'"' -f2)
    if [[ -z "$LINE_RATE" ]]; then
        echo "Warning: Could not find coverage data for package: $PACKAGE_FILTER"
        echo "Available packages:"
        grep -o 'package name="[^"]*"' "$COBERTURA_FILE" | head -10
        exit 1
    fi
    SCOPE="package $PACKAGE_FILTER"
else
    # Use root coverage element (entire solution)
    LINE_RATE=$(grep -o '^<coverage[^>]*line-rate="[^"]*"' "$COBERTURA_FILE" | grep -o 'line-rate="[^"]*"' | cut -d'"' -f2)
    if [[ -z "$LINE_RATE" ]]; then
        echo "Error: Could not extract line-rate from coverage file"
        exit 1
    fi
    SCOPE="entire solution"
fi

# Convert to percentage for display
LINE_RATE_PCT=$(awk "BEGIN {printf \"%.2f\", $LINE_RATE * 100}")
MIN_RATE_PCT=$(awk "BEGIN {printf \"%.2f\", $MIN_LINE_RATE * 100}")

echo "Coverage Report for $SCOPE"
echo "  Measured: $LINE_RATE_PCT%"
echo "  Required: $MIN_RATE_PCT%"

# Compare as floats
if (( $(echo "$LINE_RATE >= $MIN_LINE_RATE" | bc -l) )); then
    echo "✓ Coverage check PASSED"
    exit 0
else
    echo "✗ Coverage check FAILED"
    exit 1
fi
