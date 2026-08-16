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
    if ! python3 - "$src" "$key" "$ROOT/tests/$manifest" > "$tmp" <<'PY'
import json, os, sys, collections
src, key, previous_path = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(src))

# Hand-written expectations this generator CANNOT reproduce (#907).
#
# The generator writes each page's measured status verbatim, which silently
# destroys any row a human annotated: issue19517.pdf was pinned "*" with the
# note "Reference renderer timeouts make PASS/PASS_ONE load-dependent", and one
# regeneration turned it into a literal PASS — re-arming exactly the false-red
# the wildcard existed to prevent. Nothing warned; it was caught by reading the
# diff, which is not a control.
annotated = {}
if os.path.exists(previous_path):
    with open(previous_path) as fh:
        for line in fh:
            if line.startswith("#") or not line.strip():
                continue
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 3:
                continue
            path, page, status = parts[0], parts[1], parts[2]
            note = parts[4] if len(parts) > 4 else ""
            if status == "*" or note:
                annotated[(path, page)] = (status, note)

# Refuse to pin a baseline from a run that did not cover everything (#879).
# A partial report contains real results, so it looks perfectly usable — but
# every page a lost chunk never reached would simply be ABSENT from the
# manifest, and an absent page is not gated at all. The failure is silent in
# exactly the direction that matters: coverage quietly shrinks and the gate
# still reports green.
if isinstance(d, dict) and d.get("partial"):
    ps = d.get("partialSlices") or []
    sys.stderr.write(
        f"REFUSING to generate from a PARTIAL report: {src}\n"
        f"  {len(ps)} chunk(s) did not finish; pages they never reached would be\n"
        f"  missing from the manifest and therefore ungated.\n"
        f"  Re-run the scan to completion first.\n")
    raise SystemExit(2)

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
print("#   MALFORMED_PDF / EMPTY_DOC  the fixture is broken or hostile on")
print("#                purpose; refusing it is correct")
print("#   AGREED_REFUSAL  excise refused AND no oracle rendered it either —")
print("#                corroborated refusal, correct behaviour (#907)")
print("#")
print("# One status is NOT a correct outcome and must never be pinned bare:")
print("#   EXCISE_SIDE_GAP  excise refused but an oracle rendered the page. An")
print("#                oracle proves it is renderable, so this records an excise")
print("#                bug. File it, then pin the row with the issue number in")
print("#                the note column — or fix it.")
print("#   *            load-dependent when measured (see the note on the row) —")
print("#                asserts only that the page was scanned")
print("#")
print("# Keys are corpus-relative PATHS, not basenames: this corpus may have")
print("# subdirectories and duplicate filenames.")
c = collections.Counter(r.get("status") for r in rows)
print("# Baseline: " + ", ".join(f"{k}={v}" for k, v in c.most_common()))

def path_of(r):
    return r.get("path") or r.get("file") or ""

# Statuses that depend on how loaded the machine was, not on what the code does.
# TIMEOUT is measured against a 15s per-file budget under 14-way chunk
# parallelism: the same page can be TIMEOUT on a busy run and PASS on an idle
# one. Pinning it literally makes the gate go red in the GOOD direction, and a
# gate that false-reds teaches people to regenerate the manifest reflexively —
# which is exactly how a real regression gets waved through. Pin these as "*",
# which still asserts the page was scanned and did not take down the run.
LOAD_DEPENDENT = {"TIMEOUT"}

gaps = []
lost = []
for r in sorted(rows, key=path_of):
    p = path_of(r)
    page = r.get("page") or r.get("pageNumber") or 1
    st = r.get("status")
    if not (p and st):
        continue
    was = annotated.get((p, str(page)))
    if was and not (st in LOAD_DEPENDENT or st == "EXCISE_SIDE_GAP"):
        lost.append((p, page, was[0], was[1], st))
    if st in LOAD_DEPENDENT:
        print(f"{p}\t{page}\t*\t\tload-dependent ({st} when measured); any terminal status accepted")
    elif st == "EXCISE_SIDE_GAP":
        # Pinned, because an ungated page is worse than a gated bug — but never
        # silently: the row carries its own indictment and the run warns (#907).
        gaps.append(f"{p}#p{page}")
        print(f"{p}\t{page}\t{st}\t\tUNTRIAGED excise-side gap: an oracle rendered "
              f"this page and excise did not (was {r.get('refusedAs') or 'a refusal'}) "
              f"— file an issue and replace this note with its number")
    else:
        print(f"{p}\t{page}\t{st}")

if gaps:
    sys.stderr.write(
        f"  ⚠ {len(gaps)} EXCISE_SIDE_GAP page(s) pinned — each is a DEFECT, not an\n"
        "    expectation. An oracle rendered a page excise refused:\n")
    for g in gaps:
        sys.stderr.write(f"      {g}\n")

if lost:
    sys.stderr.write(
        f"  ⚠ {len(lost)} hand-annotated row(s) OVERWRITTEN with a measured status.\n"
        "    The generator cannot reproduce a human's judgement — re-apply any that\n"
        "    still hold (a '*' usually guards against a load-dependent false red):\n")
    for p, page, status, note, new_status in lost:
        sys.stderr.write(f"      {p}#p{page}: was '{status}' -> now '{new_status}'\n")
        if note:
            sys.stderr.write(f"          note was: {note}\n")
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
