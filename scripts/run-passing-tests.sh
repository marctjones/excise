#!/bin/bash
# Run only passing tests (exclude known broken tests)
#
# Usage:
#   ./run-passing-tests.sh           # Build and run passing tests
#   ./run-passing-tests.sh --no-build # Skip build

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
LOG_DIR="$PROJECT_ROOT/logs"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/passing_tests_$TIMESTAMP.log"

# Parse arguments
SKIP_BUILD=false
if [[ "$1" == "--no-build" ]]; then
    SKIP_BUILD=true
fi

# Create logs directory
mkdir -p "$LOG_DIR"

# Redirect all output to both console and log file
exec > >(tee "$LOG_FILE")
exec 2>&1

echo "================================================="
echo "excise Passing Tests Only"
echo "================================================="
echo ""
echo "Excluding known broken tests:"
echo "  - Character-level redaction (not implemented - #98)"
echo "  - Metadata sanitization (#99)"
echo "  - Render integration (#100)"
echo "  - Scripted GUI invalid syntax (#101)"
echo "  - Automation scripts with known excise bugs (#95, #97)"
echo ""
echo "Log file: $LOG_FILE"
echo ""

cd "$PROJECT_ROOT"

# Build if needed
if [ "$SKIP_BUILD" = false ]; then
    echo "Building tests..."
    dotnet build Excise.App.Tests/Excise.App.Tests.csproj --nologo -v quiet
    echo "✅ Build successful"
    echo ""
fi

# Run tests excluding broken ones
echo "Running passing tests..."
echo ""

dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
    --filter "FullyQualifiedName!~CharacterLevel & FullyQualifiedName!~CharacterMatcher & FullyQualifiedName!~MetadataRedaction & FullyQualifiedName!~RenderIntegration & FullyQualifiedName!~Script_InvalidSyntax & FullyQualifiedName!~AutomationScript_VeraPdfCorpusSample & FullyQualifiedName!~AutomationScript_BirthCertificateSpecificWords & FullyQualifiedName!~AutomationScript_RedactText" \
    --logger "console;verbosity=normal" \
    --no-build

EXIT_CODE=$?

echo ""
echo "================================================="
echo "Test Results"
echo "================================================="
echo ""

if [ $EXIT_CODE -eq 0 ]; then
    echo "✅ ALL PASSING TESTS SUCCEEDED"
else
    echo "❌ UNEXPECTED FAILURES"
    echo "Some tests that should pass are failing!"
fi

echo ""
echo "Full log: $LOG_FILE"

exit $EXIT_CODE
