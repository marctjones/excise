#!/usr/bin/env bash
#
# Extraction-parity gate (#645) — the prerequisite #513 needs before it
# touches the font resolver both the renderer and the text extractor share.
#
# Redaction completeness is bounded by extraction coverage: RedactText cannot
# remove what excise cannot read, and reports success anyway (#637). Two
# anecdotes (#636, #608, #637) each passed a fully green suite over a leaking
# document. This script is the corpus-wide measurement that replaces
# anecdote with a number, and a gate that fails when the number gets worse.
#
# WHY THIS IS A SEPARATE SCRIPT AND NOT JUST `dotnet test`:
# ExtractionParityTests (Excise.Rendering.Tests/Differential/) requires mutool
# and the smoke corpus (test-pdfs/smoke/, gitignored, downloaded on demand).
# Both are ABSENT on GitHub-hosted PR runners, so `dotnet test` filters the
# Differential category OUT of PR CI (see .github/workflows/ci.yml) — the
# test would otherwise silently report "0 tests found" and the PR would go
# green having measured nothing. That is exactly the invisible-coverage-loss
# failure #619's skip budget exists to catch elsewhere in this repo.
#
# So: this is a RELEASE gate (docs/RELEASE_CHECKLIST.md), not a PR gate, and
# unlike the underlying test it REFUSES TO SILENTLY SKIP. Missing mutool or
# missing corpus is a hard FAIL here, not a quiet pass.
#
# Usage:
#   scripts/check-extraction-parity.sh              # gate: fail on regression
#   scripts/check-extraction-parity.sh --update      # rewrite the baseline from
#                                                     # the current measurement
#                                                     # (review the diff before
#                                                     # committing — this is a
#                                                     # deliberate, reviewed
#                                                     # ratchet, not a rubber stamp)
set -euo pipefail

UPDATE=0
if [[ "${1:-}" == "--update" ]]; then UPDATE=1; fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

BASELINE="tests/extraction-parity/baseline.json"
REPORT="logs/extraction-parity/latest-report.json"
# Allow tiny run-to-run noise (font-fallback nondeterminism, mutool version
# drift) without failing the gate on jitter. A real regression is much larger
# than this in practice — see the worklist entries in the baseline.
TOLERANCE=0.02

command -v mutool >/dev/null 2>&1 || {
  echo "FAIL: mutool not found on PATH. Install mupdf-tools."
  echo "      This gate refuses to silently skip — that is the bug it exists to fix."
  exit 1
}

if [[ ! -d test-pdfs/smoke ]] || [[ -z "$(ls -A test-pdfs/smoke/*.pdf 2>/dev/null)" ]]; then
  echo "FAIL: no smoke corpus at test-pdfs/smoke. Run ./scripts/download-smoke-corpus.sh"
  echo "      This gate refuses to silently skip — that is the bug it exists to fix."
  exit 1
fi

echo "==> generating extraction parity report (mutool + excise over test-pdfs/smoke + test-pdfs/sample-pdfs)"
dotnet test Excise.Rendering.Tests -c Debug \
  --filter "FullyQualifiedName~ExtractionParityTests.GenerateExtractionParityReport" \
  --logger "console;verbosity=normal"

if [[ ! -f "$REPORT" ]]; then
  echo "FAIL: $REPORT was not produced. The generator test did not run — check the output above."
  exit 1
fi

python3 - "$REPORT" "$BASELINE" "$TOLERANCE" "$UPDATE" <<'PY'
import json, os, sys

report_path, baseline_path, tolerance, update = sys.argv[1], sys.argv[2], float(sys.argv[3]), sys.argv[4] == "1"

report = json.load(open(report_path))
pages = {(p["file"], p["page"]): p for p in report["pages"]}

if update:
    new_pages = {
        f"{f}#{n}": {
            "coverageFloor": round(p["coverageRatio"], 4),
            "similarityFloor": round(p["similarity"], 4),
        }
        for (f, n), p in pages.items()
    }
    baseline = {
        "generatedUtc": report["generatedUtc"],
        "mutoolVersion": report.get("mutoolVersion", "unknown"),
        "aggregateCoverage": round(report["aggregateCoverage"], 4),
        "pageCount": report["pageCount"],
        "pages": new_pages,
    }
    os.makedirs(os.path.dirname(baseline_path), exist_ok=True)
    with open(baseline_path, "w") as fh:
        json.dump(baseline, fh, indent=2, sort_keys=True)
        fh.write("\n")
    print(f"==> baseline updated: {len(new_pages)} pages, aggregate coverage {baseline['aggregateCoverage']:.1%}")
    print(f"    review the diff before committing: git diff {baseline_path}")
    sys.exit(0)

if not os.path.exists(baseline_path):
    print(f"FAIL: no baseline at {baseline_path}. Run with --update to create one (review the diff before committing).")
    sys.exit(1)

baseline = json.load(open(baseline_path))
if not baseline.get("pages"):
    print(f"FAIL: {baseline_path} has no page floors. Run with --update.")
    sys.exit(1)

regressions = []
for key, floor in baseline["pages"].items():
    f, n = key.rsplit("#", 1)
    n = int(n)
    current = pages.get((f, n))
    if current is None:
        print(f"NOTE: baseline page {key} is missing from the current report (corpus changed?)")
        continue
    if current["coverageRatio"] < floor["coverageFloor"] - tolerance:
        regressions.append((key, "coverage", current["coverageRatio"], floor["coverageFloor"]))
    if current["similarity"] < floor["similarityFloor"] - tolerance:
        regressions.append((key, "similarity", current["similarity"], floor["similarityFloor"]))

new_keys = set(f"{f}#{n}" for f, n in pages) - set(baseline["pages"].keys())
if new_keys:
    print(f"NOTE: {len(new_keys)} pages in the report aren't in the baseline yet (new corpus pages). Run --update to add them.")

if regressions:
    print(f"FAIL: {len(regressions)} extraction-parity regression(s):")
    for key, metric, current, floor in regressions:
        print(f"  {key}: {metric} dropped to {current:.3f} (floor {floor:.3f})")
    sys.exit(1)

print(f"==> extraction parity OK: {len(baseline['pages'])} pages at or above their baseline floor")
print(f"    aggregate coverage (baseline): {baseline['aggregateCoverage']:.1%}")
PY
