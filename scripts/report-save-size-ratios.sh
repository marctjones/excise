#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUTPUT="$ROOT/logs/save-size-ratios/latest/report.json"
MAX_RATIO="1.20"
FILES=()

while [[ $# -gt 0 ]]; do
  case "$1" in
    --output)
      OUTPUT="$2"
      shift 2
      ;;
    --max-ratio)
      MAX_RATIO="$2"
      shift 2
      ;;
    --help|-h)
      cat <<USAGE
Usage: scripts/report-save-size-ratios.sh [--output report.json] [--max-ratio 1.20] [pdf...]

Runs excise save-size-report and writes a JSON report for writer size/latency
tracking. If no PDFs are supplied, uses checked-in smoke fixtures.
USAGE
      exit 0
      ;;
    *)
      FILES+=("$1")
      shift
      ;;
  esac
done

if [[ ${#FILES[@]} -eq 0 ]]; then
  FILES=(
    "$ROOT/test-pdfs/smoke/irs-w4.pdf"
    "$ROOT/test-pdfs/smoke/irs-w9.pdf"
    "$ROOT/test-pdfs/smoke/scotus-trump-v-anderson.pdf"
  )
fi

mkdir -p "$(dirname "$OUTPUT")"
dotnet run --project "$ROOT/Excise.Cli" -- \
  save-size-report "${FILES[@]}" \
  --max-ratio "$MAX_RATIO" \
  --output "$OUTPUT"
