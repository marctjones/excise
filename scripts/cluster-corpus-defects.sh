#!/usr/bin/env bash
#
# Cluster the corpus scan's DEFECT pages by root-cause signature.
#
# Three classes count as defects (see the agreement vocabulary in
# run-exploratory-corpus.sh):
#
#   EXCISE_SIDE_GAP   an oracle rendered the page and excise produced nothing
#   MISSING_CONTENT   excise rendered, but drew no ink in ANY tile the
#                     most-inked oracle drew in
#   DIFF              both rendered and no oracle agreed
#
# Everything else is either agreement, a corroborated refusal, or a question
# about the oracles rather than about excise.
#
# The point of clustering is that 37 defect pages are not 37 bugs. They are a
# handful of causes with a long tail, and filing them one per page would bury
# the two or three that matter. Signatures are the error message with digits
# normalised, so "Expected 'endobj', got '8' at position 623" and the same at
# position 4471 land together.
#
# Usage:
#   ./scripts/cluster-corpus-defects.sh            # all four all-pages reports
#   ./scripts/cluster-corpus-defects.sh --first    # the page-1 reports instead
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
BIN="$ROOT/Excise.Rendering.Tests/bin/Debug/net10.0"

SUFFIX="all"
[ "${1:-}" = "--first" ] && SUFFIX="first"

python3 - "$BIN" "$SUFFIX" <<'PY'
import json, os, sys, re, collections

bin_dir, suffix = sys.argv[1], sys.argv[2]
if suffix == "all":
    files = [('pdf.js', 'exploratory-report-all.json'),
             ('veraPDF', 'exploratory-report-test-pdfs-verapdf-corpus-all.json'),
             ('Isartor', 'exploratory-report-test-pdfs-isartor-all.json'),
             ('PDFium', 'exploratory-report-test-pdfs-pdfium-all.json')]
else:
    files = [('pdf.js', 'exploratory-report.json'),
             ('veraPDF', 'exploratory-report-test-pdfs-verapdf-corpus-first.json'),
             ('Isartor', 'exploratory-report-test-pdfs-isartor-first.json'),
             ('PDFium', 'exploratory-report-test-pdfs-pdfium-first.json')]

ORACLES = [('mutool', 'mutoolStatus'), ('pdftocairo', 'cairoStatus'),
           ('ghostscript', 'ghostscriptStatus'), ('pdfbox', 'pdfboxStatus'),
           ('pdfium', 'pdfiumStatus')]
CRED = {"PASSWORD_REQUIRED", "UNSUPPORTED_ENCRYPTED"}

def classify(x):
    s = x.get('status')
    if s == 'PASS':            return 'PASS'
    if s == 'PASS_ONE':        return 'ORACLE_SPLIT'
    if s == 'DIFF':            return 'DIFF'
    if s == 'MISSING_CONTENT': return 'MISSING_CONTENT'
    states = [x.get(k) for _, k in ORACLES]
    ok = any(v == 'OK' for v in states)
    attempted = any(v is not None for v in states)
    if x.get('renderMs') is not None:
        return 'EXCISE_ONLY' if not ok else 'RENDERED'
    if s in CRED:              return 'CREDENTIAL_BLOCKED'
    if ok:                     return 'EXCISE_SIDE_GAP'
    return 'AGREED_REFUSAL' if attempted else 'UNCORROBORATED'

rows, total = [], 0
for label, f in files:
    p = os.path.join(bin_dir, f)
    if not os.path.exists(p):
        print(f"  (missing report: {f})")
        continue
    for x in json.load(open(p)).get('entries') or []:
        total += 1
        c = classify(x)
        if c in ('EXCISE_SIDE_GAP', 'MISSING_CONTENT', 'DIFF'):
            rows.append((label, c, x))

print(f"  {total} pages scanned, {len(rows)} defect pages\n")

for cls in ('EXCISE_SIDE_GAP', 'MISSING_CONTENT', 'DIFF'):
    sub = [(l, x) for l, c, x in rows if c == cls]
    print("=" * 78)
    print(f"  {cls}: {len(sub)} page(s)")
    print("=" * 78)
    if not sub:
        print("    (none)\n")
        continue

    groups = collections.defaultdict(list)
    for label, x in sub:
        if cls == 'EXCISE_SIDE_GAP':
            key = re.sub(r'\d+', 'N', (x.get('errorMessage') or '').strip())[:66] or x.get('status')
        elif cls == 'MISSING_CONTENT':
            # No exception here — excise rendered. Group by what the page uses,
            # which is the actionable axis.
            key = f"blank page ({x.get('referenceInkedTiles')} reference tiles)"
            key = "excise rendered no ink at all"
        else:
            key = f"rank {x.get('exciseReferenceCenterRank')} of {(x.get('comparedOracles') or 0)+1}, oracles split {x.get('oracleDisagreeingPairs')}/{x.get('oracleComparisonPairs')}"
        groups[key].append((label, x))

    for key, items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        corpora = collections.Counter(l for l, _ in items)
        print(f"\n    [{len(items):3}]  {key}")
        print(f"           corpora: {dict(corpora)}")
        for label, x in items[:6]:
            oks = ",".join(n for n, k in ORACLES if x.get(k) == 'OK')
            extra = ""
            if cls == 'MISSING_CONTENT':
                extra = f"  missing={x.get('missingInkTiles')}/{x.get('referenceInkedTiles')}"
            elif cls == 'DIFF':
                extra = f"  diff={x.get('diffFraction'):.4f}" if x.get('diffFraction') is not None else ""
            print(f"             {label:8} {(x.get('path') or '')[-46:]:48}#p{x.get('pageNumber')}{extra}")
            if cls == 'EXCISE_SIDE_GAP':
                print(f"                      rendered by: {oks or 'none'}")
        if len(items) > 6:
            print(f"             ... and {len(items) - 6} more")
    print()
PY
