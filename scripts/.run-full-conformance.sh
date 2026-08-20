#!/usr/bin/env bash
# Ad-hoc full conformance sweep, 1.7 + 2.0 (untracked; see the issue it produces).
set -uo pipefail
cd "$(dirname "$0")/.."
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="logs/full-conformance_$STAMP"
mkdir -p "$OUT"
say() { echo "=== $* ===" | tee -a "$OUT/summary.txt"; }
rc_all=0

say "1/4 PDF 2.0 renderer conformance gate (ISO 32000-2:2020 + EC3 matrix)"
scripts/run-pdf20-renderer-conformance.sh --run-tests > "$OUT/pdf20.log" 2>&1
echo "  rc=$? $(grep -cE 'hard gate failures: 0' "$OUT/pdf20.log")" | tee -a "$OUT/summary.txt"
tail -12 "$OUT/pdf20.log" >> "$OUT/summary.txt"; [ $? -ne 0 ] && rc_all=1

say "2/4 conformance harness incl. LARGE corpus (veraPDF 2694 + Isartor 205)"
SKIP_LARGE_CORPUS=0 scripts/run-conformance.sh > "$OUT/conformance.log" 2>&1
echo "  rc=$?" | tee -a "$OUT/summary.txt"
tail -25 "$OUT/conformance.log" >> "$OUT/summary.txt"

say "3/4 image/filter conformance scan"
scripts/run-image-conformance-suite.sh > "$OUT/image.log" 2>&1
echo "  rc=$?" | tee -a "$OUT/summary.txt"
tail -12 "$OUT/image.log" >> "$OUT/summary.txt"

say "4/4 corpus rendering scans vs oracle expectations"
for c in verapdf-corpus isartor pdfjs pdfium; do
  [ -d "test-pdfs/$c" ] || continue
  man="tests/corpus-expectations-${c%-corpus}.tsv"
  args=(--corpus "test-pdfs/$c" --page-mode first --extra-oracles all)
  [ -f "$man" ] && args+=(--expectation-manifest "$man")
  scripts/run-exploratory-corpus.sh "${args[@]}" > "$OUT/scan-$c.log" 2>&1
  echo "  $c rc=$? $(grep -oE 'PASS[_A-Z]*: *[0-9]+' "$OUT/scan-$c.log" | tail -3 | tr '\n' ' ')" | tee -a "$OUT/summary.txt"
done

echo "DONE $(date)" | tee -a "$OUT/summary.txt"
echo "report: $OUT/summary.txt"
