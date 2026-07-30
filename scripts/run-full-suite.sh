#!/usr/bin/env bash
# run-full-suite.sh — the whole test suite, restartable, with a bounded
# memory footprint.
#
# WHY
# ---
# Two problems this solves at once:
#
#   1. A full run is long enough that something will interrupt it. On
#      2026-07-29 a kernel panic ("watchdog timeout: no checkins from
#      watchdogd in 91 seconds", 17 swapfiles, LOW swap space) killed five
#      concurrent sessions mid-run. Restarting a ~30-minute run from zero
#      after each such event means it never finishes.
#   2. Aggregate memory load, not any single test process, is what killed the
#      box: five Claude sessions plus concurrent `dotnet test` runs on a 24GB
#      machine with a 91%-full disk (macOS grows swap there). A single testhost
#      peaks at only ~450MB (Core) to ~700MB (a Rendering chunk) — MEASURED,
#      see below.
#
# So: every unit of work is checkpointed to disk the moment it passes, the big
# suites are split into chunks so a crash costs one chunk instead of 17
# minutes, each chunk runs in its own short-lived testhost, exactly one dotnet
# process runs at a time, and a guard refuses to start work on a machine that
# is already under memory pressure.
#
# NOTE: GC-flag tuning was tried and MEASURED NOT TO HELP (it made peak RSS
# ~24% worse). See runner_export_lean_env in scripts/lib-runner.sh for the
# numbers. Peak RSS is ~450MB-700MB per testhost; the aggregate load of many
# concurrent processes is what hurts, not any single test run.
#
# HOW TO RUN IT (in your own terminal, not through an agent — CLAUDE.md)
#
#   caffeinate -i scripts/run-full-suite.sh --resume 2>&1 | tee -a logs/full-suite.log
#
# Crashed? Re-run the exact same command. Passed steps are skipped, the
# interrupted one re-runs.
#
#   scripts/run-full-suite.sh --list          # show the plan, run nothing
#   scripts/run-full-suite.sh --status        # what's done / what's left
#   scripts/run-full-suite.sh --fresh         # discard checkpoints, start over
#   scripts/run-full-suite.sh --only core     # steps matching a pattern
#
# WHAT IS NOT RESUMABLE, BY DESIGN
# --------------------------------
# The redaction gates re-run on every invocation, even on resume. CLAUDE.md:
# "t1's redaction test suites run unconditionally and there is no flag to skip
# them." A checkpoint that skipped them would be that flag. See
# RUNNER_NEVER_CHECKPOINT in lib-runner.sh.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
source "$ROOT/scripts/lib-runner.sh"

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; D='\033[0;90m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; D=''; N=''
fi
say() { echo -e "$1"; }

CONFIG="Debug"
MODE="run"
ONLY=""
FRESH=0
# Test classes per chunk. Smaller = finer resume granularity and lower peak RSS
# per testhost, at ~2s of process-start overhead per extra chunk.
CHUNK_CLASSES="${CHUNK_CLASSES:-12}"
SKIP_CHUNKING=0

usage() {
    cat <<'EOF'
Usage: scripts/run-full-suite.sh [options]

  --resume           No-op: resuming is ALREADY the default. Accepted so the
                     documented command reads explicitly. --fresh is the opt-out.
  --fresh            Discard checkpoints for this commit and run everything.
  --list             Print the plan and exit (runs nothing).
  --status           Print per-step done/pending state and exit.
  --release          Build and test in Release (default: Debug).
  --only <pattern>   Only steps whose name matches this grep -E pattern.
  --no-chunking      One dotnet process per project instead of per class group.
  --chunk-size <n>   Test classes per chunk (default 12).

Environment (see scripts/lib-runner.sh for all):
  RUNNER_MIN_FREE_GIB=20    abort if the data volume drops below this
  RUNNER_MAX_PRESSURE=2     wait when kern.memorystatus_vm_pressure_level exceeds this
  RUNNER_HEAP_CAP_GIB=0     optional per-testhost GC heap cap (GiB); 0 = off
  RUNNER_TUNE_GC=0          optional GC tuning; measured NOT to help here
EOF
}

while [ $# -gt 0 ]; do
    case "$1" in
        --resume) MODE="run"; shift ;;
        --fresh) FRESH=1; shift ;;
        --list) MODE="list"; shift ;;
        --status) MODE="status"; shift ;;
        --release) CONFIG="Release"; shift ;;
        --only) ONLY="${2:-}"; shift 2 ;;
        --no-chunking) SKIP_CHUNKING=1; shift ;;
        --chunk-size) CHUNK_CLASSES="${2:-20}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) say "${R}Unknown option: $1${N}"; usage; exit 2 ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/full-suite_${CONFIG}_$TS"
mkdir -p "$LOG_DIR"

runner_state_init "full-suite" "$CONFIG"
[ "$FRESH" = "1" ] && runner_state_reset
runner_export_lean_env

CHUNK_DIR="$(runner_state_dir)/chunks"
mkdir -p "$CHUNK_DIR"

# Ctrl-C / SIGTERM must leave the state consistent. Markers are written
# synchronously per step, so there is nothing to flush — just report where to
# resume from and exit with a distinct code.
INTERRUPTED=0
on_signal() {
    INTERRUPTED=1
    say ""
    say "${Y}Interrupted.${N} Resume with the same command; completed steps are checkpointed."
    say "  State: $(runner_state_dir)"
    exit 130
}
trap on_signal INT TERM

# ---------------------------------------------------------------------------
# Plan
# ---------------------------------------------------------------------------
# The step list is DATA, not control flow. Four tab-separated columns:
#   name <TAB> kind <TAB> target <TAB> filter
# kind=script  target = command line,  filter = "-"
# kind=test    target = csproj/sln,    filter = --filter expression or "-"
#
# The literal "-" for "no filter" is not cosmetic: an EMPTY trailing field is
# stripped by `read` (tab is IFS whitespace), which previously made an absent
# filter indistinguishable from a present one and caused `cut -f2` to echo the
# whole line — passing the csproj path as --filter, matching zero tests, and
# exiting 0. A vacuous green. Always emit an explicit placeholder.
PLAN_FILE="$LOG_DIR/plan.tsv"

PROJECTS_SMALL="Excise.Avalonia.Tests Excise.Cli.Tests Excise.Ocr.Tests"
PROJECTS_BIG="Excise.Core.Tests Excise.Rendering.Tests Excise.App.Tests"

emit() { printf '%s\t%s\t%s\t%s\n' "$1" "$2" "$3" "${4:--}" >> "$PLAN_FILE"; }

# Enumerate test classes for a project and write a chunk plan. The enumeration
# is itself cached in the state dir — it costs a process start per project and
# survives a crash like everything else.
build_chunk_plan() {
    local proj="$1"
    local out="$CHUNK_DIR/$proj.chunks"

    if [ -s "$out" ]; then
        echo "$out"; return 0
    fi

    local listing="$CHUNK_DIR/$proj.tests.txt"
    if ! dotnet test "$proj/$proj.csproj" --no-build -c "$CONFIG" --list-tests \
            > "$listing" 2>"$CHUNK_DIR/$proj.list.err"; then
        say "  ${Y}--list-tests failed for $proj; falling back to one unchunked step${N}" >&2
        return 1
    fi

    # Lines are indented FQNs: Namespace.Class.Method(args). Strip the method
    # (and any parameter list) to get the class FQN, then unique them.
    sed -n 's/^[[:space:]]\{1,\}\([A-Za-z_][A-Za-z0-9_.]*\).*$/\1/p' "$listing" \
        | sed 's/\.[^.]*$//' \
        | sort -u \
        | grep -E '\.' > "$CHUNK_DIR/$proj.classes.txt" || true

    if [ ! -s "$CHUNK_DIR/$proj.classes.txt" ]; then
        return 1
    fi

    : > "$out"
    local i=0 n=0 filter=""
    while IFS= read -r cls; do
        [ -n "$cls" ] || continue
        if [ -z "$filter" ]; then
            filter="FullyQualifiedName~$cls"
        else
            filter="$filter|FullyQualifiedName~$cls"
        fi
        n=$(( n + 1 ))
        if [ "$n" -ge "$CHUNK_CLASSES" ]; then
            i=$(( i + 1 ))
            printf 'chunk%02d\t%s\n' "$i" "$filter" >> "$out"
            filter=""; n=0
        fi
    done < "$CHUNK_DIR/$proj.classes.txt"
    if [ -n "$filter" ]; then
        i=$(( i + 1 ))
        printf 'chunk%02d\t%s\n' "$i" "$filter" >> "$out"
    fi

    verify_chunk_coverage "$proj" "$listing" "$out" || return 1

    echo "$out"
}

# Chunking must never reduce coverage. A dropped class would just... not run,
# and the summary would still say PASS for every chunk — a silent cap presented
# as a complete run. Assert that every test the runner can see is matched by at
# least one chunk filter, and refuse the chunk plan otherwise (the caller then
# falls back to one unchunked step, which cannot drop anything).
verify_chunk_coverage() {
    local proj="$1" listing="$2" chunks="$3"
    python3 - "$listing" "$chunks" <<'PY'
import re, sys
listing, chunks = sys.argv[1], sys.argv[2]
tests = sorted({m.group(1) for m in
                (re.match(r'^\s+([A-Za-z_][A-Za-z0-9_.]*)', l) for l in open(listing, errors='replace'))
                if m})
terms = []
for line in open(chunks):
    _, filt = line.rstrip('\n').split('\t', 1)
    terms += [t.replace('FullyQualifiedName~', '') for t in filt.split('|')]
missing = [t for t in tests if not any(term in t for term in terms)]
if missing:
    print(f"CHUNK COVERAGE HOLE: {len(missing)} of {len(tests)} tests match no chunk", file=sys.stderr)
    for m in missing[:10]:
        print(f"  uncovered: {m}", file=sys.stderr)
    sys.exit(1)
# stdout is the captured return value of build_chunk_plan — anything printed
# there would be mistaken for the chunk-plan path. Report on stderr.
print(f"  chunk coverage verified: {len(tests)} test names, {len(terms)} classes, 0 uncovered",
      file=sys.stderr)
PY
}

: > "$PLAN_FILE"

# --- Phase 1: build once, then never again -------------------------------
emit "build" script "dotnet build excise.sln -c $CONFIG -m:1" "-"

# --- Phase 2: static / cheap gates ---------------------------------------
emit "doc-claims"           script "scripts/verify-doc-claims.sh" "-"
emit "gate-asymmetry"       script "scripts/check-gate-asymmetry.sh origin/develop" "-"
emit "redaction-architecture" script "scripts/verify-true-redaction.sh" "-"
emit "testdata-sync"        script "scripts/check-testdata-sync.sh" "-"
emit "skip-budget-selftest" script "scripts/test-check-skip-budget.sh" "-"

# --- Phase 3: the redaction gates (NEVER checkpointed — always re-run) ----
emit "redaction-suites" test "excise.sln" "FullyQualifiedName~Redaction"

# --- Phase 4: every test project ------------------------------------------
for proj in $PROJECTS_SMALL; do
    emit "$proj" test "$proj/$proj.csproj" "-"
done

for proj in $PROJECTS_BIG; do
    if [ "$SKIP_CHUNKING" = "1" ]; then
        emit "$proj" test "$proj/$proj.csproj" "-"
        continue
    fi
    # Do NOT swallow stderr here: the coverage guard and the --list-tests
    # fallback both report there, and a silent degrade to unchunked (or a
    # silent coverage hole) is the thing this script exists to avoid.
    chunks="$(build_chunk_plan "$proj" || true)"
    if [ -n "$chunks" ] && [ -s "$chunks" ]; then
        while IFS=$'\t' read -r cname cfilter; do
            emit "$proj.$cname" test "$proj/$proj.csproj" "$cfilter"
        done < "$chunks"
    else
        emit "$proj" test "$proj/$proj.csproj" "-"
    fi
done

# --- Phase 5: gates that need a FULL-project pass to be meaningful --------
# check-skip-budget.sh counts skip sites across a whole project. Feeding it a
# chunked run would give it wrong numbers, so it keeps its own unchunked run.
emit "skip-budget-core"      script "scripts/check-skip-budget.sh Excise.Core.Tests/Excise.Core.Tests.csproj" "-"
emit "skip-budget-rendering" script "scripts/check-skip-budget.sh Excise.Rendering.Tests/Excise.Rendering.Tests.csproj" "-"
emit "skip-budget-app"       script "scripts/check-skip-budget.sh Excise.App.Tests/Excise.App.Tests.csproj" "-"
emit "extraction-parity"     script "scripts/check-extraction-parity.sh" "-"
emit "copy-whitespace-parity" script "scripts/check-copy-whitespace-parity.sh" "-"

# --- Phase 6: unchunked App.Tests as the release evidence -----------------
# Chunking changes which tests share a process. That can hide OR manufacture
# cross-test contamination (a real one was found before: a shared window.json
# view-mode preference leaking between continuous-view tests, which only
# reproduced in a full-suite run). Chunks above give fast, resumable feedback;
# this single serial pass is what actually counts as evidence. CLAUDE.md:
# Excise.App.Tests is serial by design and must run alone.
emit "app-tests-unchunked-evidence" test "Excise.App.Tests/Excise.App.Tests.csproj" "-"

# ---------------------------------------------------------------------------
# Filter to --only
# ---------------------------------------------------------------------------
if [ -n "$ONLY" ]; then
    grep -E "^[^	]*$ONLY" "$PLAN_FILE" > "$PLAN_FILE.filtered" || true
    mv "$PLAN_FILE.filtered" "$PLAN_FILE"
fi

TOTAL="$(wc -l < "$PLAN_FILE" | tr -d ' ')"

# ---------------------------------------------------------------------------
# list / status modes
# ---------------------------------------------------------------------------
if [ "$MODE" = "list" ] || [ "$MODE" = "status" ]; then
    say "${B}Plan${N} ($TOTAL steps, config=$CONFIG)"
    say "State: $(runner_state_dir)"
    say ""
    ndone=0; npend=0
    while IFS=$'\t' read -r name kind target filter; do
        [ -n "$name" ] || continue
        if runner_is_never_checkpointed "$name"; then
            say "  ${Y}ALWAYS${N}  $name ${D}(never checkpointed)${N}"
            npend=$(( npend + 1 ))
        elif runner_step_should_run "$name"; then
            say "  ${D}pending${N} $name"
            npend=$(( npend + 1 ))
        else
            say "  ${G}done${N}    $name"
            ndone=$(( ndone + 1 ))
        fi
    done < "$PLAN_FILE"
    say ""
    say "done=$ndone pending=$npend total=$TOTAL"
    say "Resources: $(runner_resource_report)"
    exit 0
fi

# ---------------------------------------------------------------------------
# Run
# ---------------------------------------------------------------------------
say "${B}=================================================${N}"
say "${B} excise full suite — restartable${N}"
say "${B}=================================================${N}"
say "Started  : $(date)"
say "Config   : $CONFIG"
say "Steps    : $TOTAL"
say "Logs     : $LOG_DIR"
say "State    : $(runner_state_dir)"
say "Memory   : serial (1 testhost at a time) chunkSize=$CHUNK_CLASSES tuneGC=${RUNNER_TUNE_GC} heapCap=${RUNNER_HEAP_CAP_GIB}GiB"
say "Resources: $(runner_resource_report)"
say ""

OVERALL=0
RESULTS=()
IDX=0

run_one() {
    local name="$1" kind="$2" target="$3" filter="${4:--}"
    IDX=$(( IDX + 1 ))

    if ! runner_step_should_run "$name"; then
        say "${D}[$IDX/$TOTAL] $name — SKIP (checkpointed)${N}"
        RESULTS+=("$name|SKIP|checkpointed")
        return 0
    fi

    # Never start a step when the machine is already in trouble.
    runner_mem_guard "$name"

    local log="$LOG_DIR/$name.log"
    local start rc dur
    start="$(date +%s)"

    say "${B}[$IDX/$TOTAL] $name${N}"

    if [ "$kind" = "script" ]; then
        # shellcheck disable=SC2086
        eval $target > "$log" 2>&1
        rc=$?
    elif [ "$filter" = "-" ]; then
        dotnet test "$target" --no-build -c "$CONFIG" \
            --logger "console;verbosity=minimal" > "$log" 2>&1
        rc=$?
    else
        dotnet test "$target" --no-build -c "$CONFIG" \
            --filter "$filter" --logger "console;verbosity=minimal" > "$log" 2>&1
        rc=$?
    fi

    dur=$(( $(date +%s) - start ))

    # A test step that matched NOTHING exits 0. Checkpointing that is exactly
    # the failure mode this repo is built to prevent: a green that proves
    # nothing, then permanently skipped on every resume.
    #
    # The signal is ZERO EXECUTED TESTS, *not* the presence of "No test matches
    # the given testcase filter". A solution-wide filter (the redaction gate
    # runs against excise.sln) legitimately prints that line for every assembly
    # holding none of the targeted tests, while the others run thousands. The
    # first version of this guard grepped for the string and so reported the
    # redaction gate as FAIL on a run where it had actually passed 393 tests
    # across 5 assemblies — with only Excise.Avalonia.Tests, which contains no
    # Redaction tests, producing the line. Fail-safe direction, but it cried
    # wolf on the one gate that must never be ignored.
    if [ "$kind" = "test" ] && [ "$rc" = "0" ]; then
        local executed
        executed="$(grep -oE 'Total: *[0-9]+' "$log" 2>/dev/null | grep -oE '[0-9]+' \
                    | awk '{s+=$1} END {print s+0}')"
        if [ "${executed:-0}" = "0" ]; then
            executed="$(grep -oE 'Total tests: *[0-9]+' "$log" 2>/dev/null | grep -oE '[0-9]+' \
                        | awk '{s+=$1} END {print s+0}')"
        fi
        if [ "${executed:-0}" = "0" ]; then
            say "  ${R}FAIL${N} (${dur}s) — ZERO tests executed; refusing to checkpoint a vacuous pass"
            say "       filter: $filter"
            RESULTS+=("$name|FAIL|zero-tests-matched")
            OVERALL=1
            return 0
        fi
    fi

    if [ "$rc" = "0" ]; then
        runner_step_mark "$name" "$rc" "$dur"
        say "  ${G}PASS${N} (${dur}s)"
        RESULTS+=("$name|PASS|${dur}s")
    else
        say "  ${R}FAIL${N} rc=$rc (${dur}s) -> $log"
        tail -30 "$log" | sed 's/^/    /'
        RESULTS+=("$name|FAIL|rc=$rc ${dur}s")
        OVERALL=1
    fi

    # Release the build-server processes ONCE, right after the only step that
    # uses them. Every later step is --no-build, and MSBUILDDISABLENODEREUSE=1
    # already stops nodes persisting — so calling this per step would pay
    # process-teardown latency 49 more times for nothing.
    [ "$name" = "build" ] && runner_reclaim
    return 0
}

while IFS=$'\t' read -r name kind target filter; do
    [ -n "$name" ] || continue
    run_one "$name" "$kind" "$target" "$filter"
done < "$PLAN_FILE"

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
SUMMARY="$LOG_DIR/summary.tsv"
: > "$SUMMARY"
say ""
say "${B}=================================================${N}"
say "${B} Summary${N}"
say "${B}=================================================${N}"
npass=0; nfail=0; nskip=0
for r in "${RESULTS[@]:-}"; do
    [ -n "$r" ] || continue
    IFS='|' read -r nm st detail <<< "$r"
    printf '%s\t%s\t%s\n' "$nm" "$st" "$detail" >> "$SUMMARY"
    case "$st" in
        PASS) npass=$(( npass + 1 )); say "  ${G}PASS${N}  $nm ($detail)" ;;
        FAIL) nfail=$(( nfail + 1 )); say "  ${R}FAIL${N}  $nm ($detail)" ;;
        SKIP) nskip=$(( nskip + 1 )) ;;
    esac
done
say ""
say "pass=$npass fail=$nfail skipped-as-checkpointed=$nskip total=$TOTAL"
say "Resources: $(runner_resource_report)"
say "Logs    : $LOG_DIR"
say "Summary : $SUMMARY"
if [ "$OVERALL" != "0" ]; then
    say ""
    say "${Y}Re-run the same command to retry only the failed/pending steps.${N}"
fi
exit $OVERALL
