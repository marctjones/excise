#!/usr/bin/env bash
# Bundle-level cold-launch double-open harness (#1819, for #1629).
#
# For each delay: cold-launch a NEW excise instance on document A
# (`open -g -n -a <bundle> A`), wait the delay, hand document B to the running
# app (`open -g -a <bundle> B`), let it settle, then read back what the windows
# really show through the accessibility tree (scripts/cold-launch-ax-state.js):
# native title, in-window title, tab strip. Every instance is quit before the
# next run starts: its app menu, then a quit event addressed to the bundle path,
# then SIGTERM.
#
# `-g` asks Launch Services not to activate the app, and nothing here activates
# it, but excise can still take the foreground on its own when its first window
# shows (Avalonia.Native activates on Show; it did in 3 of 30 runs on 3.13.0).
# The harness samples the frontmost app during each run and records whether
# excise took it (the "focus" column). Expect focus steals while it runs.
#
# A run FAILS on #1629's shape: a tab named "Untitled", a window title that
# names a document not in that window's tabs, or either document missing from
# every window. Each run is also classified by WHERE B's request arrived,
# from excise's own log, so a green table says what it exercised:
#   queued  B arrived before the main window existed (activation queue)
#   pre     after the main window, before A's load started
#   inload  while A's load was in flight (between A's STEP 1 and STEP 13)
#   after   after A's load completed (plain sequential opens)
#
# Needs: macOS, an unlocked screen, Accessibility permission for the terminal,
# and no other GUI automation running. Writes evidence to logs/ (or --output).

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

APP="$ROOT/dist/excise.app"
DOC_A="$ROOT/test-pdfs/smoke/irs-w4.pdf"
DOC_B="$ROOT/test-pdfs/smoke/irs-1040.pdf"
DELAYS="0 0.5 1 2 5 10"
RUNS=5
SETTLE=5
LOAD_TIMEOUT=60
OPEN_BG=(-g)
OUT="$ROOT/logs/cold-launch-double-open_$(date +%Y%m%d_%H%M%S)"

usage() {
    cat <<'EOF'
Usage: scripts/run-cold-launch-double-open.sh [options]

  --app <path>       excise.app bundle. Default: dist/excise.app.
  --a <pdf>          First document (cold launch). Default: test-pdfs/smoke/irs-w4.pdf.
  --b <pdf>          Second document. Default: test-pdfs/smoke/irs-1040.pdf.
  --delays "<list>"  Seconds between the two opens. Default: "0 0.5 1 2 5 10".
  --runs <n>         Runs per delay. Default: 5.
  --settle <s>       Seconds to wait after the second open. Default: 5.
  --foreground       Plain `open` (no -g), exactly as #1629 was reported: the
                     app activates and its window becomes key. Takes focus.
  --output <dir>     Evidence directory. Default: logs/cold-launch-double-open_<ts>.

Exit status: 0 when every run keeps both documents with matching titles,
1 when any run shows #1629's shape, 2 on a harness error.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --app) APP="$2"; shift 2 ;;
        --a) DOC_A="$2"; shift 2 ;;
        --b) DOC_B="$2"; shift 2 ;;
        --delays) DELAYS="$2"; shift 2 ;;
        --runs) RUNS="$2"; shift 2 ;;
        --settle) SETTLE="$2"; shift 2 ;;
        --foreground) OPEN_BG=(); shift ;;
        --output) OUT="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown argument: $1" >&2; usage >&2; exit 2 ;;
    esac
done

if [ "$(uname -s)" != "Darwin" ]; then
    echo "macOS only" >&2
    exit 2
fi

APP="$(cd "$APP" && pwd)"
EXE="$APP/Contents/MacOS/Excise.App"
READER="$SCRIPT_DIR/cold-launch-ax-state.js"
CLASSIFIER="$SCRIPT_DIR/cold-launch-classify-run.py"
for f in "$EXE" "$DOC_A" "$DOC_B" "$READER" "$CLASSIFIER"; do
    [ -e "$f" ] || { echo "missing: $f" >&2; exit 2; }
done
mkdir -p "$OUT"
RESULTS="$OUT/results.tsv"
printf 'delay\trun\tverdict\tarrival\tprocesses\tfocus\tdetail\tstate\n' > "$RESULTS"

app_pids() { pgrep -f "^$EXE" || true; }

frontmost() {
    osascript -e 'tell application "System Events" to get name of every process whose frontmost is true' 2>/dev/null
}

quit_pid() {
    local pid="$1"
    osascript -e "tell application \"System Events\" to tell (first process whose unix id is $pid) to click menu item \"Quit\" of menu 1 of menu bar item \"Excise\" of menu bar 1" >/dev/null 2>&1
    local i
    for i in $(seq 1 10); do
        kill -0 "$pid" 2>/dev/null || return 0
        sleep 0.5
    done
    # A background app's menu is not always pressable. The quit Apple event,
    # addressed to this bundle by path (never by bundle id: a stale
    # /Applications/excise.app shares it), and sent only while the pid lives.
    if kill -0 "$pid" 2>/dev/null; then
        osascript -e "tell application \"$APP\" to quit" >/dev/null 2>&1
        for i in $(seq 1 10); do
            kill -0 "$pid" 2>/dev/null || return 0
            sleep 0.5
        done
    fi
    echo "  pid $pid did not quit from its menu or a quit event; SIGTERM" >&2
    kill -TERM "$pid" 2>/dev/null
    sleep 3
    kill -0 "$pid" 2>/dev/null && kill -KILL "$pid" 2>/dev/null
    return 0
}

quit_all() {
    local pid
    for pid in $(app_pids); do
        quit_pid "$pid"
    done
    local i
    for i in $(seq 1 20); do
        [ -z "$(app_pids)" ] && return 0
        sleep 0.5
    done
    echo "instances still running after quit: $(app_pids)" >&2
    return 1
}

# Loads that have finished, either way: completed or failed.
steps_done() { grep -c -e 'STEP 13: LoadDocumentAsync COMPLETE' -e '!!! ERROR in LoadDocumentAsync' "$1" 2>/dev/null || true; }

if [ -n "$(app_pids)" ]; then
    echo "an instance of $APP is already running; quit it first" >&2
    exit 2
fi

A_NAME="$(basename "$DOC_A")"
B_NAME="$(basename "$DOC_B")"
overall=0

for delay in $DELAYS; do
    for run in $(seq 1 "$RUNS"); do
        dir="$OUT/d${delay}_r${run}"
        mkdir -p "$dir"
        focus_samples=""
        focus_samples+="$(frontmost);"

        open ${OPEN_BG[@]+"${OPEN_BG[@]}"} -n -a "$APP" --stdout "$dir/app.log" --stderr "$dir/app.err" "$DOC_A"
        sleep "$delay"
        focus_samples+="$(frontmost);"
        open ${OPEN_BG[@]+"${OPEN_BG[@]}"} -a "$APP" --stdout "$dir/second.log" --stderr "$dir/second.err" "$DOC_B"
        sleep "$SETTLE"
        focus_samples+="$(frontmost);"

        waited=0
        while [ "$(steps_done "$dir/app.log")" -lt 2 ] && [ "$waited" -lt "$LOAD_TIMEOUT" ]; do
            sleep 1
            waited=$((waited + 1))
        done

        pids="$(app_pids | tr '\n' ' ')"
        : > "$dir/state.txt"
        for pid in $pids; do
            printf 'PID %s\n' "$pid" >> "$dir/state.txt"
            osascript -l JavaScript "$READER" "$pid" >> "$dir/state.txt" 2>&1
        done
        focus_samples+="$(frontmost);"

        quit_all || { echo "harness error: could not quit" >&2; exit 2; }
        focus_samples+="$(frontmost)"
        printf '%s\n' "$focus_samples" > "$dir/focus.txt"

        printf '%s\n' "$pids" > "$dir/pids.txt"
        line="$(python3 "$CLASSIFIER" "$dir" "$A_NAME" "$B_NAME" "$delay" "$run" "$pids")"
        printf '%s\n' "$line" >> "$RESULTS"
        printf '%s\n' "$line"
        case "$line" in
            *$'\tFAIL\t'*) overall=1 ;;
        esac
        sleep 1
    done
done

echo "results: $RESULTS"
exit "$overall"
