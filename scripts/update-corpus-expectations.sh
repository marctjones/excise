#!/usr/bin/env bash
#
# Regenerate the per-corpus expectation manifests from the newest scan reports.
#
# The rendering scan covers four corpora, each with its own manifest so a
# filename that appears in two of them cannot collide:
#
#   pdf.js   (Mozilla)          685 files — another engine's regression history
#   veraPDF  (PDF Association) 2694 files — PDF/A and PDF/UA conformance
#   Isartor  (PDF Association)  205 files — PDF/A-1 violation suite
#   PDFium   (Chrome)           331 files — Chrome's regression history
#
# Keys are CORPUS-RELATIVE PATHS, not basenames: PDFium's corpus has
# subdirectories (45 files under javascript/xfa_specific/ alone) and at least
# one duplicate basename, so keying on the filename would silently merge two
# different fixtures into one expectation.
#
# Usage:
#   ./scripts/update-corpus-expectations.sh            # all corpora
#   ./scripts/update-corpus-expectations.sh pdfjs      # just one
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

BIN="$ROOT/Excise.Rendering.Tests/bin/Debug/net10.0"

# corpus-key : report filename : manifest filename
CORPORA=(
    "pdfjs:exploratory-report.json:corpus-expectations.tsv"
    "pdfium:exploratory-report-test-pdfs-pdfium-first.json:corpus-expectations-pdfium.tsv"
    "isartor:exploratory-report-test-pdfs-isartor-first.json:corpus-expectations-isartor.tsv"
    "verapdf:exploratory-report-test-pdfs-verapdf-corpus-first.json:corpus-expectations-verapdf.tsv"
)

WANT="${1:-}"
updated=0
failed=0

for spec in "${CORPORA[@]}"; do
    key="${spec%%:*}"; rest="${spec#*:}"
    report="${rest%%:*}"; manifest="${rest#*:}"
    [ -n "$WANT" ] && [ "$WANT" != "$key" ] && continue

    src="$BIN/$report"
    if [ ! -f "$src" ]; then
        echo "  skip $key — no report at $src"
        continue
    fi

    # Write to .tmp and promote only after checking BOTH the exit status and
    # that rows landed. Redirecting python straight onto the manifest would let
    # a crash truncate a gate baseline to zero rows while this script still
    # exited 0 — a manifest with no rows gates nothing and reads as green. Same
    # vacuous-pass shape already fixed once in run-full-suite.sh.
    tmp="$ROOT/tests/$manifest.tmp"
    if ! python3 - "$src" "$key" > "$tmp" <<'PY'
import json, sys, collections
src, key = sys.argv[1], sys.argv[2]
d = json.load(open(src))
rows = d if isinstance(d, list) else (d.get("results") or d.get("entries") or [])

print(f"# Expected corpus-scan status per page for the {key} corpus (#862).")
print("#")
print("# Format:  corpus-relative-path <TAB> pageNumber <TAB> expectedStatus")
print("#          (no header row: the loader only skips one on line 1, and these")
print("#          comments push it past that)")
print("# Regenerate: scripts/update-corpus-expectations.sh " + key)
print("#")
print("# A RATCHET, not an aspiration — it records what each page does today so a")
print("# regression fails loudly. Several statuses are CORRECT outcomes:")
print("#   PASS_ONE     only one oracle corroborated (they disagree with each other)")
print("#   EXCISE_ONLY  excise rendered and no oracle could — coverage without")
print("#                corroboration, not a defect")
print("#   MALFORMED_PDF / EMPTY_DOC / TIMEOUT  the fixture is broken or hostile")
print("#                on purpose; refusing it is correct")
print("#")
print("# Keys are corpus-relative PATHS, not basenames: this corpus may have")
print("# subdirectories and duplicate filenames.")
c = collections.Counter(r.get("status") for r in rows)
print("# Baseline: " + ", ".join(f"{k}={v}" for k, v in c.most_common()))

def path_of(r):
    return r.get("path") or r.get("file") or ""

for r in sorted(rows, key=path_of):
    p = path_of(r)
    page = r.get("page") or r.get("pageNumber") or 1
    st = r.get("status")
    if p and st:
        print(f"{p}\t{page}\t{st}")
PY
    then
        echo "  ✗ $key — generator failed, tests/$manifest left unchanged" >&2
        rm -f "$tmp"
        failed=$((failed+1))
        continue
    fi

    n=$(grep -vc '^#' "$tmp")
    if [ "${n:-0}" -eq 0 ]; then
        echo "  ✗ $key — generator produced 0 rows, tests/$manifest left unchanged" >&2
        rm -f "$tmp"
        failed=$((failed+1))
        continue
    fi

    mv "$tmp" "$ROOT/tests/$manifest"
    echo "  ✓ tests/$manifest ($n pages)"
    updated=$((updated+1))
done

echo ""
echo "Updated $updated manifest(s). REVIEW THE DIFF — a status moving the right"
echo "way still needs a human to confirm the improvement is real."
[ "$failed" -gt 0 ] && echo "$failed manifest(s) FAILED to regenerate." >&2
exit $(( failed > 0 ? 1 : 0 ))
