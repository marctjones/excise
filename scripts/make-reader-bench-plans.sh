#!/usr/bin/env bash
# Builds test-pdfs/reader-bench/plans-x20.pdf for the reader bench (#1543): the
# 6.2 MB single-page CAD drawing (test-pdfs/pdfjs/22060_A1_01_Plans.pdf) repeated
# into 20 pages. WHY: nothing else in the bench stresses the PATH rasteriser
# (thousands of vector paths per page) rather than image decode, and a one-page
# document cannot be paged. qpdf shares the page's content stream between the
# repeats, so the file stays ~6 MB while every page is a full-cost render.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/test-pdfs/pdfjs/22060_A1_01_Plans.pdf"
OUT="$ROOT/test-pdfs/reader-bench/plans-x20.pdf"
[ -f "$SRC" ] || { echo "missing $SRC (scripts/download-pdfjs-corpus.sh)" >&2; exit 1; }
mkdir -p "$(dirname "$OUT")"
RANGE="$(printf '1,%.0s' $(seq 20))"; RANGE="${RANGE%,}"
qpdf --empty --pages "$SRC" "$RANGE" -- "$OUT"
echo "built $OUT: $(qpdf --show-npages "$OUT") pages, $(( $(stat -f %z "$OUT") / 1024 )) KB"
