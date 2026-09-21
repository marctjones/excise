#!/usr/bin/env bash
# TOOLING — not a gate (tests/gates-tooling.txt).
#
# Archive a completed GUI performance run into a datestamped, committed history
# so footprint and latency can be tracked over time. Modelled deliberately on
# scripts/archive-bench-run.sh, which does the same job for the redaction bench:
# one compact JSON line per run in a COMMITTED .jsonl, so the trend survives
# even though the run directories under logs/ are gitignored and transient.
#
# ⚠️ WHICH COLUMN TO TREND DEPENDS ON THE BOUNDARY. Measured 2026-09-20
# (logs/gui-perf_20260920_162501, altona-scroll x5) against the CI spike's
# hosted-runner data (#1699):
#   - At a POST-CACHE-TRIM idle boundary (altona-scroll's idle-45s), the
#     managed-heap columns win. liveHeapMB read 350.3 / 247.6 / 156.9 / 156.9 /
#     156.7 -- a warm-up decay that CONVERGES, last three spanning 0.2 MB
#     (CV 0.07%). rssMB at the same boundary did NOT converge (CV 13.3%).
#   - At a CLOSE boundary, the reverse: #1699 found macOS liveHeap bimodal at
#     after-close-1 (CV 21.64%, a 13 MB gap with nothing between the clusters)
#     while working set held CV 2.11%.
#   So: trend liveHeapMB/committedMB after a trim, rssMB after a close, and
#   never assume one column is universally better.
#
# ⚠️ DISCARD THE WARM-UP REPS. The decay above spans the first TWO reps of a
# five-rep run and settles at rep 3. This script takes MEDIANS, which is robust
# to that -- but the harness's own noise floor is derived from the SPREAD of the
# same reps, so warm-up inflates it (liveHeapMB floor 102.7 MB on that run). A
# floor computed across a warm-up curve measures the warm-up, not the noise, and
# would hide a real regression smaller than it. Do not trust a spread-derived
# floor from a run whose first reps are cold.
#
# ⚠️ These are ABSOLUTE numbers from ONE machine under whatever load it had.
# They are not a gate and must never become one — scripts/run-gui-perf-scenarios.sh
# explains why at length (a gate on absolute footprint fails on load, not on
# code). This history answers "did it move, and when", not "is it fast enough".
#
# Usage: scripts/archive-gui-perf-run.sh [logs/gui-perf_<stamp>] [label]
#   default: the most recent logs/gui-perf_* directory
set -euo pipefail
cd "$(dirname "$0")/.."

RUN_DIR="${1:-$(ls -1dt logs/gui-perf_2* 2>/dev/null | head -1)}"
LABEL="${2:-}"
[ -n "$RUN_DIR" ] && [ -s "$RUN_DIR/run.json" ] || {
  echo "no run.json under ${RUN_DIR:-<no gui-perf run found>}" >&2; exit 1; }

# GUI_PERF_HISTORY redirects the line, the way REDACTION_BENCH_HISTORY does, so
# an automated run can record without dirtying the tree. Committing a history
# point stays a deliberate, manual invocation.
HISTORY="${GUI_PERF_HISTORY:-tests/gui-perf-history.jsonl}"

python3 - "$RUN_DIR" "$HISTORY" "$LABEL" <<'PY'
import json, subprocess, sys, statistics as st, pathlib, datetime

run_dir, history, label = sys.argv[1], sys.argv[2], sys.argv[3]
doc = json.load(open(f"{run_dir}/run.json"))
runs = doc["runs"] if isinstance(doc, dict) and "runs" in doc else doc

def git(*a):
    try: return subprocess.check_output(["git", *a], text=True).strip()
    except Exception: return "unknown"

def med(vals):
    return round(st.median(vals), 1) if vals else None

def step_metric(rs, step, field):
    return [x[field] for r in rs for x in r["steps"]
            if str(x.get("step")) == step and field in x]

scenarios = {}
for sc in sorted({r["scenario"] for r in runs}):
    rs = [r for r in runs if r["scenario"] == sc]
    steps = [str(x.get("step")) for x in rs[0]["steps"]]
    last = steps[-1] if steps else None
    m = lambda k: [r["metrics"].get(k) for r in rs if r["metrics"].get(k) is not None]
    scenarios[sc] = {
        "repeats": len(rs),
        "terminalStep": last,
        # Terminal boundary: the resting figures, which is what a trend wants.
        "rssMB": med(step_metric(rs, last, "rssMB")),
        "footprintMB": med(step_metric(rs, last, "footprintMB")),
        "committedMB": med(step_metric(rs, last, "committedMB")),
        "liveHeapMB": med(step_metric(rs, last, "liveHeapMB")),
        "fragmentedMB": med(step_metric(rs, last, "fragmentedMB")),
        "peakRssMB": med([max(x["rssMB"] for x in r["steps"]) for r in rs]),
        "cpuTotalMs": med([max(x["cpuTotalMs"] for x in r["steps"]) for r in rs]),
        "bandRenderP50Ms": med(m("bandRenderP50Ms")),
        "bandRenderP99Ms": med(m("bandRenderP99Ms")),
        "heapReclaims": med(m("heapReclaims")),
        "load1": med([x["load1"] for r in rs for x in r["steps"] if "load1" in x]),
    }

line = {
    "timestamp": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%dT%H:%M:%SZ"),
    "runDir": pathlib.Path(run_dir).name,
    "label": label or None,
    "excise": {
        "commit": git("rev-parse", "HEAD"),
        "describe": git("describe", "--tags", "--always", "--dirty"),
        "dirty": bool(git("status", "--porcelain")),
    },
    # The floor is what separates a real move from noise. Recording it beside the
    # numbers is the point: a delta under its floor is NOT a result.
    "noiseFloor": doc.get("floors") if isinstance(doc, dict) else None,
    "calibrated": bool(pathlib.Path(run_dir, "calibration.json").exists()),
    "scenarios": scenarios,
}

with open(history, "a") as f:
    f.write(json.dumps(line, separators=(",", ":")) + "\n")
print(f"appended {len(scenarios)} scenario(s) to {history}")
for name, s in scenarios.items():
    print(f"  {name:<22} rss {s['rssMB']} / peak {s['peakRssMB']} MB · "
          f"committed {s['committedMB']} · p99 {s['bandRenderP99Ms']} ms · "
          f"reclaims {s['heapReclaims']}")
PY
