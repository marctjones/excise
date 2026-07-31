#!/usr/bin/env bash
#
# Rescan every rendering corpus under the configuration the standard suite
# uses, so tests/corpus-expectations*.tsv is a baseline of what the GATE will
# actually see rather than of some other configuration.
#
# That distinction has teeth: the baselines were first generated with the
# default ghostscript-only escalation, but run-full-suite.sh runs
# --extra-oracles all. Adding PDFBox and PDFium as tie-breakers changes
# classifications on exactly the contested pages (a DIFF where only one oracle
# agreed can become PASS once a majority forms), so a manifest built under the
# other setting would fail on first use for no real reason.
#
# Usage:
#   ./scripts/rescan-all-corpora.sh              # all present corpora
#   ./scripts/rescan-all-corpora.sh pdfium       # just one
#
# Afterwards: ./scripts/update-corpus-expectations.sh, then review the diff.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
mkdir -p logs

# key:corpus-dir  (order: cheapest first, so a broken setup fails fast)
CORPORA=(
    "isartor:test-pdfs/isartor"
    "pdfium:test-pdfs/pdfium"
    "pdfjs:test-pdfs/pdfjs"
    "verapdf:test-pdfs/verapdf-corpus"
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

    log="logs/rescan-$key.log"
    echo "▶ $key ($dir) → $log"
    # No --expectation-manifest: this run DEFINES the baseline, so gating it
    # against the old one would just fail on the changes we are recording.
    if scripts/run-exploratory-corpus.sh \
            --corpus "$dir" --page-mode first --extra-oracles all \
            > "$log" 2>&1; then
        echo "  ✓ $key"
    else
        echo "  ✗ $key (exit $?) — see $log"
        rc=1
    fi
done

echo ""
echo "Next: scripts/update-corpus-expectations.sh && git diff tests/"
exit $rc
