#!/usr/bin/env bash
#
# Split a corpus scan's non-PASS pages into "corroborated refusal" and
# "excise-side gap", so a genuine defect cannot be pinned into an expectation
# manifest as though it were expected behaviour.
#
# WHY THIS EXISTS
# ---------------
# update-corpus-expectations.sh writes every page's status verbatim. That is
# right for PASS_ONE, MALFORMED_PDF, EMPTY_DOC and friends — a hostile fixture
# that every renderer refuses is *correctly* refused, and pinning it catches the
# day we start crashing on it instead. It is wrong for a page where mutool and
# pdftocairo both rendered happily and only excise errored: pinning that records
# "excise fails here" as the desired outcome and the gate then defends the bug.
#
# The discriminator needs no judgement and is already in the report. Two fields
# carry it: `renderMs` is set iff excise produced a bitmap, and each oracle's
# `<name>Status` is "OK" iff that oracle produced one.
#
#   excise rendered                          →  agreement/coverage question
#                                               (PASS_ONE, EXCISE_ONLY — pin)
#   excise did not, no oracle did either     →  corroborated refusal    (pin)
#   excise did not, but an oracle did        →  EXCISE-SIDE GAP  (file it first)
#
# Getting this split wrong in the obvious direction — treating "some oracle
# rendered" alone as the gap signal — misfiles all 20 pdf.js PASS_ONE pages as
# defects, when PASS_ONE means excise rendered fine and only one oracle was
# there to agree. The excise-side question is whether excise produced anything,
# not whether the oracles did.
#
# This is the same rule as the rest of the differential suite: excise is never
# its own oracle for the property it exists to guarantee.
#
# Usage:
#   ./scripts/triage-corpus-nonpass.sh <report.json>
#   ./scripts/triage-corpus-nonpass.sh pdfium          # by corpus key
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
BIN="$ROOT/Excise.Rendering.Tests/bin/Debug/net10.0"

ARG="${1:-}"
if [ -z "$ARG" ]; then
    echo "usage: $0 <report.json | pdfjs | pdfium | isartor | verapdf>" >&2
    exit 2
fi

case "$ARG" in
    pdfjs)   REPORT="$BIN/exploratory-report.json" ;;
    pdfium)  REPORT="$BIN/exploratory-report-test-pdfs-pdfium-first.json" ;;
    isartor) REPORT="$BIN/exploratory-report-test-pdfs-isartor-first.json" ;;
    verapdf) REPORT="$BIN/exploratory-report-test-pdfs-verapdf-corpus-first.json" ;;
    *)       REPORT="$ARG" ;;
esac

if [ ! -f "$REPORT" ]; then
    echo "✗ no report at $REPORT" >&2
    exit 1
fi

python3 - "$REPORT" <<'PY'
import json, sys, collections

d = json.load(open(sys.argv[1]))
rows = d if isinstance(d, list) else (d.get("results") or d.get("entries") or [])

# Every oracle's per-page status field. Missing keys are simply absent oracles.
# Note the casing: the report writes `pdfboxStatus`, not `pdfBoxStatus` — a
# silent typo here would make every PDFBox render invisible to the triage.
ORACLES = {
    "mutool":      "mutoolStatus",
    "pdftocairo":  "cairoStatus",
    "ghostscript": "ghostscriptStatus",
    "pdfbox":      "pdfboxStatus",
    "pdfium":      "pdfiumStatus",
}

def oracles_ok(r):
    return [name for name, key in ORACLES.items() if r.get(key) == "OK"]

# renderMs is populated iff excise produced a bitmap for the page.
def excise_rendered(r):
    return r.get("renderMs") is not None

nonpass = [r for r in rows if r.get("status") != "PASS"]
rendered, refusal, gap = [], [], []
for r in nonpass:
    if excise_rendered(r):
        rendered.append(r)
    elif oracles_ok(r):
        gap.append(r)
    else:
        refusal.append(r)

def summarise(label, bucket, note):
    print(f"  {label}: {len(bucket)}")
    print(f"      {note}")
    for st, n in collections.Counter(r.get("status") for r in bucket).most_common():
        print(f"      {st:32} {n:4}")
    print()

print(f"  {len(rows)} pages, {len(nonpass)} non-PASS")
print()
summarise("EXCISE RENDERED — agreement/coverage question, not a render failure",
          rendered, "safe to pin: this is what PASS_ONE and EXCISE_ONLY mean")
summarise("CORROBORATED REFUSAL — excise produced nothing, and neither did any oracle",
          refusal, "safe to pin: a fixture every renderer refuses is correctly refused")
summarise("EXCISE-SIDE GAP — excise produced nothing, but an oracle did",
          gap, "NOT safe to pin bare: an oracle proves the page is renderable")

# "No oracle rendered either" is NOT corroboration when the reason is that
# nobody had the password — every renderer was locked out equally, which says
# nothing about whether the page is renderable. These belong in
# tests/corpus-passwords.tsv (or UnknownCredential with a reason), and
# EncryptedCorpusPasswordCoverageTests is what enforces that.
CREDENTIAL_BLOCKED = {"PASSWORD_REQUIRED", "UNSUPPORTED_ENCRYPTED"}
locked = [r for r in refusal if r.get("status") in CREDENTIAL_BLOCKED]
if locked:
    print(f"  ⚠ {len(locked)} of those refusals are CREDENTIAL-BLOCKED, not corroborated —")
    print("    every renderer was locked out equally, which proves nothing about the page:")
    for r in sorted(locked, key=lambda r: r.get("path") or ""):
        print(f"      {r.get('status'):28} {r.get('path')}")
    print("    Add a password to tests/corpus-passwords.tsv, or record why it is unobtainable.")
    print()

# excise rendered AND disagreed. Pinning is legitimate (it is today's measured
# behaviour) but it freezes a live discrepancy, so it should never be silent.
disagree = [r for r in rendered if r.get("status") == "DIFF"]
if disagree:
    print(f"  ⚠ {len(disagree)} page(s) rendered but DISAGREE with the oracles —")
    print("    pinning records the discrepancy as expected; it does not resolve it:")
    for r in sorted(disagree, key=lambda r: r.get("path") or ""):
        rank = r.get("exciseReferenceCenterRank")
        frac = r.get("diffFraction")
        extra = []
        if rank is not None:
            extra.append(f"centrality-rank={rank}")
        if frac is not None:
            extra.append(f"diff={frac:.4f}")
        print(f"      {r.get('path')}  {' '.join(extra)}")
    print("    centrality-rank=1 means excise is the MOST central of the renderers,")
    print("    i.e. the oracles disagree with each other more than with excise.")
    print()

# A page excise recovered rather than rendered cleanly is pinnable but should
# not be silent — it is degraded output, not agreement.
degraded = [r for r in rendered if r.get("errorType")]
if degraded:
    print(f"  (of those rendered, {len(degraded)} were degraded/recovered:")
    for r in degraded:
        print(f"      {r.get('status'):28} {r.get('path')}  [{r.get('errorType')}]")
    print("   pinning is fine, but the status is doing the remembering.)")
    print()

if gap:
    print("  These must NOT be pinned as plain expected statuses — an oracle")
    print("  proves the page is renderable, so the status records an excise bug:")
    for r in sorted(gap, key=lambda r: (r.get("status") or "", r.get("path") or "")):
        p = r.get("path") or r.get("file") or "?"
        ok = ",".join(oracles_ok(r))
        err = (r.get("errorMessage") or "").strip().replace("\n", " ")[:90]
        print(f"      {r.get('status'):28} {p}")
        print(f"          rendered by: {ok}")
        if err:
            print(f"          excise: {err}")

raise SystemExit(1 if gap else 0)
PY
