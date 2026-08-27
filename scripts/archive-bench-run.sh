#!/usr/bin/env bash
# Archive a completed redaction-benchmark run into a datestamped, versioned
# history so performance can be tracked over time (#1185).
#
# Records WHICH excise (git commit) ran against WHICH bench version (manifest +
# design hash + case counts + tool versions), plus the run's metrics. Appends one
# compact line to the COMMITTED history (tests/redaction-bench-history.jsonl) and
# copies the full rows to a local archive (logs/redaction-benchmark/history/,
# gitignored — the committed index is the durable trend record).
#
# Usage: scripts/archive-bench-run.sh [results.jsonl] [corpus-label]
#   defaults: logs/redaction-benchmark/results.jsonl, "redaction-hard"
set -euo pipefail
cd "$(dirname "$0")/.."

RESULTS="${1:-logs/redaction-benchmark/results.jsonl}"
CORPUS="${2:-redaction-hard}"
[ -s "$RESULTS" ] || { echo "no results at $RESULTS" >&2; exit 1; }

HISTORY="tests/redaction-bench-history.jsonl"
ARCHIVE_DIR="logs/redaction-benchmark/history"
mkdir -p "$ARCHIVE_DIR"

# --- version metadata (captured at archive time == run time) ---
TS="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
STAMP="$(date -u +%Y%m%d-%H%M%S)"
COMMIT="$(git rev-parse HEAD 2>/dev/null || echo unknown)"
DESCRIBE="$(git describe --tags --always --dirty 2>/dev/null || echo unknown)"
DIRTY=$([ -n "$(git status --porcelain 2>/dev/null)" ] && echo true || echo false)
# bench version = hash over the manifest + design + adversarial fixture set
MANIFEST_SHA="$(cat tests/redaction-hard-cases.tsv tests/redaction-bench-design.json \
                test-pdfs/redaction-adversarial/.manifest-hash 2>/dev/null \
                | shasum -a 256 | cut -c1-16)"
DESIGN_VERSION="$(python3 -c "import json;print(json.load(open('tests/redaction-bench-design.json')).get('version','?'))" 2>/dev/null || echo '?')"
REAL_CASES="$(grep -vcE '^#|^$' tests/redaction-hard-cases.tsv 2>/dev/null || echo 0)"
SYNTH_FIXTURES="$(ls test-pdfs/redaction-adversarial/*.pdf 2>/dev/null | wc -l | tr -d ' ')"

# --- tool versions (empty string if absent) ---
tool_ver() { "$@" 2>&1 | head -1 | tr -d '"' || true; }
PYMUPDF="$(tools/vendor/xray-venv/bin/python -c 'import fitz;print(fitz.VersionBind)' 2>/dev/null || echo '')"
TESS="$(tesseract --version 2>&1 | head -1 | awk '{print $2}' || echo '')"
GS="$(gs --version 2>/dev/null || echo '')"
MUTOOL="$(mutool -v 2>&1 | head -1 | awk '{print $NF}' || echo '')"
QPDF="$(qpdf --version 2>/dev/null | head -1 | awk '{print $NF}' || echo '')"

ARCHIVE="$ARCHIVE_DIR/${STAMP}-${COMMIT:0:8}.jsonl"
cp "$RESULTS" "$ARCHIVE"

# --- compute metrics + emit the history line (python does the JSON) ---
python3 - "$RESULTS" "$TS" "$COMMIT" "$DESCRIBE" "$DIRTY" "$MANIFEST_SHA" \
         "$DESIGN_VERSION" "$REAL_CASES" "$SYNTH_FIXTURES" "$CORPUS" \
         "$PYMUPDF" "$TESS" "$GS" "$MUTOOL" "$QPDF" "$ARCHIVE" "$HISTORY" <<'PY'
import json, sys, collections
(results, ts, commit, describe, dirty, manifest_sha, design_version, real_cases,
 synth, corpus, pymupdf, tess, gs, mutool, qpdf, archive, history) = sys.argv[1:]

rows = [json.loads(l) for l in open(results) if l.strip()]
ok = [r for r in rows if not r.get("error")]
tools = sorted({r["tool"] for r in rows})

def leaked(r):
    return bool(r.get("leakSavedBytes") or r.get("leakOracleText")
                or (r.get("leakBadRedactions", 0) > 0)
                or r.get("visualTermReadable") == 1 or r.get("imageBakedReadable") == 1)

# leak rate by tier x tool  (tiers only meaningful for redaction-hard corpus)
by_tier = {}
tiers = sorted({r.get("difficulty", "") for r in ok if r.get("difficulty")})
for tier in tiers:
    by_tier[tier] = {t: None for t in tools}
    for t in tools:
        sub = [r for r in ok if r.get("difficulty") == tier and r["tool"] == t]
        by_tier[tier][t] = [sum(1 for r in sub if leaked(r)), len(sub)]

# security x fidelity per tool
sf = {}
for t in tools:
    g = [r for r in ok if r["tool"] == t]
    if not g: continue
    secure = sum(1 for r in g if not leaked(r))
    rf = [r for r in g if r.get("survivingRenderDelta", -1) >= 0]
    rok = sum(1 for r in rf if r.get("survivingRenderDelta", 1) < 0.02
              and not (r.get("inputQpdfOk") and not r.get("qpdfOk")))
    tk = sum(1 for r in g if r.get("collateralFraction", 1) < 0.02
             and r.get("survivingWordsDamaged", 1) == 0)
    sf[t] = {"secure": round(secure/len(g), 3),
             "rendersOk": round(rok/len(rf), 3) if rf else None,
             "textKept": round(tk/len(g), 3), "n": len(g)}

entry = {
    "timestamp": ts,
    "excise": {"commit": commit, "describe": describe, "dirty": dirty == "true"},
    "bench": {"designVersion": design_version, "manifestSha": manifest_sha,
              "corpus": corpus, "realCases": int(real_cases), "syntheticFixtures": int(synth)},
    "tools": {k: v for k, v in {
        "pymupdf": pymupdf, "tesseract": tess, "ghostscript": gs,
        "mutool": mutool, "qpdf": qpdf}.items() if v},
    "metrics": {"measured": len(ok), "errored": len(rows) - len(ok),
                "leakByTierTool": by_tier, "securityFidelity": sf},
    "archive": archive,
}
with open(history, "a") as f:
    f.write(json.dumps(entry) + "\n")

print(f"archived run {ts}")
print(f"  excise {describe} ({commit[:8]}{' DIRTY' if dirty=='true' else ''})")
print(f"  bench  design v{design_version} manifest {manifest_sha} "
      f"({real_cases} real + {synth} synthetic, corpus={corpus})")
print(f"  measured {len(ok)}, errored {len(rows)-len(ok)}")
for tier in tiers:
    cells = "  ".join(f"{t}={by_tier[tier][t][0]}/{by_tier[tier][t][1]}" for t in tools)
    print(f"  leak {tier:8} {cells}")
print(f"  full rows -> {archive}")
print(f"  history   -> {history}")
PY
