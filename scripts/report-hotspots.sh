#!/usr/bin/env bash
#
# Resource hotspot DETECTOR — deliberately not a gate.
#
# run-full-suite.sh records each step's peak RSS and CPU seconds. This turns
# that into a ranked list of OPTIMIZATION CANDIDATES and keeps a small history
# so growth over time is visible.
#
# A candidate is a question, not a defect. "Why does that step need 2 GB?" may
# have a perfectly good answer — it is still worth knowing, and worth writing
# down for a future release rather than rediscovering it when a user's machine
# starts swapping.
#
# WHAT THIS IS NOT
# ----------------
# It does not fail. It does not block anything. Real performance ENFORCEMENT
# lives in tests/perf-budgets/workflow-budgets.json via
# scripts/check-perf-budgets.sh, which anchors on managed ALLOCATION — nearly
# machine- and run-invariant, and therefore the only one of these signals
# suitable for a hard gate. Peak RSS and wall time are too machine- and
# contention-dependent to gate on; CLAUDE.md already records wall time
# producing FALSE REDS in this suite.
#
# TWO LIMITS, STATED UP FRONT
# ---------------------------
#   * Step-level RSS is COARSE. "Excise.App.Tests used 2 GB" covers ~1,285
#     tests in one process; it tells you where to look, not what to fix.
#     Finer-grained, per-workflow and per-PDF allocation data already exists in
#     tests/perf-budgets/workflow-budgets.json — start there once a step looks
#     interesting.
#   * CHUNKED steps (Excise.*.Tests.chunkNN) are within-run signal only. Chunk
#     membership shifts when --chunk-size changes or classes are added, so the
#     same chunk name across runs is not the same set of tests. They are ranked
#     but never compared against history.
#
# Usage:
#   scripts/report-hotspots.sh                 # newest full-suite run
#   scripts/report-hotspots.sh --run <dir>     # a specific run directory
#   scripts/report-hotspots.sh --update-issue 123
#
# --update-issue rewrites that issue's body with the current candidate table.
# It is never invoked automatically: a test run that mutates the tracker on
# every local invocation would be a surprise, so a human asks for it.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

RUN_DIR=""
ISSUE=""
HISTORY="${HOTSPOT_HISTORY:-$ROOT/logs/hotspot-history.tsv}"
# Ignore anything below this: small steps are noise, not candidates. This is a
# noise FLOOR, not a trigger — nothing is flagged merely for exceeding it.
FLOOR_MB="${HOTSPOT_FLOOR_MB:-200}"
# Relative thresholds are self-calibrating, so they need no retuning as the
# suite and the machine change.
MEDIAN_FACTOR="${HOTSPOT_MEDIAN_FACTOR:-1.5}"
GROWTH_FACTOR="${HOTSPOT_GROWTH_FACTOR:-1.25}"
# Cap the history so this cannot become the next #858 (unbounded generated data).
MAX_HISTORY_ROWS="${HOTSPOT_MAX_HISTORY_ROWS:-2000}"

while [ $# -gt 0 ]; do
    case "$1" in
        --run) RUN_DIR="${2:-}"; shift 2 ;;
        --update-issue) ISSUE="${2:-}"; shift 2 ;;
        --history) HISTORY="${2:-}"; shift 2 ;;
        -h|--help) sed -n '2,45p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "Unknown option: $1" >&2; exit 2 ;;
    esac
done

if [ -z "$RUN_DIR" ]; then
    # Newest run WITH DATA, not merely newest. `--list` and `--status`
    # invocations create a LOG_DIR and execute nothing, leaving an empty
    # resources.tsv that would otherwise shadow the last real run.
    for candidate in $(ls -td "$ROOT"/logs/full-suite_* 2>/dev/null); do
        if [ -s "$candidate/resources.tsv" ]; then RUN_DIR="$candidate"; break; fi
    done
fi
RESOURCES="$RUN_DIR/resources.tsv"

if [ ! -s "$RESOURCES" ]; then
    echo "No resource data found (looked for $RESOURCES)."
    echo "Run scripts/run-full-suite.sh first — it records peak RSS/CPU per step."
    exit 0
fi

RUN_ID="$(basename "$RUN_DIR")"
SHA="$(git rev-parse --short HEAD 2>/dev/null || echo nogit)"
mkdir -p "$(dirname "$HISTORY")"
touch "$HISTORY"

REPORT="$(mktemp)"
trap 'rm -f "$REPORT"' EXIT

python3 - "$RESOURCES" "$HISTORY" "$RUN_ID" "$SHA" "$FLOOR_MB" "$MEDIAN_FACTOR" "$GROWTH_FACTOR" "$MAX_HISTORY_ROWS" > "$REPORT" <<'PY'
import os, statistics, sys

resources, history, run_id, sha, floor, med_f, grow_f, max_rows = sys.argv[1:9]
floor, med_f, grow_f, max_rows = int(floor), float(med_f), float(grow_f), int(max_rows)

rows = []
for line in open(resources):
    parts = line.rstrip("\n").split("\t")
    if len(parts) < 3 or not parts[0]:
        continue
    name, rss, cpu = parts[0], parts[1], parts[2]
    if not rss:
        continue
    rows.append((name, int(rss), int(cpu) if cpu else 0))

if not rows:
    print("No usable resource rows in this run.")
    raise SystemExit

# ---- history: append this run once (idempotent on run_id), then cap ---------
prior = {}          # step -> most recent rss from an EARLIER run
seen_runs = set()
hist_lines = []
if os.path.exists(history):
    for line in open(history):
        p = line.rstrip("\n").split("\t")
        if len(p) < 5:
            continue
        h_run, h_sha, h_step, h_rss, h_cpu = p[:5]
        hist_lines.append(line.rstrip("\n"))
        seen_runs.add(h_run)
        if h_run != run_id:
            prior[h_step] = int(h_rss)      # later lines win => most recent

if run_id not in seen_runs:
    for name, rss, cpu in rows:
        hist_lines.append(f"{run_id}\t{sha}\t{name}\t{rss}\t{cpu}")
    with open(history, "w") as fh:
        fh.write("\n".join(hist_lines[-max_rows:]) + "\n")

# ---- candidates -------------------------------------------------------------
considered = [r for r in rows if r[1] >= floor]
median_rss = statistics.median([r[1] for r in considered]) if considered else 0

def is_chunk(name):
    return ".chunk" in name

candidates = []
for name, rss, cpu in considered:
    reasons = []
    if median_rss and rss > med_f * median_rss:
        reasons.append(f"{rss/median_rss:.1f}x this run's median ({median_rss:.0f} MB)")
    if not is_chunk(name) and name in prior and prior[name] > 0:
        ratio = rss / prior[name]
        if ratio > grow_f:
            reasons.append(f"grew {ratio:.2f}x vs previous run ({prior[name]} MB)")
    if reasons:
        candidates.append((name, rss, cpu, reasons))

candidates.sort(key=lambda c: -c[1])

print(f"### Resource hotspot candidates — `{sha}`\n")
print(f"Run: `{run_id}` · {len(rows)} measured steps · "
      f"median peak RSS {median_rss:.0f} MB (of steps ≥ {floor} MB)\n")

if not candidates:
    print("No candidates: no step is materially above this run's median, and none grew "
          "against the previous recorded run.\n")
else:
    print("| step | peak RSS | CPU s | why it is a candidate |")
    print("|---|---:|---:|---|")
    for name, rss, cpu, reasons in candidates:
        note = "; ".join(reasons)
        if is_chunk(name):
            note += " _(chunked step — within-run signal only)_"
        print(f"| `{name}` | {rss} MB | {cpu} | {note} |")
    print()

print("<details><summary>All measured steps</summary>\n")
print("| step | peak RSS | CPU s |")
print("|---|---:|---:|")
for name, rss, cpu in sorted(rows, key=lambda r: -r[1]):
    print(f"| `{name}` | {rss} MB | {cpu} |")
print("\n</details>\n")

print("_Detection only — nothing here fails a build._ A candidate is a question "
      "(\"why does that need this much?\"), and the answer may well be \"that is fine\". "
      "Performance ENFORCEMENT lives in `tests/perf-budgets/workflow-budgets.json` "
      "(`scripts/check-perf-budgets.sh`), which gates on managed allocation because "
      "allocation is machine-invariant and peak RSS is not.\n")
print("Step-level RSS is coarse — one step can cover a whole project. For per-workflow "
      "and per-PDF allocation, start from `tests/perf-budgets/workflow-budgets.json`.")
PY

cat "$REPORT"

if [ -n "$ISSUE" ]; then
    if ! command -v gh >/dev/null 2>&1; then
        echo "gh not available; cannot update issue #$ISSUE" >&2
        exit 1
    fi
    gh issue edit "$ISSUE" --body-file "$REPORT" >/dev/null && \
        echo "" && echo "Updated issue #$ISSUE with the table above."
fi
