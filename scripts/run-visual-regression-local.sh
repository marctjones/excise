#!/usr/bin/env bash
# Run visual-regression checks locally.
#
# This replaces the old scheduled GitHub Actions visual-regression workflow.
# Keep these environment-sensitive tests off paid hosted runners; run them on a
# known developer machine when preparing a release candidate or investigating
# rendering changes.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
DO_BUILD=1
ONLY=""
PER_TEST_TIMEOUT_MS=600000

# Avalonia's build-time telemetry writes under the user's application support
# directory. Keep this local runner hermetic and workspace-safe.
export AVALONIA_TELEMETRY_OPTOUT=1

for arg in "$@"; do
    case "$arg" in
        --release) CONFIG="Release" ;;
        --no-build) DO_BUILD=0 ;;
        --only=*) ONLY="${arg#*=}" ;;
        --help|-h)
            sed -n '1,42p' "$0"
            exit 0
            ;;
        *)
            echo "Unknown argument: $arg" >&2
            exit 2
            ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/visual-regression_$TS"
mkdir -p "$LOG_DIR"
ln -sf "visual-regression_$TS" "$ROOT/logs/visual-regression_latest" 2>/dev/null

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; N=''
fi

say() { echo -e "$1"; }

HAVE_MUTOOL=0; command -v mutool >/dev/null 2>&1 && HAVE_MUTOOL=1
HAVE_PDFTOCAIRO=0; command -v pdftocairo >/dev/null 2>&1 && HAVE_PDFTOCAIRO=1
HAVE_GS=0; command -v gs >/dev/null 2>&1 && HAVE_GS=1
HAVE_XVFB=0; command -v xvfb-run >/dev/null 2>&1 && HAVE_XVFB=1
HAVE_SMOKE=0; [ -d "$ROOT/test-pdfs/smoke" ] && HAVE_SMOKE=1

say "${B}=================================================${N}"
say "${B} excise local visual-regression runner${N}"
say "${B}=================================================${N}"
say "Started : $(date)"
say "Config  : $CONFIG"
say "Logs    : $LOG_DIR"
say "Deps    : mutool=$HAVE_MUTOOL pdftocairo=$HAVE_PDFTOCAIRO gs=$HAVE_GS xvfb-run=$HAVE_XVFB smoke-corpus=$HAVE_SMOKE"
say ""

if [ "$DO_BUILD" = "1" ]; then
    say "${B}[build]${N} dotnet build excise.sln -c $CONFIG"
    if dotnet build excise.sln -c "$CONFIG" > "$LOG_DIR/build.log" 2>&1; then
        say "${G}[build] OK${N}"
    else
        say "${R}[build] FAILED${N} -> $LOG_DIR/build.log"
        exit 1
    fi
    say ""
fi

declare -a RESULTS
overall=0

run_group() {
    local name="$1"
    shift
    local log="$LOG_DIR/$name.log"

    if [ -n "$ONLY" ] && ! echo ",$ONLY," | grep -qi ",$name,"; then
        return
    fi

    say "${B}[$name]${N} $*"
    local start
    start="$(date +%s)"
    "$@" > "$log" 2>&1
    local rc=$?
    local dur=$(( $(date +%s) - start ))

    if [ "$rc" = "0" ]; then
        say "  ${G}PASS${N} (${dur}s) -> $log"
        RESULTS+=("$name|PASS|${dur}s")
    else
        say "  ${R}FAIL${N} rc=$rc (${dur}s) -> $log"
        grep -E "\\[FAIL\\]|Failed!|Error Message:|Assert\\.Fail|Expected .* but" "$log" | tail -40 | sed 's/^/    /'
        RESULTS+=("$name|FAIL|rc=$rc ${dur}s")
        overall=1
    fi
    say ""
}

run_group "render-visual-baselines" \
    dotnet test Excise.Rendering.Tests -c "$CONFIG" --no-build \
        --filter "FullyQualifiedName~Visual" \
        --blame-hang-timeout "$PER_TEST_TIMEOUT_MS" \
        --logger "console;verbosity=normal"

if [ "$HAVE_MUTOOL" = "1" ] && [ "$HAVE_SMOKE" = "1" ]; then
    run_group "render-differential-smoke" \
        dotnet test Excise.Rendering.Tests -c "$CONFIG" --no-build \
            --filter "FullyQualifiedName~DifferentialRenderingTests|FullyQualifiedName~TextExtractionDifferentialTests|FullyQualifiedName~RedactionRoundTripTests" \
            --blame-hang-timeout "$PER_TEST_TIMEOUT_MS" \
            --logger "console;verbosity=normal"
else
    say "${Y}[render-differential-smoke] SKIP${N} - needs mutool and test-pdfs/smoke."
    say "  Install mutool and run scripts/download-smoke-corpus.sh, then rerun this script."
    RESULTS+=("render-differential-smoke|SKIP|needs mutool and smoke corpus")
    say ""
fi

gui_cmd=(dotnet test Excise.App.Tests -c "$CONFIG" --no-build
    --filter "FullyQualifiedName~MatchesBaseline"
    --blame-hang-timeout "$PER_TEST_TIMEOUT_MS"
    --logger "console;verbosity=normal")
if [ "$HAVE_XVFB" = "1" ]; then
    gui_cmd=(xvfb-run -a "${gui_cmd[@]}")
fi
run_group "gui-headless-baselines" "${gui_cmd[@]}"

say "${B}Collecting PNG artifacts...${N}"
find "$ROOT" -type f \( -name "*-actual.png" -o -name "*-diff.png" -o -name "*triptych.png" \) \
    -path "*/bin/*" -exec cp {} "$LOG_DIR/" \; 2>/dev/null
say "Artifacts copied to $LOG_DIR"
say ""

say "${B}Summary${N}"
for r in "${RESULTS[@]}"; do
    IFS='|' read -r name status detail <<< "$r"
    case "$status" in
        PASS) say "  ${G}PASS${N} $name $detail" ;;
        FAIL) say "  ${R}FAIL${N} $name $detail" ;;
        SKIP) say "  ${Y}SKIP${N} $name $detail" ;;
    esac
done

exit "$overall"
