#!/usr/bin/env bash
# Run a focused image/filter rendering conformance scan. This script ties the
# feature inventory to Excise.RenderTools corpus-scan and rendering-quality
# classification commands.

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CORPUS="test-pdfs"
MATRIX="test-pdfs/manifests/pdf-image-feature-matrix.json"
CONTRACTS="test-pdfs/rendering-contracts"
FEATURE=""
PAGE_MODE="sample"
ORACLES="all"
PARALLEL="0"
PDF_TIMEOUT_MS="120000"
CONFIG="Debug"
DOWNLOAD=0
INCLUDE_LARGE=0

usage() {
    cat <<'EOF'
Run image/filter conformance and rendering quality scans.

Usage:
  scripts/run-image-conformance-suite.sh [options]

Options:
  --download             Download/refresh public corpuses before scanning.
  --include-large        With --download, include large Altona 2.0 PDFs.
  --feature <id>         Restrict to one detected feature or matrix requirement
                         id, e.g. filter:JBIG2Decode or image-filter:JBIG2Decode.
                         Omit to scan every PDF with any image stream.
  --page-mode <mode>     first, sample, or all. Default: sample.
  --oracles <set>        none, ghostscript, pdfbox, pdfium, all. Default: all.
  --parallel <n>         Excise.RenderTools corpus-scan parallelism. Default: 0 auto.
  --pdf-timeout-ms <n>   Per-PDF oracle timeout. Default: 120000.
  --release              Use Release build.
  -h, --help             Show this help.

Outputs:
  logs/image-conformance/<slug>/inventory.json
  logs/image-conformance/<slug>/page-manifest.tsv
  logs/image-conformance/<slug>/raw-corpus-scan.json
  logs/image-conformance/<slug>/quality-report.json
  logs/image-conformance/<slug>/jbig2-classify.json
EOF
}

while [[ $# -gt 0 ]]; do
    case "$1" in
        --download) DOWNLOAD=1; shift ;;
        --include-large) INCLUDE_LARGE=1; shift ;;
        --feature) FEATURE="$2"; shift 2 ;;
        --feature=*) FEATURE="${1#*=}"; shift ;;
        --page-mode) PAGE_MODE="$2"; shift 2 ;;
        --page-mode=*) PAGE_MODE="${1#*=}"; shift ;;
        --oracles) ORACLES="$2"; shift 2 ;;
        --oracles=*) ORACLES="${1#*=}"; shift ;;
        --parallel) PARALLEL="$2"; shift 2 ;;
        --parallel=*) PARALLEL="${1#*=}"; shift ;;
        --pdf-timeout-ms) PDF_TIMEOUT_MS="$2"; shift 2 ;;
        --pdf-timeout-ms=*) PDF_TIMEOUT_MS="${1#*=}"; shift ;;
        --release) CONFIG="Release"; shift ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [[ "$DOWNLOAD" == "1" ]]; then
    DOWNLOAD_ARGS=()
    if [[ "$INCLUDE_LARGE" == "1" ]]; then
        DOWNLOAD_ARGS+=(--include-large)
    fi
    "$SCRIPT_DIR/download-standards-image-corpora.sh" "${DOWNLOAD_ARGS[@]}"
fi

SLUG="$(python3 - "$FEATURE" <<'PY'
import re
import sys
raw = sys.argv[1] or "all-image-features"
print(re.sub(r"[^A-Za-z0-9._-]+", "-", raw).strip("-"))
PY
)"
OUT_DIR="logs/image-conformance/$SLUG"
mkdir -p "$OUT_DIR"
PDF20_FIXTURE_DIR="test-pdfs/pdf20"
PDF20_FIXTURE_OUT_DIR="logs/image-conformance/pdf20-fixtures"
mkdir -p "$PDF20_FIXTURE_OUT_DIR"

INVENTORY="$OUT_DIR/inventory.json"
PAGE_MANIFEST="$OUT_DIR/page-manifest.tsv"
PDF20_FIXTURE_INVENTORY="$PDF20_FIXTURE_OUT_DIR/inventory.json"
PDF20_FIXTURE_PAGE_MANIFEST="$PDF20_FIXTURE_OUT_DIR/page-manifest.tsv"
RAW_REPORT="$OUT_DIR/raw-corpus-scan.json"
QUALITY_REPORT="$OUT_DIR/quality-report.json"
JBIG2_REPORT="$OUT_DIR/jbig2-classify.json"
COVERAGE_REPORT="$OUT_DIR/coverage-audit.json"

INVENTORY_ARGS=(
    --corpus "$CORPUS"
    --matrix "$MATRIX"
    --output "$INVENTORY"
    --page-manifest "$PAGE_MANIFEST"
)
if [[ -n "$FEATURE" ]]; then
    INVENTORY_ARGS+=(--feature "$FEATURE")
fi

"$SCRIPT_DIR/build-image-feature-inventory.py" "${INVENTORY_ARGS[@]}"

if [[ -z "$FEATURE" ]]; then
    "$SCRIPT_DIR/generate-pdf20-image-fixtures.py" --output-dir "$PDF20_FIXTURE_DIR"
    "$SCRIPT_DIR/build-image-feature-inventory.py" \
        --corpus "$PDF20_FIXTURE_DIR" \
        --matrix "$MATRIX" \
        --output "$PDF20_FIXTURE_INVENTORY" \
        --page-manifest "$PDF20_FIXTURE_PAGE_MANIFEST"
fi

AUDIT_ARGS=(
    --matrix "$MATRIX"
    --inventory "$INVENTORY"
    --output "$COVERAGE_REPORT"
)
if [[ -z "$FEATURE" ]]; then
    AUDIT_ARGS+=(--inventory "$PDF20_FIXTURE_INVENTORY")
fi
"$SCRIPT_DIR/audit-image-feature-coverage.py" \
    "${AUDIT_ARGS[@]}"

if ! awk 'NR > 1 { found=1; exit } END { exit found ? 0 : 1 }' "$PAGE_MANIFEST"; then
    echo "No PDFs matched feature '${FEATURE:-image:any}'." >&2
    exit 1
fi

echo "building Excise.RenderTools ($CONFIG)"
dotnet build -c "$CONFIG" tools/Excise.RenderTools/Excise.RenderTools.csproj >/dev/null
RENDER_TOOLS_BIN="$ROOT/tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools"

if [[ -z "$FEATURE" || "$FEATURE" == "filter:JBIG2Decode" ]]; then
    echo "classifying JBIG2 requirements"
    "$RENDER_TOOLS_BIN" jbig2-classify "$CORPUS" --output "$JBIG2_REPORT"
else
    echo "skipping JBIG2 classification for non-JBIG2 feature '$FEATURE'"
fi

echo "running raw differential image/filter scan"
"$RENDER_TOOLS_BIN" corpus-scan "$CORPUS" \
    --output "$RAW_REPORT" \
    --page-manifest "$PAGE_MANIFEST" \
    --page-mode "$PAGE_MODE" \
    --extra-oracles "$ORACLES" \
    --parallel "$PARALLEL" \
    --pdf-timeout-ms "$PDF_TIMEOUT_MS"

echo "applying rendering quality contracts"
"$RENDER_TOOLS_BIN" render-quality-classify "$RAW_REPORT" \
    --contracts "$CONTRACTS" \
    --output "$QUALITY_REPORT"

echo
echo "wrote:"
echo "  inventory:      $INVENTORY"
echo "  page manifest:  $PAGE_MANIFEST"
echo "  coverage audit: $COVERAGE_REPORT"
echo "  raw report:     $RAW_REPORT"
echo "  quality report: $QUALITY_REPORT"
if [[ -f "$JBIG2_REPORT" ]]; then
    echo "  JBIG2 report:   $JBIG2_REPORT"
fi
