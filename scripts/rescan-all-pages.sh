#!/usr/bin/env bash
#
# Scan EVERY PAGE of every document in all four corpora, not just page 1.
#
# WHY THIS IS A SEPARATE SCRIPT FROM rescan-all-corpora.sh
# --------------------------------------------------------
# Scale, and one fixture in particular. Measured 2026-08-01:
#
#   corpus            pdfs   pages   multi-page docs
#   pdfium             331     393    37
#   pdfjs              685    1254    49
#   verapdf-corpus    2694    2719    21
#   isartor            205   10223     2
#   TOTAL             3915   14589
#
# Isartor's 10,223 pages are almost entirely ONE file:
#
#   Isartor testsuite/PDFA-1b/6.1 File structure/6.1.12 Implementation Limits/
#     isartor-6-1-12-t01-fail-a.pdf   ->  10,000 pages
#
# That is a PDF/A-1b implementation-limits VIOLATION fixture: its whole purpose
# is to exceed a structural limit, so the 10,000 pages are near-identical by
# construction. It is 69% of every page in every corpus and contributes about
# as much rendering information as its first few pages do.
#
# It is still scanned — "all pages" means all pages — but corpora run
# cheapest-first so the informative results land long before it does, and the
# cost is recorded here so nobody has to rediscover why an all-pages run takes
# hours.
#
# --page-mode all also turns on two things the page-1 mode leaves off: the
# excise render cache and page sharding. Both exist for exactly this run.
#
# Usage:
#   ./scripts/rescan-all-pages.sh              # all four, cheapest first
#   ./scripts/rescan-all-pages.sh pdfium       # just one
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
mkdir -p logs

# key:corpus-dir — ordered by measured page count, cheapest first.
CORPORA=(
    "pdfium:test-pdfs/pdfium"
    "pdfjs:test-pdfs/pdfjs"
    "verapdf:test-pdfs/verapdf-corpus"
    "isartor:test-pdfs/isartor"
)

WANT="${1:-}"
rc=0

for spec in "${CORPORA[@]}"; do
    key="${spec%%:*}"; dir="${spec#*:}"
    [ -n "$WANT" ] && [ "$WANT" != "$key" ] && continue

    if [ ! -d "$ROOT/$dir" ]; then
        echo "  skip $key — $dir not downloaded"
        continue
    fi

    log="logs/allpages-$key.log"
    echo "▶ $key ($dir) — every page → $log"
    if scripts/run-exploratory-corpus.sh \
            --corpus "$dir" --page-mode all --extra-oracles all \
            > "$log" 2>&1; then
        echo "  ✓ $key"
    else
        echo "  ✗ $key (exit $?) — see $log"
        rc=1
    fi
done

echo ""
echo "Reports: Excise.Rendering.Tests/bin/Debug/net10.0/exploratory-report*-all.json"
exit $rc
