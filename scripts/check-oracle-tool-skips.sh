#!/usr/bin/env bash
#
# check-oracle-tool-skips.sh: a test that SKIPPED because an independent tool
# is missing is a failed gate, not a pass (#1781).
#
# Excise.Core.Tests holds ~70 tests that shell out to mutool, pdftotext, qpdf,
# pdfimages, pdfinfo, pdfcpu, Ghostscript or veraPDF -- the de-redaction
# recovery oracles, the only veraPDF cross-check of PdfUaValidator, the writer
# round-trips. Each one does Assert.SkipUnless(tool present, "mutool is not on
# PATH"). Take the tool away and every one of them becomes a declared-reason
# skip: the project row stays green, skip-budget-core accepts the reason
# (#1172 only checks that a reason exists), and the core-oracles floor never
# saw them because they do not follow its `_WhenAvailable` naming convention.
# The whole oracle layer can go silent with every gate green.
#
# This gate reads the trx the project row already wrote (no second test run)
# and fails on any skip whose reason says a tool is absent. It keys on the
# REASON, not on a method-name convention or a filter, so a new tool-gated
# test is covered the day it is written, whatever it is called.
#
# A skip is a tool-absence skip when its reason
#   * says "not on PATH", "not installed" or "must be on PATH", or
#   * names one of the independent tools below and is NOT a #1527
#     path-absence reason ("[excise-searched: ...]", re-verified by
#     check-skip-budget.sh) -- "veraPDF corpus" is a corpus, not the tool.
# Not covered: opj_decompress (the reason calls it an optional fallback, and it
# is not an oracle), and a reason that names no tool and uses none of the
# phrases. The selftest (scripts/test-check-oracle-tool-skips.sh) pins both
# the catch and the corpus exclusion.
#
# On the owner's machine every one of these tools is installed, so a red here
# means a tool vanished (an upgrade renamed a binary, PATH changed) or a probe
# broke. Install the tool; do not reword the skip reason to dodge this gate.
#
# Usage: scripts/check-oracle-tool-skips.sh [--label <text>] --trx <file> [--trx <file>]...
#   --trx may repeat: t1 and full chunk the project by class, and the union of
#   the chunk trx files is one run. Every --trx must exist and be non-empty.
set -euo pipefail

LABEL="tool-gated tests"
TRX=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --label) LABEL="${2:?--label needs text}"; shift ;;
    --trx) TRX+=("${2:?--trx needs a file}"); shift ;;
    *) echo "check-oracle-tool-skips: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

if [[ ${#TRX[@]} -eq 0 ]]; then
  echo "FAIL: no --trx given -- nothing ran, so there is nothing to vouch for." >&2
  exit 1
fi
for t in "${TRX[@]}"; do
  if [[ ! -s "$t" ]]; then
    echo "FAIL: no trx at $t -- the run that should have written it did not complete." >&2
    echo "      It is one part of a ${#TRX[@]}-file union; reading the rest as the whole" >&2
    echo "      would report its tool skips as absent." >&2
    exit 1
  fi
done

python3 - "$LABEL" "${TRX[@]}" <<'PY'
import re
import sys
import xml.etree.ElementTree as ET

label, files = sys.argv[1], sys.argv[2:]
PHRASE = re.compile(r"not on PATH|not installed|must be on PATH", re.IGNORECASE)
TOOL = re.compile(
    r"(?<![A-Za-z0-9_])(mutool|mupdf|pdftotext|pdfimages|pdfinfo|pdftoppm|pdftocairo|pdfsig|"
    r"poppler|qpdf|ghostscript|gs|pdfcpu|verapdf|pdfbox|tesseract)(?![A-Za-z0-9_])",
    re.IGNORECASE)
CORPUS_ABSENCE = "excise-searched:"

total = 0
tool_skips = []
for path in files:
    try:
        root = ET.parse(path).getroot()
    except ET.ParseError as e:
        print(f"FAIL: {path} is not a readable trx ({e}); a torn trx is not 'no skips'.", file=sys.stderr)
        sys.exit(1)
    ns = root.tag.split("}")[0] + "}" if root.tag.startswith("{") else ""
    for r in root.iter(f"{ns}UnitTestResult"):
        total += 1
        if r.get("outcome") != "NotExecuted":
            continue
        m = r.find(f".//{ns}Message")
        reason = (m.text or "").strip() if m is not None else ""
        names_tool = TOOL.search(reason) and CORPUS_ABSENCE not in reason
        if PHRASE.search(reason) or names_tool:
            tool_skips.append((r.get("testName", "?"), reason))

print(f"{label}: {total} results read from {len(files)} trx file(s); "
      f"{len(tool_skips)} skipped for a missing independent tool")
if total == 0:
    print("FAIL: the trx holds ZERO results -- the run discovered nothing, which is not a clean run.",
          file=sys.stderr)
    sys.exit(1)
if tool_skips:
    print("\nFAIL: these tests did not run because an independent tool is missing.", file=sys.stderr)
    print("  They are the oracle layer; a skip here is that layer switched off with every", file=sys.stderr)
    print("  other gate green (#1781). Install the tool (scripts/check-oracle-tools.sh names", file=sys.stderr)
    print("  what is missing); do not reword the reason.\n", file=sys.stderr)
    for name, reason in sorted(tool_skips):
        print(f"    {name}\n        reason: {reason[:200]}", file=sys.stderr)
    sys.exit(1)
print("  ok")
PY
