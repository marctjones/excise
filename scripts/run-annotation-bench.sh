#!/usr/bin/env bash
# Annotation bench (#1053): score excise's annotation rendering against every
# independent renderer that draws annotations, per annotation, and report by
# subtype — Group A (/AP present) and Group B (/AP absent) scored SEPARATELY.
#
# This is a BENCH, not a gate. It reports; it never fails a build. The gates are
# AppearanceStreamDifferentialTests and CorpusAppearanceStreamTests.
#
#   scripts/run-annotation-bench.sh                       # pdf.js corpus
#   scripts/run-annotation-bench.sh --corpus test-pdfs/pdfium
#   scripts/run-annotation-bench.sh --oracles primaries    # drop pdfium/pdfbox
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-Debug}"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT_DIR="logs/annotation-bench_${STAMP}"
mkdir -p "$OUT_DIR"

ARGS=()
HAVE_CORPUS=0
for a in "$@"; do
  [ "$a" = "--corpus" ] && HAVE_CORPUS=1
  ARGS+=("$a")
done
[ "$HAVE_CORPUS" = "0" ] && ARGS+=(--corpus test-pdfs/pdfjs)

dotnet build tools/Excise.RenderTools -c "$CONFIG" >/dev/null

BIN="tools/Excise.RenderTools/bin/${CONFIG}/net10.0/Excise.RenderTools"
"$BIN" annotation-bench "${ARGS[@]}" --out "$OUT_DIR/rows.tsv" 2>&1 | tee "$OUT_DIR/summary.txt"

echo
echo "report: $OUT_DIR/summary.txt"
echo "rows:   $OUT_DIR/rows.tsv"
