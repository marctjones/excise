#!/usr/bin/env bash
# Run the PDF 2.0 renderer-first conformance gate.
#
# This is the reproducible entry point for the renderer conformance dashboard:
# it regenerates local PDF 2.0 image fixtures, builds their feature inventory,
# merges that inventory with the broader image/filter inventory, validates the
# curated PDF 2.0 matrix, and runs the renderer coverage audit.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

BASE_IMAGE_INVENTORY="logs/image-conformance/inventory.json"
PDF20_FIXTURE_DIR="test-pdfs/pdf20"
PDF20_IMAGE_DIR="logs/image-conformance/pdf20-fixtures"
IMAGE_COVERAGE="logs/image-conformance/normative/coverage-audit.json"
PDF20_COVERAGE="logs/pdf20/pdf20-renderer-coverage.json"
RUN_TESTS=0
REBUILD_BASE_IMAGE_INVENTORY=0
INCLUDE_BASE_IMAGE_INVENTORY=0

usage() {
    cat <<'EOF'
Run the PDF 2.0 renderer-first conformance gate.

Usage:
  scripts/run-pdf20-renderer-conformance.sh [options]

Options:
  --include-base-image-inventory Merge the broad image/filter inventory with the
                                 generated PDF 2.0 fixture inventory.
  --base-image-inventory <path>  Broad image/filter inventory to merge when
                                 --include-base-image-inventory is set.
                                 Default: logs/image-conformance/inventory.json
  --rebuild-base-image-inventory Rebuild the broad test-pdfs image inventory first.
                                 This can be slow on a large local corpus.
  --run-tests                    Also run the focused xUnit matrix guard tests.
  -h, --help                     Show this help.

Outputs:
  logs/image-conformance/pdf20-fixtures/inventory.json
  logs/image-conformance/normative/coverage-audit.json
  logs/pdf20/pdf20-renderer-coverage.json
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --include-base-image-inventory) INCLUDE_BASE_IMAGE_INVENTORY=1; shift ;;
        --base-image-inventory) BASE_IMAGE_INVENTORY="$2"; shift 2 ;;
        --base-image-inventory=*) BASE_IMAGE_INVENTORY="${1#*=}"; shift ;;
        --rebuild-base-image-inventory) REBUILD_BASE_IMAGE_INVENTORY=1; shift ;;
        --run-tests) RUN_TESTS=1; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

mkdir -p "$PDF20_IMAGE_DIR" "$(dirname "$IMAGE_COVERAGE")" "$(dirname "$PDF20_COVERAGE")"

echo "generating PDF 2.0 image/filter fixtures"
"$SCRIPT_DIR/generate-pdf20-image-fixtures.py" --output-dir "$PDF20_FIXTURE_DIR"

if [[ "$INCLUDE_BASE_IMAGE_INVENTORY" == "1" && ( "$REBUILD_BASE_IMAGE_INVENTORY" == "1" || ! -f "$BASE_IMAGE_INVENTORY" ) ]]; then
    echo "building broad image/filter inventory: $BASE_IMAGE_INVENTORY"
    "$SCRIPT_DIR/build-image-feature-inventory.py" \
        --corpus test-pdfs \
        --matrix test-pdfs/manifests/pdf-image-feature-matrix.json \
        --output "$BASE_IMAGE_INVENTORY" \
        --page-manifest "$(dirname "$BASE_IMAGE_INVENTORY")/page-manifest.tsv"
elif [[ "$INCLUDE_BASE_IMAGE_INVENTORY" == "1" ]]; then
    echo "using existing broad image/filter inventory: $BASE_IMAGE_INVENTORY"
else
    echo "using generated PDF 2.0 fixture inventory for image/filter coverage"
fi

echo "building supplemental PDF 2.0 fixture inventory"
"$SCRIPT_DIR/build-image-feature-inventory.py" \
    --corpus "$PDF20_FIXTURE_DIR" \
    --matrix test-pdfs/manifests/pdf-image-feature-matrix.json \
    --output "$PDF20_IMAGE_DIR/inventory.json" \
    --page-manifest "$PDF20_IMAGE_DIR/page-manifest.tsv"

echo "auditing image/filter coverage"
IMAGE_AUDIT_ARGS=(
    --matrix test-pdfs/manifests/pdf-image-feature-matrix.json
    --inventory "$PDF20_IMAGE_DIR/inventory.json"
    --output "$IMAGE_COVERAGE"
    --fail-on-missing
)
if [[ "$INCLUDE_BASE_IMAGE_INVENTORY" == "1" ]]; then
    IMAGE_AUDIT_ARGS+=(--inventory "$BASE_IMAGE_INVENTORY")
fi
"$SCRIPT_DIR/audit-image-feature-coverage.py" "${IMAGE_AUDIT_ARGS[@]}"

echo "validating PDF 2.0 renderer matrix"
"$SCRIPT_DIR/curate-pdf20-requirements.py" \
    --matrix test-pdfs/manifests/pdf20-renderer-requirements.json

echo "auditing PDF 2.0 renderer coverage"
"$SCRIPT_DIR/audit-pdf20-renderer-coverage.py" \
    --output "$PDF20_COVERAGE" \
    --image-coverage-report "$IMAGE_COVERAGE" \
    --fail-on-hard-gate

if [[ "$RUN_TESTS" == "1" ]]; then
    echo "running focused PDF 2.0 matrix tests"
    dotnet test Pdfe.Core.Tests/Pdfe.Core.Tests.csproj \
        --filter "FullyQualifiedName~Pdf20RendererRequirementMatrixTests" \
        --logger "console;verbosity=minimal"
fi

echo
echo "PDF 2.0 renderer conformance gate passed"
echo "  image/filter coverage: $IMAGE_COVERAGE"
echo "  renderer coverage:     $PDF20_COVERAGE"
