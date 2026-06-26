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
EXTRA_ARGS=()

while [[ $# -gt 0 ]]; do
    case "$1" in
        --release) CONFIG="Release"; shift ;;
        --corpus) CORPUS="$2"; shift 2 ;;
        --corpus=*) CORPUS="${1#*=}"; shift ;;
        --contracts) CONTRACTS="$2"; shift 2 ;;
        --contracts=*) CONTRACTS="${1#*=}"; shift ;;
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
RAW_ARGS=()
if [[ -n "$RAW_OUTPUT" ]]; then
    mkdir -p "$(dirname "$RAW_OUTPUT")"
    RAW_ARGS=(--raw-output "$RAW_OUTPUT")
fi
STRICT_ARGS=()
if [[ "$STRICT_CONTRACTS" == "1" ]]; then
    STRICT_ARGS=(--strict-contracts)
fi

dotnet run --project Pdfe.Cli/Pdfe.Cli.csproj -c "$CONFIG" -- \
    render-quality-scan "$CORPUS" \
    --contracts "$CONTRACTS" \
    --output "$OUTPUT" \
    "${RAW_ARGS[@]}" \
    --page-mode "$PAGE_MODE" \
    --oracles "$ORACLES" \
    --parallel "$PARALLEL" \
    --pdf-timeout-ms "$PDF_TIMEOUT_MS" \
    "${STRICT_ARGS[@]}" \
    "${EXTRA_ARGS[@]}"
