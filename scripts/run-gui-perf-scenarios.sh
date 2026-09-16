#!/usr/bin/env bash
# TOOLING — not a gate (tests/gates-tooling.txt): drives the live GUI, and its
# numbers are absolute footprint on ONE machine. A gate on those would fail on
# load, not on code — the lesson tests/reference-performance already encodes
# (two identical 3-run passes minutes apart moved every fixture 1.27-2.24x in
# the same direction; the machine, not the code).
#
# Unattended live GUI performance measurement for #1497.
#
# WHAT THIS REPLACES
#   Every live GUI performance verdict so far (#1477, #1478, #1481, #1492,
#   #1483's L1-L5) needed Marc to page, scroll and open files by hand, and to
#   run `sudo memory_pressure` himself. That costs his time, the numbers are not
#   repeatable, and they varied with how the app was driven. This runs the same
#   sequences from inside the app and samples the process from outside.
#
# WHAT IT IS NOT
#   NOT headless. Avalonia.Native needs CoreVideo to bind an active display or
#   the app dies at startup with -6661, so a display is REQUIRED. What this is
#   is *unattended*: nobody has to sit in front of it. The preflight refuses to
#   launch into a wedged display state instead of burning launches.
#
#   NOT a measure of whether scrolling FEELS smooth. That stays human. The
#   proxies reported here are band render p50/p99 and the tile cache counters.
#
# THE CREDIBILITY RULE
#   Every number is printed next to a FLOOR:
#       floor = max(noise spread, runner overhead, sampler overhead)
#   A delta below its floor is printed as BELOW-FLOOR, never as a win. Run
#   `--calibrate` to measure the floor on this machine; without a calibration
#   file the summary says the floor is UNKNOWN rather than assuming it is zero.
#
# USAGE
#   scripts/run-gui-perf-scenarios.sh                       # every scenario, 1 repeat
#   scripts/run-gui-perf-scenarios.sh --scenario altona-close --repeats 5
#   scripts/run-gui-perf-scenarios.sh --calibrate           # runner + sampler overhead, noise floor
#   scripts/run-gui-perf-scenarios.sh --list                # print the plan, run nothing
#   scripts/run-gui-perf-scenarios.sh --baseline logs/gui-perf_OLD/run.json
#
# OPTIONS
#   --scenario ID       one scenario (repeatable); default: all
#   --repeats N         repeats per scenario (default 1; use 5 for a noise floor)
#   --sample MODE       boundary (default) | 1s | 5s | none
#                       How much the OUTER sampler does; the knob the
#                       sampler-overhead calibration sweeps. `none` takes NO
#                       outer samples at all and acks boundaries immediately.
#                       (It was called `end-only` and that was a lie: the
#                       runner quits the process before an end sample could
#                       be taken, so no end sample ever existed.)
#   --vmmap             also run `vmmap --summary` at each boundary (expensive:
#                       it suspends the process; off by default)
#   --calibrate         run the four calibration measurements and stop
#   --baseline FILE     compare against a previous run.json
#   --out DIR           output dir (default logs/gui-perf_<stamp>)
#   --app PATH          use an existing .app bundle instead of building
#   --no-build          reuse the bundle under --out/bundle
#   --keep-bundle       do not delete the bundle afterwards
#   --list              print the plan and exit
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# A calibration pass is ~64 launches, ~25 min. A sleeping DISPLAY makes the app
# die at startup with -6661, so hold the DISPLAY awake (-d), not just the
# system (-i). `-i` alone was the first version, and the first calibration
# aborted after 13 launches: displaysleep is 10 min on this machine,
# PreventUserIdleDisplaySleep was 0, and the preflight read activeDisplays=0.
if [ -z "${EXCISE_GUI_PERF_CAFFEINATED:-}" ] && command -v caffeinate >/dev/null 2>&1; then
  export EXCISE_GUI_PERF_CAFFEINATED=1
  exec caffeinate -d -i "$0" "$@"
fi

SCENARIO_FILE="$ROOT/tests/gui-perf-scenarios.json"
STAMP="$(date +%Y%m%d_%H%M%S)"
OUT="$ROOT/logs/gui-perf_$STAMP"
REPEATS=1
SAMPLE_MODE="boundary"
DO_VMMAP=0
DO_CALIBRATE=0
BASELINE=""
APP=""
BUILD=1
KEEP_BUNDLE=0
LIST_ONLY=0
SELECTED=()

while [ "$#" -gt 0 ]; do
  case "$1" in
    --scenario) SELECTED+=("$2"); shift 2 ;;
    --repeats) REPEATS="$2"; shift 2 ;;
    --sample) SAMPLE_MODE="$2"; shift 2 ;;
    --vmmap) DO_VMMAP=1; shift ;;
    --calibrate) DO_CALIBRATE=1; shift ;;
    --baseline) BASELINE="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --app) APP="$2"; BUILD=0; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --keep-bundle) KEEP_BUNDLE=1; shift ;;
    --scenarios-file) SCENARIO_FILE="$2"; shift 2 ;;
    --list) LIST_ONLY=1; shift ;;
    -h|--help) sed -n '2,60p' "$0"; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

case "$SAMPLE_MODE" in
  boundary|1s|5s|none) ;;
  *) echo "--sample must be boundary|1s|5s|none" >&2; exit 2 ;;
esac

BUNDLE_DIR="$OUT/bundle"
EXE="$BUNDLE_DIR/excise.app/Contents/MacOS/Excise.App"
[ -n "$APP" ] && EXE="$APP/Contents/MacOS/Excise.App"

# ---------------------------------------------------------------- scenario plan

if [ ! -f "$SCENARIO_FILE" ]; then
  echo "scenario file not found: $SCENARIO_FILE" >&2
  exit 1
fi

all_scenarios() {
  python3 -c '
import json, sys
doc = json.load(open(sys.argv[1]))
for s in doc["scenarios"]:
    print(s["id"])
' "$SCENARIO_FILE"
}

scenario_docs() {
  # Every document a scenario needs, so a missing fixture is reported up front
  # rather than three minutes into a launched run.
  python3 -c '
import json, sys
doc = json.load(open(sys.argv[1]))
want = set(sys.argv[2:]) or None
for s in doc["scenarios"]:
    if want and s["id"] not in want: continue
    for step in s["steps"]:
        if step.get("document"): print(step["document"])
' "$SCENARIO_FILE" "${SELECTED[@]+"${SELECTED[@]}"}" | sort -u
}

# NOTE: macOS ships bash 3.2 — no `mapfile`, no `local -n`, no associative
# arrays. Everything below stays inside 3.2, the same constraint
# check-unwired-api.sh records having learned the hard way.
if [ "${#SELECTED[@]}" -eq 0 ]; then
  while IFS= read -r line; do
    [ -n "$line" ] && SELECTED[${#SELECTED[@]}]="$line"
  done < <(all_scenarios)
else
  # A scenario name that matches nothing is a TYPO, not "run everything" — the
  # same rule --fixture enforces in the reference-performance bench.
  known="$(all_scenarios)"
  for want in "${SELECTED[@]}"; do
    if ! printf '%s\n' "$known" | grep -qxF "$want"; then
      echo "unknown scenario '$want'. Known:" >&2
      printf '  %s\n' $known >&2
      exit 2
    fi
  done
fi

MISSING=0
while read -r doc; do
  [ -z "$doc" ] && continue
  if [ ! -f "$ROOT/$doc" ]; then
    echo "MISSING FIXTURE: $doc"
    MISSING=1
  fi
done < <(scenario_docs)

if [ "$LIST_ONLY" = "1" ]; then
  echo "scenario file : $SCENARIO_FILE"
  echo "scenarios     : ${SELECTED[*]}"
  echo "repeats       : $REPEATS"
  echo "sample mode   : $SAMPLE_MODE   (vmmap=$DO_VMMAP)"
  echo "output        : $OUT"
  echo "launches      : $(( ${#SELECTED[@]} * REPEATS ))"
  [ "$MISSING" = "1" ] && echo "NOTE: fixtures above are missing; those scenarios would fail."
  exit 0
fi

if [ "$MISSING" = "1" ]; then
  echo "refusing to run with missing fixtures (download the corpora first)" >&2
  exit 1
fi

mkdir -p "$OUT"

# ------------------------------------------------------------------ safety gates

# Never measure while something else owns the machine. A concurrent test host
# is enough to move every number here (perf-spread-is-load, and the false reds
# App.Tests produces under contention), and the 24 GB box has a kernel-panic
# history under stacked load.
if pgrep -fl "(excise\.app/Contents/MacOS/Excise\.App|bin/(Debug|Release)/net10\.0/Excise\.App)( |$)" >/dev/null 2>&1; then
  echo "An Excise.App process is already running. Quit it first." >&2
  pgrep -fl "Excise\.App" >&2
  exit 1
fi
if pgrep -fl "testhost|vstest.console|dotnet test" >/dev/null 2>&1; then
  echo "A test host is running — its CPU and memory would move every number here." >&2
  echo "Wait for the run to finish, then retry." >&2
  exit 1
fi

LOAD1="$(sysctl -n vm.loadavg | awk '{print $2}')"
echo "==> load1 at start: $LOAD1"

# ------------------------------------------------------------------------- build

if [ "$BUILD" = "1" ]; then
  echo "==> building Release bundle from $(git rev-parse --short HEAD)"
  rm -rf "$BUNDLE_DIR"
  "$ROOT/scripts/build-macos-app.sh" --version "0.0.0-guiperf" --output "$BUNDLE_DIR" \
    || { echo "build failed" >&2; exit 1; }
fi

if [ ! -x "$EXE" ]; then
  echo "no app executable at $EXE (build, or pass --app)" >&2
  exit 1
fi

GIT_SHA="$(git rev-parse --short HEAD)"
GIT_DIRTY="clean"
git diff --quiet || GIT_DIRTY="dirty"

cleanup_bundle() {
  if [ "$KEEP_BUNDLE" = "0" ] && [ "$BUILD" = "1" ]; then
    rm -rf "$BUNDLE_DIR"
  fi
}
trap cleanup_bundle EXIT

# --------------------------------------------------------------------- sampling

cpusecs() {
  ps -p "$1" -o time= 2>/dev/null | awk -F'[:.]' '
    NF==3 { print $1*60+$2+$3/100; next }
    NF==4 { print $1*3600+$2*60+$3+$4/100; next }
    { print 0 }'
}

footprint_mb() {
  # `footprint` is the number #1461/#1496 are argued in; vmmap's "Physical
  # footprint" line is the fallback when footprint is unavailable.
  footprint -p "$1" 2>/dev/null \
    | awk '/Footprint:/{for(i=1;i<=NF;i++) if ($i=="Footprint:") {print $(i+1); exit}}'
}

rss_mb() {
  local kb; kb="$(ps -p "$1" -o rss= 2>/dev/null | tr -d ' ')"
  [ -n "$kb" ] && echo $(( kb / 1024 )) || echo ""
}

# One sample row. Written to samples.tsv, joined to steps.jsonl by seq.
sample_row() {
  local dir="$1" pid="$2" seq="$3" label="$4" kind="$5"
  local fp rss cpu
  fp="$(footprint_mb "$pid")"
  rss="$(rss_mb "$pid")"
  cpu="$(cpusecs "$pid")"
  if [ "$DO_VMMAP" = "1" ] && [ "$kind" = "boundary" ]; then
    vmmap --summary "$pid" > "$dir/vmmap-$seq-$label.txt" 2>/dev/null
    [ -n "$fp" ] || fp="$(awk '/^Physical footprint:/{print $3}' "$dir/vmmap-$seq-$label.txt" | tr -d 'M')"
  fi
  printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
    "$(date +%H:%M:%S)" "$seq" "$label" "$kind" "${rss:-}" "${fp:-}" "${cpu:-}" \
    "$(sysctl -n vm.loadavg | awk '{print $2}')" >> "$dir/samples.tsv"
}

# The other half of the runner's handshake (PerfStepJournal): watch step.marker,
# sample the quiescent process, then ack so the app proceeds. vmmap suspends the
# target, so it must only ever run inside this window — never mid-scroll.
ack_loop() {
  local dir="$1" pid="$2"
  local last="" periodic=0
  case "$SAMPLE_MODE" in 1s) periodic=1 ;; 5s) periodic=5 ;; esac
  local next_periodic=$SECONDS

  while ps -p "$pid" >/dev/null 2>&1; do
    if [ -f "$dir/step.marker" ]; then
      local seq; seq="$(cat "$dir/step.marker" 2>/dev/null | tr -d '[:space:]')"
      if [ -n "$seq" ] && [ "$seq" != "$last" ]; then
        local label
        label="$(python3 -c '
import json,sys
want=sys.argv[2]
try:
    for line in open(sys.argv[1]):
        try: o=json.loads(line)
        except Exception: continue
        if str(o.get("seq"))==want: print(o.get("step","?")); break
    else: print("?")
except FileNotFoundError: print("?")
' "$dir/steps.jsonl" "$seq" 2>/dev/null)"
        if [ "$SAMPLE_MODE" != "none" ]; then
          sample_row "$dir" "$pid" "$seq" "${label:-?}" boundary
        fi
        printf '%s' "$seq" > "$dir/step.ack.tmp" && mv "$dir/step.ack.tmp" "$dir/step.ack"
        last="$seq"
      fi
    fi

    if [ "$periodic" -gt 0 ] && [ "$SECONDS" -ge "$next_periodic" ]; then
      sample_row "$dir" "$pid" "${last:-0}" periodic periodic
      next_periodic=$(( SECONDS + periodic ))
    fi

    sleep 0.2
  done
}

# ------------------------------------------------------------------- one launch

# Returns 0 on a clean scenario run. $1 scenario id, $2 repeat, $3 run dir,
# $4 = "norunner" to launch WITHOUT the scenario runner (the runner-overhead
# control), in which case $5 is how many seconds to hold the app open.
launch_one() {
  local scenario="$1" repeat="$2" dir="$3" mode="${4:-runner}" hold="${5:-30}"
  mkdir -p "$dir"
  printf 'time\tseq\tstep\tkind\trssMB\tfootprintMB\tcpuSec\tload1\n' > "$dir/samples.tsv"

  if ! python3 "$ROOT/scripts/displaylink-preflight.py" > "$dir/preflight.txt" 2>&1 \
     && grep -q 'CGMainDisplayID=[1-9].*activeDisplays=0 ' "$dir/preflight.txt"; then
    # A display that is merely ASLEEP (a main display exists, none active) is
    # not the #18895 wedge (CGMainDisplayID=0). `-d` stops idle sleep but does
    # not wake a display that already slept, so assert user activity ONCE and
    # re-check. One wake, never a loop: if it is still down, it is a wedge.
    echo "    display asleep (activeDisplays=0); waking it once"
    caffeinate -u -t 2; sleep 3
  fi
  if ! python3 "$ROOT/scripts/displaylink-preflight.py" > "$dir/preflight.txt" 2>&1; then
    cat "$dir/preflight.txt"
    echo "ABORTING: -6661 display state. Do not retry in a loop; log out and back in." >&2
    return 66
  fi
  cat "$dir/preflight.txt"

  local -a env_args=("EXCISE_TRACE_VIEWER=$dir/metrics.jsonl")
  if [ "$mode" = "runner" ]; then
    env_args+=(
      "EXCISE_PERF_SCENARIO=$SCENARIO_FILE"
      "EXCISE_PERF_SCENARIO_ID=$scenario"
      "EXCISE_PERF_SCENARIO_OUT=$dir"
      "EXCISE_PERF_SCENARIO_REPEAT=$repeat"
    )
    [ "$SAMPLE_MODE" = "none" ] && env_args+=("EXCISE_PERF_SCENARIO_SAMPLE_MS=250")
  fi

  echo "==> launch $scenario rep=$repeat mode=$mode"
  env "${env_args[@]}" nohup "$EXE" > "$dir/app.log" 2>&1 &
  local pid=$!
  echo "$pid" > "$dir/pid"
  disown 2>/dev/null || true

  sample_row "$dir" "$pid" 0 launch boundary

  if [ "$mode" = "runner" ]; then
    ack_loop "$dir" "$pid" &
    local acker=$!
    # The runner quits the app itself; 20 min is its own internal ceiling.
    local deadline=$(( SECONDS + 1500 ))
    while ps -p "$pid" >/dev/null 2>&1 && [ "$SECONDS" -lt "$deadline" ]; do sleep 1; done
    kill "$acker" 2>/dev/null
    wait "$acker" 2>/dev/null
  else
    # The runner-overhead control: no scenario, so nothing quits the app. Hold
    # it for the same wall time, sample, then quit through the app menu via an
    # Apple Event — never SIGTERM, which skips the teardown being measured.
    local deadline=$(( SECONDS + hold ))
    while ps -p "$pid" >/dev/null 2>&1 && [ "$SECONDS" -lt "$deadline" ]; do sleep 1; done
    sample_row "$dir" "$pid" 999 held boundary
    osascript -e 'tell application id "cl.skpt.excise" to quit' >/dev/null 2>&1
    local q=$(( SECONDS + 20 ))
    while ps -p "$pid" >/dev/null 2>&1 && [ "$SECONDS" -lt "$q" ]; do sleep 1; done
  fi

  if ps -p "$pid" >/dev/null 2>&1; then
    echo "   app did not quit on its own; asking it to" >&2
    osascript -e 'tell application id "cl.skpt.excise" to quit' >/dev/null 2>&1
    sleep 5
  fi
  if ps -p "$pid" >/dev/null 2>&1; then
    echo "   LEFTOVER PROCESS $pid — recording and killing" >&2
    echo "leftover=$pid" >> "$dir/notes.txt"
    kill -9 "$pid" 2>/dev/null
    return 1
  fi

  if grep -q -- "-6661" "$dir/app.log" 2>/dev/null; then
    echo "ABORTING: -6661 in the app log." >&2
    return 66
  fi
  if [ -f "$dir/SCENARIO_ERROR.txt" ]; then
    echo "   scenario error:"; sed 's/^/     /' "$dir/SCENARIO_ERROR.txt"
    return 1
  fi
  return 0
}

# ---------------------------------------------------------------------- the run

echo "==> output: $OUT"
{
  echo "sha=$GIT_SHA"
  echo "tree=$GIT_DIRTY"
  echo "load1=$LOAD1"
  echo "sample=$SAMPLE_MODE"
  echo "vmmap=$DO_VMMAP"
  echo "repeats=$REPEATS"
  echo "scenarios=${SELECTED[*]}"
  echo "exe=$EXE"
  echo "started=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
} > "$OUT/run-meta.txt"

FAILED=0
ABORT=0

# $1 = space-separated scenario ids (bash 3.2 has no namerefs), $2 repeats,
# $3 subdirectory under $OUT.
run_matrix() {
  local ids="$1" repeats="$2" subdir="$3"
  local scenario rep dir rc
  for scenario in $ids; do
    for rep in $(seq 1 "$repeats"); do
      dir="$OUT/$subdir/${scenario}_r${rep}"
      launch_one "$scenario" "$rep" "$dir"
      rc=$?
      if [ "$rc" = "66" ]; then ABORT=1; return 66; fi
      [ "$rc" != "0" ] && FAILED=$(( FAILED + 1 ))
    done
  done
  return 0
}

if [ "$DO_CALIBRATE" = "1" ]; then
  echo ""
  echo "=== CALIBRATION (#1497) ==========================================="
  echo "Four measurements, each answering one question about whether this"
  echo "instrument's numbers mean anything:"
  echo "  1. runner overhead  — does having the harness in the process change it?"
  echo "  2. sampler overhead — does watching cost more than the thing watched?"
  echo "  3. driving fidelity — is view-model driving the same work as a user?"
  echo "  4. noise floor      — how much does the same thing move run to run?"
  echo "Measurements 1, 2 and 4 run here. 3 needs real keyboard input and is"
  echo "run separately; see the summary for the command."
  echo "==================================================================="

  echo ""
  echo "--- 1. runner overhead: null scenario, runner ON vs OFF, 5 each ---"
  echo "    Expect this to come out near zero: the runner is a static class"
  echo "    doing Task.Delay and a few counter reads. Near-zero is the CORRECT"
  echo "    result, not a failed measurement — it is what licenses trusting the"
  echo "    other three."
  for rep in 1 2 3 4 5; do
    launch_one null "$rep" "$OUT/calib/runner-on_r$rep" runner
    if [ $? = 66 ]; then ABORT=1; break; fi
  done
  if [ "$ABORT" = "0" ]; then
    # The control MUST be held for the same wall time as the runner-on runs.
    # cpuTotalMs is cumulative, so a 30 s hold against a ~12 s scenario measures
    # 18 s of extra idle and calls it "runner overhead". Take the median
    # totalMs the runner-on runs actually reported and hold to that.
    HOLD=$(python3 - "$OUT/calib" <<'PYEOF'
import glob, json, statistics, sys
vals = []
for f in glob.glob(sys.argv[1] + "/runner-on_r*/scenario-result.json"):
    try:
        vals.append(json.load(open(f))["totalMs"] / 1000.0)
    except Exception:
        pass
print(int(round(statistics.median(vals))) if vals else 30)
PYEOF
)
    echo "    holding the control for ${HOLD}s to match the runner-on median"
    for rep in 1 2 3 4 5; do
      launch_one null "$rep" "$OUT/calib/runner-off_r$rep" norunner "$HOLD"
      if [ $? = 66 ]; then ABORT=1; break; fi
    done
  fi

  if [ "$ABORT" = "0" ]; then
    echo ""
    echo "--- 2. sampler overhead: w9-launch-idle at none / 5s / 1s ---"
    for mode in none 5s 1s; do
      SAMPLE_MODE="$mode"
      for rep in 1 2 3; do
        launch_one w9-launch-idle "$rep" "$OUT/calib/sampler-${mode}_r$rep" runner
        if [ $? = 66 ]; then ABORT=1; break; fi
      done
      if [ "$ABORT" = "1" ]; then break; fi
    done
    SAMPLE_MODE="boundary"
  fi

  if [ "$ABORT" = "0" ]; then
    echo ""
    echo "--- 4. noise floor: every scenario x5 ---"
    run_matrix "${SELECTED[*]}" 5 noise
  fi
else
  run_matrix "${SELECTED[*]}" "$REPEATS" runs
fi

# ------------------------------------------------------------------- summarise

echo ""
echo "==> summarising"
python3 "$ROOT/scripts/summarize-gui-perf.py" \
  --run-dir "$OUT" \
  --scenario-file "$SCENARIO_FILE" \
  ${BASELINE:+--baseline "$BASELINE"} \
  || { echo "summariser failed" >&2; exit 1; }

echo ""
echo "Artifacts : $OUT"
echo "  run.json         machine-readable, one object per (scenario, repeat)"
echo "  summary.md       human table, with the floor beside every delta"
echo "  summary.tsv      the session.sh results.tsv column shape, extended"
[ "$DO_CALIBRATE" = "1" ] && echo "  calibration.json the four measurements above"

if [ "$ABORT" = "1" ]; then
  echo ""
  echo "RUN ABORTED on -6661. Numbers above are partial." >&2
  exit 66
fi
if [ "$FAILED" -gt 0 ]; then
  echo ""
  echo "$FAILED scenario run(s) reported a failure — see summary.md." >&2
  exit 1
fi
exit 0
