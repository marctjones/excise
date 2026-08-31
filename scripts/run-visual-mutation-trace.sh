#!/usr/bin/env bash
# Live visual-stability trace for page-mutation operations (#846 / #695 Phase 3).
#
# Drives a page-mutation (rotate / remove / move / zoom) in the REAL running app
# — where the compositor actually re-renders the continuous view after a document
# swap, unlike the headless test host — then a scroll sweep, a zoom, and a save,
# capturing a PNG per frame plus an ink-centroid trajectory. Prints a per-phase
# stability summary and the artifact locations.
#
# WHAT IT CATCHES / WHAT IT DOESN'T:
#   * save round-trip (does the mutation + save crash?) — reliable.
#   * zoom/settle stability (centroid constant after the action settles) — reliable.
#   * the scroll-sweep and the raw rotate shift are recorded as PNGs + numbers for
#     HUMAN review. Automatic "bounce detected" from the centroid is deliberately
#     NOT asserted: rotating a page makes it landscape (wider), so the ink centroid
#     legitimately moves — a numeric verdict there would be unreliable. Look at the
#     PNGs. The live-only reading-view scroll instability is tracked as #846.
#
# Usage:
#   scripts/run-visual-mutation-trace.sh [--action rotate-right] [--pdf PATH]
#                                        [--scroll mid|top] [--out DIR]
#                                        [--timeout SECONDS] [--no-build]
#                                        [--app PATH_TO_EXCISE_APP]
#
# Actions: rotate-right (default), rotate-left, rotate-180, remove-page,
#          move-later, zoom-in.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

ACTION="rotate-right"
PDF="$ROOT/test-pdfs/federal/scotus-trump-v-us.pdf"
SCROLL="mid"
OUT="$ROOT/logs/visual-trace_$(date +%Y%m%d_%H%M%S)"
TIMEOUT=120
BUILD=1
APP=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --action) ACTION="$2"; shift 2 ;;
    --pdf) PDF="$2"; shift 2 ;;
    --scroll) SCROLL="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --timeout) TIMEOUT="$2"; shift 2 ;;
    --no-build) BUILD=0; shift ;;
    --app) APP="$2"; BUILD=0; shift 2 ;;
    -h|--help) sed -n '2,30p' "$0"; exit 0 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [ ! -f "$PDF" ]; then
  echo "PDF not found: $PDF"
  echo "  (download a corpus first, e.g. scripts/download-federal-corpus.sh)"
  exit 1
fi

mkdir -p "$OUT"
echo "==> visual trace: action=$ACTION scroll=$SCROLL"
echo "    pdf=$PDF"
echo "    out=$OUT"

if [ -n "$APP" ]; then
  APP="$(cd "$(dirname "$APP")" 2>/dev/null && pwd)/$(basename "$APP")"
  APP_EXEC="$APP/Contents/MacOS/Excise.App"
  if [ ! -x "$APP_EXEC" ]; then
    echo "Packaged app executable not found: $APP_EXEC"
    exit 1
  fi
  echo "    app=$APP"
fi

if [ "$BUILD" = "1" ]; then
  echo "==> building Excise.App (Debug)"
  dotnet build Excise.App -c Debug --nologo -v q || { echo "build failed"; exit 1; }
fi

LOG="$OUT/app.log"
if [ -n "$APP" ]; then
  EXCISE_VISUAL_TRACE_OUT="$OUT" \
  EXCISE_VISUAL_TRACE_ACTION="$ACTION" \
  EXCISE_VISUAL_TRACE_SCROLL="$SCROLL" \
    nohup "$APP_EXEC" "$PDF" >"$LOG" 2>&1 &
else
  EXCISE_VISUAL_TRACE_OUT="$OUT" \
  EXCISE_VISUAL_TRACE_ACTION="$ACTION" \
  EXCISE_VISUAL_TRACE_SCROLL="$SCROLL" \
    nohup dotnet run --project Excise.App -c Debug --no-build -- "$PDF" >"$LOG" 2>&1 &
fi
APP_PID=$!

echo "==> app pid $APP_PID; waiting up to ${TIMEOUT}s for trajectory.csv"
for _ in $(seq 1 "$TIMEOUT"); do
  [ -f "$OUT/trajectory.csv" ] && break
  [ -f "$OUT/ERROR.txt" ] && break
  ps -p "$APP_PID" >/dev/null 2>&1 || break
  sleep 1
done
# The runner shuts the app down itself; make sure it's gone.
ps -p "$APP_PID" >/dev/null 2>&1 && kill "$APP_PID" 2>/dev/null

if [ -f "$OUT/ERROR.txt" ]; then
  echo "TRACE ERROR:"; cat "$OUT/ERROR.txt"; exit 1
fi
if [ ! -f "$OUT/trajectory.csv" ]; then
  echo "No trajectory.csv produced. Last app log lines:"; tail -20 "$LOG"; exit 1
fi

echo ""
echo "==> summary"
[ -f "$OUT/meta.txt" ] && sed 's/^/    /' "$OUT/meta.txt"
FRAMES=$(find "$OUT" -maxdepth 1 -name '*.png' | wc -l | tr -d ' ')
echo "    png frames: $FRAMES  (in $OUT)"

python3 - "$OUT/trajectory.csv" <<'PY'
import csv, sys
rows = list(csv.DictReader(open(sys.argv[1])))
def cy(r): return float(r["centroidY"])
phases = {}
for r in rows:
    phases.setdefault(r["phase"], []).append(r)

print("    per-phase centroidY stability (max inter-frame jump; small = stable):")
for ph in ("before","after","scroll","zoom","save"):
    fr = phases.get(ph)
    if not fr: continue
    ys = [cy(r) for r in fr]
    jumps = [abs(ys[i]-ys[i-1]) for i in range(1,len(ys))] or [0.0]
    tail = ys[-min(8,len(ys)):]
    tail_jump = max((abs(tail[i]-tail[i-1]) for i in range(1,len(tail))), default=0.0)
    note = ""
    if ph in ("zoom","save") and tail_jump > 3:
        note = "  <-- NOT settled (expected constant)"
    if ph == "after" and tail_jump > 3:
        note = "  <-- still moving at end of settle window"
    print(f"      {ph:<7} frames={len(fr):>2} maxJump={max(jumps):7.1f} tailJump={tail_jump:6.1f}{note}")
print("    (scroll-phase movement is expected; inspect the PNGs for visual bounce — #846.)")
PY

echo ""
echo "Artifacts: $OUT  (before_*.png, after_*.png, scroll_*.png, zoom_*.png, save_*.png, trajectory.csv)"
