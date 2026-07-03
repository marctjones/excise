#!/usr/bin/env bash
# Run the contract-driven rendering quality scanner.
#
# This is the preferred release-quality entry point for rendering status
# reporting. It uses per-PDF JSON contracts under test-pdfs/rendering-contracts
# instead of TSV expectation/password manifests.
#
# Usage:
#   scripts/run-render-quality-scan.sh
#   scripts/run-render-quality-scan.sh --page-mode first --oracles ghostscript
#   scripts/run-render-quality-scan.sh --contract-root-cause ALTONA_P7 --page-mode all --oracles all
#   scripts/run-render-quality-scan.sh --corpus test-pdfs --output logs/render-quality/all.json

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

CONFIG="Debug"
CORPUS="test-pdfs"
CONTRACTS="test-pdfs/rendering-contracts"
OUTPUT="logs/render-quality/render-quality-report.json"
RAW_OUTPUT=""
PAGE_MODE="all"
ORACLES="all"
PARALLEL="0"
PDF_TIMEOUT_MS="120000"
STRICT_CONTRACTS="0"
CONTRACT_PATH_CONTAINS=""
CONTRACT_ROOT_CAUSE=""
CONTRACT_OWNER=""
CONTRACT_ISSUE=""
EXTRA_ARGS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --release) CONFIG="Release"; shift ;;
        --corpus) CORPUS="$2"; shift 2 ;;
        --corpus=*) CORPUS="${1#*=}"; shift ;;
        --contracts) CONTRACTS="$2"; shift 2 ;;
        --contracts=*) CONTRACTS="${1#*=}"; shift ;;
        --contract-path-contains) CONTRACT_PATH_CONTAINS="$2"; shift 2 ;;
        --contract-path-contains=*) CONTRACT_PATH_CONTAINS="${1#*=}"; shift ;;
        --contract-root-cause) CONTRACT_ROOT_CAUSE="$2"; shift 2 ;;
        --contract-root-cause=*) CONTRACT_ROOT_CAUSE="${1#*=}"; shift ;;
        --contract-owner) CONTRACT_OWNER="$2"; shift 2 ;;
        --contract-owner=*) CONTRACT_OWNER="${1#*=}"; shift ;;
        --contract-issue) CONTRACT_ISSUE="$2"; shift 2 ;;
        --contract-issue=*) CONTRACT_ISSUE="${1#*=}"; shift ;;
        --output) OUTPUT="$2"; shift 2 ;;
        --output=*) OUTPUT="${1#*=}"; shift ;;
        --raw-output) RAW_OUTPUT="$2"; shift 2 ;;
        --raw-output=*) RAW_OUTPUT="${1#*=}"; shift ;;
        --page-mode) PAGE_MODE="$2"; shift 2 ;;
        --page-mode=*) PAGE_MODE="${1#*=}"; shift ;;
        --oracles) ORACLES="$2"; shift 2 ;;
        --oracles=*) ORACLES="${1#*=}"; shift ;;
        --parallel) PARALLEL="$2"; shift 2 ;;
        --parallel=*) PARALLEL="${1#*=}"; shift ;;
        --pdf-timeout-ms) PDF_TIMEOUT_MS="$2"; shift 2 ;;
        --pdf-timeout-ms=*) PDF_TIMEOUT_MS="${1#*=}"; shift ;;
        --strict-contracts) STRICT_CONTRACTS="1"; shift ;;
        --help|-h)
            sed -n '2,16p' "$0"; exit 0 ;;
        *)
            EXTRA_ARGS+=("$1"); shift ;;
    esac
done

mkdir -p "$(dirname "$OUTPUT")"
CMD=(dotnet run --project tools/Pdfe.RenderTools/Pdfe.RenderTools.csproj -c "$CONFIG" -- \
    render-quality-scan "$CORPUS" \
    --contracts "$CONTRACTS" \
    --output "$OUTPUT")
if [[ -n "$CONTRACT_PATH_CONTAINS" ]]; then
    CMD+=(--contract-path-contains "$CONTRACT_PATH_CONTAINS")
fi
if [[ -n "$CONTRACT_ROOT_CAUSE" ]]; then
    CMD+=(--contract-root-cause "$CONTRACT_ROOT_CAUSE")
fi
if [[ -n "$CONTRACT_OWNER" ]]; then
    CMD+=(--contract-owner "$CONTRACT_OWNER")
fi
if [[ -n "$CONTRACT_ISSUE" ]]; then
    CMD+=(--contract-issue "$CONTRACT_ISSUE")
fi
if [[ -n "$RAW_OUTPUT" ]]; then
    mkdir -p "$(dirname "$RAW_OUTPUT")"
    CMD+=(--raw-output "$RAW_OUTPUT")
fi

CMD+=( \
    --page-mode "$PAGE_MODE" \
    --oracles "$ORACLES" \
    --parallel "$PARALLEL" \
    --pdf-timeout-ms "$PDF_TIMEOUT_MS")
if [[ "$STRICT_CONTRACTS" == "1" ]]; then
    CMD+=(--strict-contracts)
fi
if (( ${#EXTRA_ARGS[@]} > 0 )); then
    CMD+=("${EXTRA_ARGS[@]}")
fi

"${CMD[@]}"
