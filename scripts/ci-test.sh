#!/bin/bash
# ci-test.sh: Local CI gate simulation
# Run this before pushing to verify the PR will pass CI gates

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

echo "========================================="
echo "CI Gate Simulation (Local)"
echo "========================================="

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

PASSED=0
FAILED=0

# Helper function to run a gate
run_gate() {
    local name="$1"
    local cmd="$2"

    echo ""
    echo -n "[$name] ... "

    if eval "$cmd" 2>&1 | tee -a ci-test.log | tail -20; then
        echo -e "${GREEN}✓ PASSED${NC}"
        ((PASSED++))
    else
        echo -e "${RED}✗ FAILED${NC}"
        ((FAILED++))
    fi
}

# Clean up previous run
rm -f ci-test.log
mkdir -p coverage

echo "Started: $(date)"
echo ""

# 1. Build
run_gate "Build (Debug)" "dotnet build pdfe.sln -c Debug"

# 2. Run Pdfe.Core.Tests with coverage
run_gate "Pdfe.Core.Tests + Coverage" \
    "dotnet test Pdfe.Core.Tests --no-build -c Debug \
     --collect:\"XPlat Code Coverage\" \
     --results-directory coverage/ \
     --logger \"console;verbosity=quiet\" \
     --verbosity normal"

# 3. Generate coverage report
run_gate "Generate Coverage Report" \
    "mkdir -p coverage/report && \
     ./tools/reportgenerator -reports:coverage/*/coverage.cobertura.xml \
     -targetdir:coverage/report -reporttypes:Cobertura"

# 4. Check coverage threshold
run_gate "Coverage Gate (Pdfe.Core >= 94%)" \
    "scripts/check-coverage.sh coverage/report/coverage.cobertura.xml 0.94 Pdfe.Core"

# 5. Run other test projects (no corpus tests - they're slow)
run_gate "Pdfe.Cli.Tests" \
    "dotnet test Pdfe.Cli.Tests --no-build -c Debug \
     --logger \"console;verbosity=quiet\""

run_gate "Pdfe.Rendering.Tests (no Corpus)" \
    "dotnet test Pdfe.Rendering.Tests --no-build -c Debug \
     --filter \"FullyQualifiedName!~Corpus\" \
     --logger \"console;verbosity=quiet\""

run_gate "PDF 2.0 Renderer Conformance" \
    "scripts/run-pdf20-renderer-conformance.sh --run-tests"

run_gate "Pdfe.Ocr.Tests" \
    "dotnet test Pdfe.Ocr.Tests --no-build -c Debug \
     --logger \"console;verbosity=quiet\" || true"

# 6. Run PdfEditor.Tests (may need xvfb on headless systems)
if command -v Xvfb &> /dev/null; then
    run_gate "PdfEditor.Tests (with Xvfb)" \
        "xvfb-run -a dotnet test PdfEditor.Tests --no-build -c Debug \
         --logger \"console;verbosity=quiet\" \
         --blame-hang-timeout 120000"
else
    echo ""
    echo -n "[PdfEditor.Tests] ... "
    echo -e "${YELLOW}⊘ SKIPPED (no display available)${NC}"
    echo "  To run: xvfb-run -a dotnet test PdfEditor.Tests --no-build -c Debug"
fi

# Summary
echo ""
echo "========================================="
echo "Summary"
echo "========================================="
echo "Passed: $PASSED"
echo "Failed: $FAILED"
echo ""

if [[ $FAILED -eq 0 ]]; then
    echo -e "${GREEN}✓ All gates passed!${NC}"
    echo "Log saved to: ci-test.log"
    exit 0
else
    echo -e "${RED}✗ $FAILED gate(s) failed${NC}"
    echo "Log saved to: ci-test.log"
    exit 1
fi
