#!/bin/bash
# ============================================================================
# run-long-tests.sh — run the slow / environment-dependent test suites that
# the PR CI gate skips (corpus, differential-vs-mutool, visual-baseline,
# benchmarks, and the full headless GUI suite), plus the standard suites for a
# complete record.
#
# Designed to be run in a SEPARATE terminal — it can take 20-40+ minutes.
# It writes a progress display to the terminal AND a timestamped log, and
# records a result line after EACH group so the log is useful even mid-run
# (or if a group hangs/crashes).
#
# Usage:
#   ./scripts/run-long-tests.sh                 # build once, run everything
#   ./scripts/run-long-tests.sh --no-build      # skip the initial build
#   ./scripts/run-long-tests.sh --only Visual,GUI   # run only matching groups
#   ./scripts/run-long-tests.sh --list          # list group names and exit
#
# Output:
#   logs/long-tests_<timestamp>.log             # master log (this transcript)
#   logs/long-tests_<timestamp>/<group>.log     # full per-group test output
#   logs/long-tests_latest.log                  # symlink to the master log
# ============================================================================

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
cd "$PROJECT_ROOT" || exit 1

# NOTE: deliberately NOT 'set -e' — a failing/timed-out group must not abort
# the rest of the run; we record its result and continue.

CONFIG="Debug"
DO_BUILD=1
ONLY=""
PER_TEST_TIMEOUT_MS=600000   # 10 min hang-timeout per test (blame-hang)

for arg in "$@"; do
    case "$arg" in
        --no-build) DO_BUILD=0 ;;
        --release)  CONFIG="Release" ;;
        --only) shift_next=1 ;;
        --only=*)   ONLY="${arg#*=}" ;;
        --list)     LIST_ONLY=1 ;;
        -h|--help)  sed -n '2,24p' "$0"; exit 0 ;;
        *) if [ "${shift_next:-0}" = "1" ]; then ONLY="$arg"; shift_next=0; fi ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$PROJECT_ROOT/logs"
GROUP_DIR="$LOG_DIR/long-tests_$TS"
LOG="$LOG_DIR/long-tests_$TS.log"
mkdir -p "$GROUP_DIR"
ln -sf "$(basename "$LOG")" "$LOG_DIR/long-tests_latest.log" 2>/dev/null

# Colors (terminal only)
if [ -t 1 ]; then R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else R=''; G=''; Y=''; B=''; N=''; fi

# say <msg>  -> terminal + master log
say() { echo -e "$1"; echo -e "$(echo -e "$1" | sed 's/\x1b\[[0-9;]*m//g')" >> "$LOG"; }

# ---- group definitions: "Name|project|filter|needs" ----
# needs: ""=none, xvfb=needs a display, mutool=needs mutool, tess=needs tesseract,
#        corpus=needs test-pdfs/
SUITES=(
  "Core|Excise.Core.Tests||"
  "Core-RealPdf|Excise.Core.Tests|FullyQualifiedName~RealPdf|FullyQualifiedName~CorpusConformance|corpus"
  "Cli|Excise.Cli.Tests||"
  "Ocr|Excise.Ocr.Tests||tess"
  "Render-Deterministic|Excise.Rendering.Tests|FullyQualifiedName!~Corpus&FullyQualifiedName!~Differential&FullyQualifiedName!~Visual&FullyQualifiedName!~Benchmark|"
  "Render-Visual|Excise.Rendering.Tests|FullyQualifiedName~Visual|"
  "Render-Benchmark|Excise.Rendering.Tests|FullyQualifiedName~Benchmark|"
  "Render-Differential|Excise.Rendering.Tests|FullyQualifiedName~Differential|mutool"
  "Render-Corpus|Excise.Rendering.Tests|FullyQualifiedName~Corpus|corpus"
  "GUI|Excise.App.Tests||xvfb"
)

if [ "${LIST_ONLY:-0}" = "1" ]; then
    echo "Groups:"; for g in "${SUITES[@]}"; do echo "  - ${g%%|*}"; done; exit 0
fi

# Detect optional dependencies once.
HAVE_MUTOOL=0; command -v mutool >/dev/null 2>&1 && HAVE_MUTOOL=1
HAVE_TESS=0;   command -v tesseract >/dev/null 2>&1 && HAVE_TESS=1
HAVE_XVFB=0;   command -v xvfb-run >/dev/null 2>&1 && HAVE_XVFB=1
HAVE_CORPUS=0; [ -d "$PROJECT_ROOT/test-pdfs" ] && HAVE_CORPUS=1

say "${B}=================================================${N}"
say "${B} excise long-running test runner${N}"
say "${B}=================================================${N}"
say "Started : $(date)"
say "Config  : $CONFIG"
say "Master log : $LOG"
say "Group logs : $GROUP_DIR/"
say "Deps    : mutool=$HAVE_MUTOOL tesseract=$HAVE_TESS xvfb-run=$HAVE_XVFB test-pdfs=$HAVE_CORPUS"
[ -n "$ONLY" ] && say "Filter  : only groups matching '$ONLY'"
say ""

if [ "$DO_BUILD" = "1" ]; then
    say "${B}[build]${N} dotnet build excise.sln -c $CONFIG ..."
    if dotnet build excise.sln -c "$CONFIG" > "$GROUP_DIR/_build.log" 2>&1; then
        say "${G}[build] OK${N}"
    else
        say "${R}[build] FAILED — see $GROUP_DIR/_build.log. Aborting.${N}"
        tail -15 "$GROUP_DIR/_build.log" | sed 's/^/    /' >> "$LOG"
        exit 1
    fi
    say ""
fi

# ---- run groups ----
TOTAL_SUITES=${#SUITES[@]}
idx=0
declare -a RESULTS
overall_rc=0
run_start=$(date +%s)

for spec in "${SUITES[@]}"; do
    idx=$((idx + 1))
    IFS='|' read -r name proj f1 f2 needs <<< "$spec"
    # Two filter fields are OR-combined into one --filter arg if f2 is set.
    filter="$f1"; [ -n "$f2" ] && filter="$f1|$f2"

    if [ -n "$ONLY" ] && ! echo ",$ONLY," | grep -qi ",$name,"; then
        continue
    fi

    # Dependency gating: skip (don't fail) when a prerequisite is absent.
    skip_reason=""
    case "$needs" in
        mutool) [ "$HAVE_MUTOOL" = "0" ] && skip_reason="mutool not installed" ;;
        tess)   [ "$HAVE_TESS"   = "0" ] && skip_reason="tesseract not installed" ;;
        corpus) [ "$HAVE_CORPUS" = "0" ] && skip_reason="test-pdfs/ missing (run scripts/download-test-pdfs.sh)" ;;
    esac

    glog="$GROUP_DIR/${name}.log"
    say "${B}[$idx/$TOTAL_SUITES] ${name}${N}  (proj=$proj${filter:+, filter=$filter})"

    if [ -n "$skip_reason" ]; then
        say "    ${Y}SKIPPED${N} — $skip_reason"
        RESULTS+=("$name|SKIP|$skip_reason")
        say ""
        continue
    fi

    runner=(dotnet test "$proj" -c "$CONFIG" --no-build
            --blame-hang-timeout "$PER_TEST_TIMEOUT_MS"
            --logger "console;verbosity=normal")
    [ -n "$filter" ] && runner+=(--filter "$filter")
    if [ "$needs" = "xvfb" ] && [ "$HAVE_XVFB" = "1" ]; then
        runner=(xvfb-run -a "${runner[@]}")
    fi

    g_start=$(date +%s)
    "${runner[@]}" > "$glog" 2>&1
    rc=$?
    g_dur=$(( $(date +%s) - g_start ))

    # Aggregate the per-assembly summary lines dotnet prints.
    summary=$(grep -E "Passed!|Failed!" "$glog" | tail -1)
    passed=$(grep -oE "Passed:[[:space:]]*[0-9]+" "$glog" | grep -oE "[0-9]+" | awk '{s+=$1} END{print s+0}')
    failed=$(grep -oE "Failed:[[:space:]]*[0-9]+" "$glog" | grep -oE "[0-9]+" | awk '{s+=$1} END{print s+0}')
    skipped=$(grep -oE "Skipped:[[:space:]]*[0-9]+" "$glog" | grep -oE "[0-9]+" | awk '{s+=$1} END{print s+0}')
    : "${passed:=?}"; : "${failed:=?}"; : "${skipped:=?}"

    if [ "$rc" = "0" ]; then
        say "    ${G}PASS${N}  passed=$passed skipped=$skipped  (${g_dur}s)  -> $glog"
        RESULTS+=("$name|PASS|p=$passed s=$skipped ${g_dur}s")
    else
        overall_rc=1
        # Show the failing test names inline so the master log is self-contained.
        fails=$(grep -oE "[A-Za-z0-9_.]+\.[A-Za-z0-9_]+ \[FAIL\]" "$glog" | sort -u)
        say "    ${R}FAIL${N}  passed=$passed failed=$failed skipped=$skipped  (rc=$rc, ${g_dur}s)  -> $glog"
        if [ -n "$fails" ]; then
            echo "$fails" | sed 's/^/        /' | tee -a "$LOG"
        fi
        RESULTS+=("$name|FAIL|p=$passed f=$failed rc=$rc ${g_dur}s")
    fi
    say ""
done

# ---- final summary ----
run_dur=$(( $(date +%s) - run_start ))
say "${B}=================================================${N}"
say "${B} SUMMARY${N}   (total ${run_dur}s)"
say "${B}=================================================${N}"
for r in "${RESULTS[@]}"; do
    IFS='|' read -r n status detail <<< "$r"
    case "$status" in
        PASS) say "  ${G}PASS${N}  $(printf '%-22s' "$n") $detail" ;;
        FAIL) say "  ${R}FAIL${N}  $(printf '%-22s' "$n") $detail" ;;
        SKIP) say "  ${Y}SKIP${N}  $(printf '%-22s' "$n") $detail" ;;
    esac
done
say ""
say "Finished: $(date)"
say "Master log: $LOG"
if [ "$overall_rc" = "0" ]; then
    say "${G}All executed groups passed.${N}"
else
    say "${R}One or more groups failed — see per-group logs in $GROUP_DIR/${N}"
fi
exit $overall_rc
