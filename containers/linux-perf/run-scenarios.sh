#!/usr/bin/env bash
# In-container scenario runner (#1720). Mirrors what
# scripts/run-gui-perf-scenarios.sh does on macOS, but samples through /proc.
#
# THE CONTRACT IT RELIES ON. The app is driven entirely by environment
# variables and, in runner mode, QUITS ITSELF when the scenario ends. So none
# of the macOS harness's osascript/Apple Event machinery is needed here; that
# path exists only for the no-scenario control.
#
# WHAT IS DIFFERENT FROM macOS, AND WHY THE NUMBERS ARE NOT COMPARABLE:
#   - RSS here is /proc VmRSS. macOS reports `footprint`, which excludes
#     purgeable and compressed pages. They measure different things.
#   - Different allocator, different kernel, no compositor.
# This is a Linux-over-time series. Comparing a row here to a macOS row is how
# a platform difference gets misread as a regression.
set -uo pipefail

APP="${APP:-/opt/excise/Excise.App}"
REPO="${REPO:-/repo}"
OUT="${OUT:-/out}"
SCENARIO="${SCENARIO:-altona-scroll}"
REPEATS="${REPEATS:-3}"
SAMPLE_MS="${SAMPLE_MS:-250}"

[ -x "$APP" ] || { echo "FAIL: no app at $APP (publish it on the host first)" >&2; exit 1; }
SCEN_FILE="$REPO/tests/gui-perf-scenarios.json"
[ -f "$SCEN_FILE" ] || { echo "FAIL: no scenario file at $SCEN_FILE" >&2; exit 1; }
python3 -c "
import json,sys
ids=[s['id'] for s in json.load(open('$SCEN_FILE'))['scenarios']]
sys.exit(0 if '$SCENARIO' in ids else 1)
" || { echo "FAIL: scenario '$SCENARIO' is not in $SCEN_FILE" >&2; exit 1; }

mkdir -p "$OUT"

# Resource limits are part of the measurement, so record what we actually got
# rather than what was requested. cgroup v2 first, v1 fallback, "unlimited"
# when neither answers — never a guess.
cpu_quota() {
  if [ -r /sys/fs/cgroup/cpu.max ]; then
    awk '{ if ($1=="max") print "unlimited"; else printf "%.2f", $1/$2 }' /sys/fs/cgroup/cpu.max
  else echo "unknown"; fi
}
mem_limit() {
  if [ -r /sys/fs/cgroup/memory.max ]; then
    awk '{ if ($1=="max") print "unlimited"; else printf "%.0f", $1/1048576 }' /sys/fs/cgroup/memory.max
  else echo "unknown"; fi
}

rss_mb()  { awk '/^VmRSS:/{printf "%.1f", $2/1024}' "/proc/$1/status" 2>/dev/null; }
# Anonymous resident is the closer analogue to macOS "footprint" than VmRSS is:
# it excludes file-backed pages (the mapped binary, fonts). Reported ALONGSIDE
# VmRSS, never instead of it, because neither is the same quantity macOS gives.
anon_mb() { awk '/^Anonymous:/{s+=$2} END{if(s)printf "%.1f", s/1024}' "/proc/$1/smaps_rollup" 2>/dev/null; }
cpu_sec() { awk '{print ($14+$15)/'"$(getconf CLK_TCK)"'}' "/proc/$1/stat" 2>/dev/null; }

echo "==> scenario=$SCENARIO repeats=$REPEATS cpus=$(cpu_quota) memLimitMB=$(mem_limit)"

for rep in $(seq 1 "$REPEATS"); do
  dir="$OUT/${SCENARIO}_rep${rep}"
  mkdir -p "$dir"
  # A per-launch HOME, so XDG config from one repeat cannot leak into the next.
  # On macOS this same isolation exists because a leaked window.json once made
  # Altona open on page 15 of 17 while every scroll reported success.
  app_home="$dir/home"; mkdir -p "$app_home"

  printf 'time\tseq\tstep\trssMB\tanonMB\tcpuSec\tload1\n' > "$dir/samples.tsv"

  # Xvfb is started EXPLICITLY rather than through xvfb-run, so that $! is the
  # app itself. xvfb-run is a shell wrapper whose own command line contains the
  # app path, so every pgrep heuristic matched the WRAPPER: the first attempt
  # here sampled it happily for 206 rows and reported a peak of 1.6 MB, which a
  # sample-count check passed. A count standing in for the property.
  disp=$(( 90 + rep ))
  Xvfb ":$disp" -screen 0 1440x900x24 -nolisten tcp > "$dir/xvfb.log" 2>&1 &
  xvfb_pid=$!
  for _ in $(seq 1 50); do [ -e "/tmp/.X11-unix/X$disp" ] && break; sleep 0.1; done
  if ! kill -0 "$xvfb_pid" 2>/dev/null; then
    echo "   FAIL rep$rep: Xvfb did not start; see $dir/xvfb.log" >&2
    continue
  fi

  env HOME="$app_home" \
      DISPLAY=":$disp" \
      XDG_CONFIG_HOME="$app_home/.config" \
      XDG_DATA_HOME="$app_home/.local/share" \
      XDG_CACHE_HOME="$app_home/.cache" \
      EXCISE_TRACE_VIEWER="$dir/metrics.jsonl" \
      EXCISE_PERF_SCENARIO="$SCEN_FILE" \
      EXCISE_PERF_SCENARIO_ID="$SCENARIO" \
      EXCISE_PERF_SCENARIO_OUT="$dir" \
      EXCISE_PERF_SCENARIO_REPEAT="$rep" \
      "$APP" > "$dir/app.log" 2>&1 &
  pid=$!
  echo "   rep$rep pid=$pid display=:$disp"

  # The boundary handshake, same protocol as the macOS harness: the app writes
  # a seq into step.marker and waits; we sample AT that boundary and ack the
  # same seq atomically. Without it every boundary reads "unsampled" and the
  # run measures only whatever the periodic timer happened to catch — the first
  # attempt scored boundariesSampled=0, boundariesUnsampled=8.
  (
    last=""
    while kill -0 "$pid" 2>/dev/null; do
      if [ -f "$dir/step.marker" ]; then
        sq="$(tr -d '[:space:]' < "$dir/step.marker" 2>/dev/null)"
        if [ -n "$sq" ] && [ "$sq" != "$last" ]; then
          label="$(python3 -c '
import json,sys
want=sys.argv[2]
try:
    for line in open(sys.argv[1]):
        try: o=json.loads(line)
        except Exception: continue
        if str(o.get("seq"))==want:
            print(o.get("step","?")); break
    else: print("?")
except FileNotFoundError: print("?")
' "$dir/steps.jsonl" "$sq" 2>/dev/null)"
          printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\n' \
            "$(date -u +%FT%TZ)" "$sq" "${label:-?}" "$(rss_mb "$pid")" \
            "$(anon_mb "$pid")" "$(cpu_sec "$pid")" \
            "$(awk '{print $1}' /proc/loadavg)" >> "$dir/samples.tsv"
          printf '%s' "$sq" > "$dir/step.ack.tmp" && mv "$dir/step.ack.tmp" "$dir/step.ack"
          last="$sq"
        fi
      fi
      sleep 0.1
    done
  ) &
  acker=$!

  seq_n=0
  while kill -0 "$pid" 2>/dev/null; do
    printf '%s\t%d\tsample\t%s\t%s\t%s\t%s\n' \
      "$(date -u +%FT%TZ)" "$seq_n" "$(rss_mb "$pid")" "$(anon_mb "$pid")" \
      "$(cpu_sec "$pid")" "$(awk '{print $1}' /proc/loadavg)" >> "$dir/samples.tsv"
    seq_n=$((seq_n+1))
    sleep "$(awk "BEGIN{print $SAMPLE_MS/1000}")"
  done
  wait "$pid" 2>/dev/null
  kill "$acker" 2>/dev/null; wait "$acker" 2>/dev/null
  kill "$xvfb_pid" 2>/dev/null; wait "$xvfb_pid" 2>/dev/null

  # A run that produced no samples is a FAILURE, not an empty success — the
  # same rule the test runner uses for a filter that matched zero tests.
  rows=$(( $(wc -l < "$dir/samples.tsv") - 1 ))
  peak="$(awk -F'\t' 'NR>1&&$4+0>m{m=$4+0}END{printf "%.1f", m}' "$dir/samples.tsv")"
  # A MAGNITUDE guard, not just a count. 206 samples of the xvfb-run wrapper at
  # 1.6 MB passed a count check and measured nothing. A GUI process that has
  # opened a 122 MB PDF cannot be under MIN_PLAUSIBLE_RSS_MB; if it is, we
  # sampled the wrong process and the run is void, not small.
  MIN_PLAUSIBLE_RSS_MB="${MIN_PLAUSIBLE_RSS_MB:-60}"
  if [ "$rows" -lt 2 ]; then
    echo "   FAIL rep$rep: only $rows samples; the app exited immediately. See $dir/app.log" >&2
  elif awk -v p="$peak" -v m="$MIN_PLAUSIBLE_RSS_MB" 'BEGIN{exit !(p<m)}'; then
    echo "   FAIL rep$rep: peak RSS ${peak} MB is below ${MIN_PLAUSIBLE_RSS_MB} MB — that is not the GUI process. Void, not small." >&2
  else
    sampled="$(python3 -c '
import json,sys
try: print(json.load(open(sys.argv[1])).get("boundariesSampled","?"))
except Exception: print("?")
' "$dir/scenario-result.json" 2>/dev/null)"
    echo "   rep$rep done: $rows samples, peak RSS ${peak} MB, boundariesSampled=$sampled"
    [ "$sampled" = "0" ] && echo "   WARN rep$rep: no boundary was acknowledged — the handshake did not work" >&2
  fi
done

python3 - "$OUT" "$SCENARIO" "$(cpu_quota)" "$(mem_limit)" <<'PYEOF'
import json, os, sys, glob, statistics
out, scenario, cpus, memlimit = sys.argv[1:5]
reps = []
for d in sorted(glob.glob(os.path.join(out, scenario + "_rep*"))):
    tsv = os.path.join(d, "samples.tsv")
    if not os.path.exists(tsv):
        continue
    rows = [l.rstrip("\n").split("\t") for l in open(tsv)][1:]
    rss  = [float(r[3]) for r in rows if len(r) > 3 and r[3]]
    anon = [float(r[4]) for r in rows if len(r) > 4 and r[4]]
    cpu  = [float(r[5]) for r in rows if len(r) > 5 and r[5]]
    if not rss:
        continue
    reps.append({"dir": os.path.basename(d), "samples": len(rows),
                 "finalRssMB": rss[-1], "peakRssMB": max(rss),
                 "finalAnonMB": anon[-1] if anon else None,
                 "peakAnonMB": max(anon) if anon else None,
                 "cpuTotalSec": max(cpu) if cpu else None})
doc = {"platform": "linux-container", "display": "xvfb (no compositor)",
       "scenario": scenario, "cpus": cpus, "memLimitMB": memlimit,
       "kernel": os.uname().release, "machine": os.uname().machine,
       "repeats": len(reps), "reps": reps,
       "note": "NOT comparable to macOS: /proc VmRSS is not macOS footprint, "
               "different allocator and kernel, and no compositor. "
               "Frame-pacing numbers from this display stack are meaningless."}
if len(reps) > 1:
    f = [r["finalRssMB"] for r in reps]
    doc["finalRssMB_median"] = statistics.median(f)
    doc["finalRssMB_spread"] = max(f) - min(f)
path = os.path.join(out, "run.json")
json.dump(doc, open(path, "w"), indent=2)
print("==> wrote " + path)
print(json.dumps({k: doc[k] for k in ("scenario","repeats","cpus","memLimitMB") }, indent=2))
for r in reps:
    print(f"    {r['dir']}: final {r['finalRssMB']} MB, peak {r['peakRssMB']} MB, {r['samples']} samples")
PYEOF
