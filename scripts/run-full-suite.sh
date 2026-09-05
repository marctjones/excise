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
#      machine with a 91%-full disk (macOS grows swap there). Footprint varies
#      hugely by project: Excise.Core.Tests stays under 450MB, but
#      Excise.App.Tests peaks at ~8.5GB in one process (#861) — measured by the
#      per-step instrumentation below.
#
# So: every unit of work is checkpointed to disk the moment it passes, the big
# suites are split into chunks so a crash costs one chunk instead of 17
# minutes, each chunk runs in its own short-lived testhost, exactly one dotnet
# process runs at a time, and a guard refuses to start work on a machine that
# is already under memory pressure.
#
# NOTE: GC-flag tuning was tried and MEASURED NOT TO HELP (it made peak RSS
# ~24% worse). See runner_export_lean_env in scripts/lib-runner.sh for the
# numbers. Peak RSS ranges from <450MB (Core) to ~8.5GB (App.Tests, #861), so
# both a single heavy test run AND aggregate concurrent load can hurt.
#
# HOW TO RUN IT (in your own terminal, not through an agent — CLAUDE.md)
#
#   scripts/test-tier.sh full 2>&1 | tee -a logs/full-suite.log
#   (= caffeinate -i scripts/run-full-suite.sh; resuming is the default)
#
# Crashed? Re-run the exact same command. Passed steps are skipped, the
# interrupted one re-runs.
#
#   scripts/run-full-suite.sh --list          # show the plan, run nothing
#   scripts/run-full-suite.sh --status        # what's done / what's left
#   scripts/run-full-suite.sh --fresh         # discard checkpoints, start over
#   scripts/run-full-suite.sh --only core     # steps matching a pattern
#
# WHERE THE PLAN COMES FROM
# -------------------------
# tests/gates.tsv (LOCAL_GATES.md). This script holds no step list: the rows
# selected for tier "full" (chain: every t0 and t1 row plus the full-only
# ones — the corpus scans, the release-smoke rows and the GRADE benches) are
# read through runner_manifest_plan, project-chunked rows are expanded through
# build_chunk_plan below, and {TRXARGS:…} references become the chunk union.
# The run ends with scripts/report-gates.sh, whose exit code is this script's.
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

# --suite <name>: a name for a slice of tier full, expanded to an --only pattern
# over manifest row names. Deliberately patterns, not step lists: a row added to
# tests/gates.tsv as redaction-<something> joins the redaction suite with nothing
# to update here, which is the property the manifest exists to protect. Keep the
# patterns anchored on the naming convention, never on individual row names.
suite_pattern() {
    case "$1" in
        redaction)  echo 'redaction-|extraction-parity' ;;
        rendering)  echo 'rendering-|Excise\.Rendering\.Tests|render-quality|image-conformance|annotation-bench|reference-performance|corpus-scan-' ;;
        benches)    echo 'redaction-bench|reference-performance|annotation-bench|image-conformance|bench-design-coverage' ;;
        suites)     echo 'redaction-suites|rendering-oracles|Excise\..*\.Tests|app-tests-unchunked-evidence' ;;
        gates)      echo '' ;;   # everything: tier full unfiltered
        *)          return 1 ;;
    esac
}

list_suites() {
    cat <<'SUITES'
--suite names (each is a pattern over tests/gates.tsv row names, expanded at plan time):

  redaction   every redaction gate and bench, plus extraction parity
              (redaction completeness is bounded by extraction coverage)
  rendering   every rendering suite, corpus scan, quality scan and rendering bench
  benches     the GRADE rows only — the numbers vs the reference tools, no gates
  suites      the test projects and oracle suites only — no benches, no scans
  gates       everything; identical to tier full with no filter

  scripts/run-full-suite.sh --suite redaction
  scripts/run-full-suite.sh --list-suites
SUITES
}
FRESH=0
# Test classes per chunk. Smaller = finer resume granularity and lower peak RSS
# per testhost, at ~2s of process-start overhead per extra chunk.
CHUNK_CLASSES="${CHUNK_CLASSES:-12}"
SKIP_CHUNKING=0
EVERYTHING=0
ALLOW_MISSING_CORPORA=0

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
  --everything       Accepted as a no-op: full IS everything. The release-smoke
                     rows, the four-corpus rendering scan (#862) and the GRADE
                     benches are in tier "full" of tests/gates.tsv. The scans
                     REQUIRE all four corpora, or see --allow-missing-corpora.
  --allow-missing-corpora
                     Don't ABORT the run at preflight when a corpus is missing
                     or empty — run the rest of the suite and let those scans
                     fail as steps. It does NOT drop them from the plan: a run
                     with a missing corpus can never report green, and can
                     never satisfy --assert-green (#958, #994).
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
        --suite)
            SUITE="${2:-}"
            if ! ONLY="$(suite_pattern "$SUITE")"; then
                echo "unknown --suite '$SUITE'" >&2; list_suites >&2; exit 2
            fi
            shift 2 ;;
        --suite=*)
            SUITE="${1#*=}"
            if ! ONLY="$(suite_pattern "$SUITE")"; then
                echo "unknown --suite '$SUITE'" >&2; list_suites >&2; exit 2
            fi
            shift ;;
        --list-suites) list_suites; exit 0 ;;
        --everything) EVERYTHING=1; shift ;;
        --allow-missing-corpora) ALLOW_MISSING_CORPORA=1; shift ;;
        --no-chunking) SKIP_CHUNKING=1; shift ;;
        --chunk-size) CHUNK_CLASSES="${2:-20}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) say "${R}Unknown option: $1${N}"; usage; exit 2 ;;
    esac
done

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/full-suite_${CONFIG}_$TS"
# --list / --status derive the same plan but must leave no run directory (an
# empty ledger under logs/ used to read as a run that happened).
[ "$MODE" = "run" ] || LOG_DIR="$(mktemp -d)"
mkdir -p "$LOG_DIR"

# BSD (macOS) time uses -l; GNU time uses -v.  Do not capability-probe
# through a redirected `time` invocation: in this shell it can return a
# false negative and select GNU's -v on macOS, making every measured step
# fail before its command starts.
if [ "$(uname -s)" = "Darwin" ]; then TIME_FLAG="-l"; else TIME_FLAG="-v"; fi
RUSAGE_TSV="$LOG_DIR/resources.tsv"
: > "$RUSAGE_TSV"

runner_state_init "full-suite" "$CONFIG"
# One JSON object per step, including steps skipped as already-checkpointed
# (#994). Every row carries the manifest's class and knownIssue, so the report
# (scripts/report-gates.sh) can separate a NEW red from a KNOWN one; the
# markers remain the enforcement channel for resume.
[ "$MODE" = "run" ] && runner_ledger_init "$LOG_DIR/ledger.jsonl"
[ "$FRESH" = "1" ] && runner_state_reset
runner_export_lean_env

# The environment every row may reference (tests/gates.tsv "target").
RUNNER_BUILD_ARGS="-m:1"          # #861: one MSBuild node
RUNNER_OPTS="aot"                 # opt:aot rows run in full
BLAME_HANG_TIMEOUT="${BLAME_HANG_TIMEOUT:-900000}"
export CONFIG LOG_DIR RUNNER_BUILD_ARGS RUNNER_OPTS BLAME_HANG_TIMEOUT
runner_export_oracle_env
runner_export_release_env
GATE_ASYMMETRY_BASE="$(runner_gate_asymmetry_base full)"
export GATE_ASYMMETRY_BASE

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
# The step list is DATA, not control flow, and it lives in tests/gates.tsv.
# The plan written here has one header line and ten tab-separated columns:
#   name kind target filter class knownIssue prereq prereqPolicy checkpoint ratchet
# The literal "-" for an empty cell is not cosmetic: an EMPTY trailing field is
# stripped by `read` (tab is IFS whitespace), which once made an absent filter
# indistinguishable from a present one — the csproj path became the --filter,
# matched zero tests, and exited 0. A vacuous green.
PLAN_FILE="$LOG_DIR/plan.tsv"

# Fingerprint of the built test assembly. If this has not changed, the set of
# test names cannot have changed either, so a cached chunk plan is still valid.
# Cheaper than re-running --list-tests, and unlike a bare file-exists check it
# cannot go stale.
chunk_plan_fingerprint() {
    local proj="$1"
    local dll="$proj/bin/$CONFIG/net10.0/$proj.dll"
    [ -f "$dll" ] || { echo "no-dll"; return 0; }
    # size+mtime is enough and costs nothing on a large assembly.
    stat -f '%z:%m' "$dll" 2>/dev/null || stat -c '%s:%Y' "$dll" 2>/dev/null || echo "no-stat"
}

# Enumerate test classes for a project and write a chunk plan. The enumeration
# is cached in the state dir — it costs a process start per project and
# survives a crash like everything else.
#
# ⚠️ THE CACHE IS KEYED ON THE TEST ASSEMBLY, NOT ON THE STATE DIR EXISTING.
#
# It used to short-circuit on `[ -s "$out" ]` alone. The state dir is keyed by
# LABEL_CONFIG_BRANCH (see runner_state_dir), NOT by commit — so
# full-suite_Debug_develop_-dirty is reused across every commit on the branch,
# and a plan built once was reused forever. Worse, the early return skipped
# verify_chunk_coverage, the one thing that would have noticed.
#
# Measured cost of that (#1362): the 2026-08-31 run reused a plan built
# 2026-08-16. Three classes added in between — RawSampleImageDecoderTests,
# ReferenceProcessResourcesTests, RenderResourceScopeTests — appeared in ZERO
# chunk. 289 of 2,357 discovered tests never ran, and every chunk still exited
# 0, because a class no filter names is not skipped or reported, it simply is
# not addressed. Only the downstream test-count check noticed.
build_chunk_plan() {
    local proj="$1"
    local out="$CHUNK_DIR/$proj.chunks"
    local fp_file="$out.fingerprint"
    local fp
    fp="$(chunk_plan_fingerprint "$proj")"

    if [ -s "$out" ] && [ -f "$fp_file" ] && [ "$(cat "$fp_file")" = "$fp" ]; then
        # Re-verify even on a cache hit. The fingerprint should make this
        # redundant; it is cheap, and this is the check whose absence cost 289
        # tests. Belt and braces on the gate that failed silently.
        if verify_chunk_coverage "$proj" "$CHUNK_DIR/$proj.tests.txt" "$out"; then
            echo "$out"; return 0
        fi
        say "  ${Y}cached chunk plan for $proj failed re-verification; rebuilding${N}" >&2
    elif [ -s "$out" ]; then
        say "  ${Y}$proj test assembly changed since its chunk plan; rebuilding${N}" >&2
    fi
    rm -f "$out" "$fp_file"

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
    printf '%s' "$fp" > "$fp_file"

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

# ---------------------------------------------------------------------------
# Plan — derived from tests/gates.tsv
# ---------------------------------------------------------------------------
CORPUS_COVERAGE_ROWS=()
row10() { printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$@"; }

runner_manifest_plan full > "$PLAN_FILE.manifest" || { say "${R}tests/gates.tsv is defective; nothing ran.${N}"; exit 2; }
: > "$PLAN_FILE.rows"
while IFS=$'\t' read -r name kind target filter class known prereq policy ckpt ratchet; do
    [ -n "$name" ] || continue
    if [ "$kind" = "project-chunked" ] && [ "$SKIP_CHUNKING" != "1" ]; then
        proj="$(basename "$target" .csproj)"
        # Do NOT swallow stderr here: the coverage guard and the --list-tests
        # fallback both report there, and a silent degrade to unchunked (or a
        # silent coverage hole) is the thing this script exists to avoid.
        chunks="$(build_chunk_plan "$proj" || true)"
        if [ -n "$chunks" ] && [ -s "$chunks" ]; then
            # Chunk rows inherit every column; {TRXARGS:name} consumers get the
            # union of their trx files (runner_plan_expand_trx).
            while IFS=$'\t' read -r cname cfilter; do
                row10 "$name.$cname" test "$target" "$cfilter" "$class" "$known" "$prereq" "$policy" "$ckpt" "$ratchet"
            done < "$chunks" >> "$PLAN_FILE.rows"
            continue
        fi
    fi
    row10 "$name" "$kind" "$target" "$filter" "$class" "$known" "$prereq" "$policy" "$ckpt" "$ratchet" >> "$PLAN_FILE.rows"
done < "$PLAN_FILE.manifest"

# "of" counts the rows AFTER chunk expansion and BEFORE --only, so
# planned<of in the plan header means exactly one thing: a partial run.
OF="$(grep -c . "$PLAN_FILE.rows")"

# ---------------------------------------------------------------------------
# Filter to --only
# ---------------------------------------------------------------------------
if [ -n "$ONLY" ]; then
    grep -E "^[^	]*$ONLY" "$PLAN_FILE.rows" > "$PLAN_FILE.only" || true
    mv "$PLAN_FILE.only" "$PLAN_FILE.rows"
fi
PLANNED="$(grep -c . "$PLAN_FILE.rows")"
runner_plan_write "$PLAN_FILE" full "$PLAN_FILE.rows" "$PLANNED" "$OF" "${ONLY:--}"
rm -f "$PLAN_FILE.rows" "$PLAN_FILE.manifest"
# LAST: after --only and chunking, so {TRXARGS?:x} can see that x was filtered out.
runner_plan_expand_trx "$PLAN_FILE" "$LOG_DIR" || exit 2
TOTAL="$PLANNED"

# --- Preflight: refuse a silently partial corpus sweep (#958) -------------
# Derived from the PLANNED corpus-scan-* rows (their target names the corpus
# dir and the expectation manifest), so a scan added to tests/gates.tsv is
# preflighted without a second list. "Pages expected" is the manifest's
# non-comment line count; "pages present" counts *.pdf files in the corpus
# dir, which is what --page-mode first turns into pages scanned.
_missing_corpora=""
while IFS=$'\t' read -r name kind target _rest; do
    case "$name" in corpus-scan-*) ;; *) continue ;; esac
    _cs_dir="$(printf '%s' "$target" | sed -n 's/.*--corpus \([^ ]*\).*/\1/p')"
    _cs_manifest="$(printf '%s' "$target" | sed -n 's/.*--expectation-manifest \([^ ]*\).*/\1/p')"
    [ -n "$_cs_dir" ] || continue
    _cs_present=0
    [ -d "$ROOT/$_cs_dir" ] && _cs_present="$(find "$ROOT/$_cs_dir" -name '*.pdf' 2>/dev/null | wc -l | tr -d ' ')"
    _cs_expected=0
    [ -f "$ROOT/$_cs_manifest" ] && _cs_expected="$(grep -vc '^#' "$ROOT/$_cs_manifest" 2>/dev/null | tr -d ' ')"
    CORPUS_COVERAGE_ROWS+=("$_cs_dir	${_cs_present:-0}	${_cs_expected:-0}")
    if [ "${_cs_present:-0}" = "0" ]; then
        _dl_cmd="scripts/download-test-pdfs.sh"
        case "$name" in
            corpus-scan-pdfjs)  _dl_cmd="scripts/download-pdfjs-corpus.sh" ;;
            corpus-scan-pdfium) _dl_cmd="scripts/download-pdfium-corpus.sh" ;;
        esac
        _missing_corpora="${_missing_corpora}  $(printf '%-24s' "$_cs_dir") (0/${_cs_expected:-0} pages)  ->  $_dl_cmd\n"
    fi
done < "$PLAN_FILE"

CORPUS_RAN_WITH_ALLOW_MISSING=0
if [ -n "$_missing_corpora" ] && [ "$MODE" = "run" ]; then
    if [ "$ALLOW_MISSING_CORPORA" = "1" ]; then
        CORPUS_RAN_WITH_ALLOW_MISSING=1
        say "${Y}WARNING: the corpus sweep is PARTIAL (#958) — missing/empty corpora:${N}"
        printf "%b" "$_missing_corpora"
        say "${Y}Continuing because --allow-missing-corpora was passed. The scans stay${N}"
        say "${Y}in the plan and will FAIL — the flag lets the rest of the suite run,${N}"
        say "${Y}it does not remove them from the evidence.${N}"
        say ""
    else
        say "${R}ABORT: the corpus sweep would be silently partial (#958).${N}"
        say "The following corpora are empty or missing:"
        printf "%b" "$_missing_corpora"
        say ""
        say "  Download the missing corpora above, then re-run. Or pass"
        say "  ${B}--allow-missing-corpora${N} to run the rest of the suite anyway; the"
        say "  missing scans then fail as steps, so the run cannot report green."
        exit 1
    fi
fi

# ---------------------------------------------------------------------------
# list / status modes
# ---------------------------------------------------------------------------
if [ "$MODE" = "list" ] || [ "$MODE" = "status" ]; then
    say "${B}Plan${N} ($TOTAL of $OF rows, config=$CONFIG, tests/gates.tsv $(runner_manifest_fingerprint))"
    say "State: $(runner_state_dir)"
    say ""
    ndone=0; npend=0
    while IFS=$'\t' read -r name kind target filter class known prereq policy ckpt ratchet; do
        case "$name" in ''|'#'*) continue ;; esac
        if runner_is_never_checkpointed "$name"; then
            say "  ${Y}ALWAYS${N}  $(printf '%-8s' "$class") $name ${D}(never checkpointed)${N}"
            npend=$(( npend + 1 ))
        elif runner_step_should_run "$name" "$(runner_target_hash "$kind" "$target" "$filter")"; then
            say "  ${D}pending${N} $(printf '%-8s' "$class") $name"
            npend=$(( npend + 1 ))
        else
            say "  ${G}done${N}    $(printf '%-8s' "$class") $name"
            ndone=$(( ndone + 1 ))
        fi
    done < "$PLAN_FILE"
    say ""
    say "done=$ndone pending=$npend total=$TOTAL"
    say "Resources: $(runner_resource_report)"
    rm -rf "$LOG_DIR"
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
say "Steps    : $TOTAL of $OF (tests/gates.tsv $(runner_manifest_fingerprint))"
say "Tree     : @${RUNNER_SHA:0:12}$([ "$RUNNER_TREE_DIRTY" = yes ] && echo ' DIRTY')  gate-asymmetry base=$GATE_ASYMMETRY_BASE"
say "Logs     : $LOG_DIR"
say "State    : $(runner_state_dir)"
say "Memory   : serial (1 testhost at a time) chunkSize=$CHUNK_CLASSES tuneGC=${RUNNER_TUNE_GC} heapCap=${RUNNER_HEAP_CAP_GIB}GiB"
say "Resources: $(runner_resource_report)"
say ""

OVERALL=0
IDX=0

# ---------------------------------------------------------------------------
# Per-step resource measurement
# ---------------------------------------------------------------------------
# Runs a step under time(1) and records peak RSS + CPU seconds alongside wall
# time. This is OBSERVATION, not a gate, and deliberately so:
#
#   * a test host's peak RSS mixes the product with xunit, fixtures and the
#     harness, so it is not a clean product measurement, and
#   * wall time on a contended machine is noisy enough that CLAUDE.md already
#     records it producing FALSE REDS in this suite.
#
# The gate for real regressions is tests/perf-budgets/workflow-budgets.json,
# which anchors on managed ALLOCATION (machine-invariant) rather than RSS or
# time — see scripts/check-perf-budgets.sh. What this adds is a cheap
# anomaly detector over a run that is happening anyway: if a step's footprint
# jumps, it shows up in the summary instead of going unnoticed until a user's
# machine swaps.
#
# time(1) differs across platforms: BSD/macOS reports "maximum resident set
# size" in BYTES with -l; GNU reports "Maximum resident set size (kbytes)"
# with -v. Handle both, and degrade to wall-time-only if neither works.
# (RUSAGE_TSV is set earlier, next to LOG_DIR — do NOT re-declare it here; an
# assignment at this point in the file runs AFTER that one and clobbers it.)

measure_step() {
    local name="$1" log="$2" cmdline="$3"
    local rusage="$LOG_DIR/$name.rusage"
    local rc=0

    # Inner redirection keeps the STEP's stdout+stderr in $log and leaves
    # time(1)'s own report — written to its stderr — alone in $rusage.
    if [ -x /usr/bin/time ]; then
        /usr/bin/time $TIME_FLAG sh -c "{ $cmdline ; } > \"$log\" 2>&1" 2>"$rusage" || rc=$?
    else
        sh -c "{ $cmdline ; } > \"$log\" 2>&1" || rc=$?
        : > "$rusage"
    fi

    # Peak RSS: BSD prints bytes, GNU prints kbytes.
    local rss_mb="" cpu_s=""
    if [ -s "$rusage" ]; then
        local bsd_bytes gnu_kb
        bsd_bytes="$(awk '/maximum resident set size/ { print $1; exit }' "$rusage" 2>/dev/null)"
        gnu_kb="$(awk -F: '/Maximum resident set size/ { gsub(/ /,"",$2); print $2; exit }' "$rusage" 2>/dev/null)"
        if [ -n "$bsd_bytes" ]; then
            rss_mb="$(awk -v b="$bsd_bytes" 'BEGIN { printf "%.0f", b/1048576 }')"
        elif [ -n "$gnu_kb" ]; then
            rss_mb="$(awk -v k="$gnu_kb" 'BEGIN { printf "%.0f", k/1024 }')"
        fi
        cpu_s="$(awk '/ user / && / sys/ { u=$1; for(i=1;i<=NF;i++) if($i=="user") u=$(i-1); for(i=1;i<=NF;i++) if($i=="sys") s=$(i-1); printf "%.0f", u+s; exit }' "$rusage" 2>/dev/null)"
    fi

    printf '%s\t%s\t%s\n' "$name" "${rss_mb:-}" "${cpu_s:-}" >> "$RUSAGE_TSV"
    return $rc
}

# run_one <name> <kind> <target> <filter> <class> <knownIssue> <prereq> <policy>
# One row of the plan, through the helpers every runner shares
# (runner_step_cmdline, runner_prereq_missing, runner_step_status in
# lib-runner.sh). Checkpoints are always on here; a marker records the target
# hash so a row whose command changed re-runs (#1362 applied to markers).
run_one() {
    local name="$1" kind="$2" target="$3" filter="${4:--}" class="$5" known="$6" prereq="$7" policy="$8"
    IDX=$(( IDX + 1 ))
    local log="$LOG_DIR/$name.log" cmdline hash rc=0 dur start reason="" status frc=0
    cmdline="$(runner_step_cmdline "$name" "$kind" "$target" "$filter")"
    hash="$(runner_target_hash "$kind" "$target" "$filter")"

    if ! runner_step_should_run "$name" "$hash"; then
        say "${D}[$IDX/$TOTAL] $name — SKIP (checkpointed)${N}"
        # Record WHERE the evidence came from. "PASS" in a resumed run's summary
        # can mean "passed twenty minutes ago on this same commit", and until now
        # nothing wrote down which. The marker is the provenance, so quote it.
        runner_ledger_record "$name" "SKIP_CHECKPOINTED" 0 0 \
            "kind=$kind" "target=$target" "filter=$filter" \
            "class=$class" "knownIssue=$known" "prereq=$prereq" \
            "evidenceFrom=$(runner_marker_path "$name")" \
            "evidenceFinished=$(runner_marker_value "$name" finished)" \
            "evidenceLog=$(runner_marker_value "$name" log)" \
            "evidenceSha=$(runner_marker_value "$name" sha)"
        return 0
    fi

    # Never start a step when the machine is already in trouble.
    runner_mem_guard "$name"
    start="$(date +%s)"
    say "${B}[$IDX/$TOTAL] $name${N}"

    if reason="$(runner_prereq_missing "$prereq")"; then
        # The same verdict the exit-77 protocol gives, without paying for the run.
        rc="$RUNNER_EXIT_SKIP"
        printf 'SKIPPED: prerequisite missing: %s\n' "$reason" > "$log"
        printf '%s\t\t\n' "$name" >> "$RUSAGE_TSV"
    else
        # Freshness: never eval a cell (see test-tier.sh run_step for the same
        # guard). build is checkpoint=never in the manifest, so a stale --no-build
        # can only come from an external change; the guard still refuses it.
        case "$kind" in
            test|project|project-chunked)
                case "$target" in
                    *.sln) runner_assert_fresh_build "$CONFIG" > "$log.freshness" 2>&1; frc=$? ;;
                    *)     runner_assert_fresh_build "$CONFIG" "$target" > "$log.freshness" 2>&1; frc=$? ;;
                esac ;;
            *)
                local _w=()
                read -r -a _w <<< "$(runner_expand_placeholders "$target")"
                while [ "${#_w[@]}" -gt 0 ] && { [ "${_w[0]}" = env ] || case "${_w[0]}" in *=*) true ;; *) false ;; esac; }; do
                    _w=("${_w[@]:1}")
                done
                runner_guard_no_build_command ${_w[@]+"${_w[@]}"} > "$log.freshness" 2>&1; frc=$? ;;
        esac
        if [ "$frc" != 0 ]; then
            dur=$(( $(date +%s) - start ))
            say "  ${R}FAIL${N} stale --no-build guard rc=$frc -> $log.freshness"
            sed 's/^/    /' "$log.freshness"
            runner_ledger_record "$name" FAIL "$frc" "$dur" "kind=$kind" "target=$target" "filter=$filter" \
                "log=$log.freshness" "class=$class" "knownIssue=$known" "prereq=$prereq" "reason=stale-no-build"
            OVERALL=1
            return 0
        fi
        # Run under measure_step so every step reports peak RSS and CPU. excise
        # is meant to be lean; a suite run we are doing anyway is free telemetry.
        measure_step "$name" "$log" "$cmdline"
        rc=$?
    fi
    dur=$(( $(date +%s) - start ))

    status="$(runner_step_status "$kind" "$class" "$policy" "$rc" "$log" "$cmdline")"
    case "$status" in
        PASS)
            runner_step_mark "$name" "$rc" "$dur" "$hash" "$log"
            say "  ${G}PASS${N} (${dur}s)" ;;
        SKIPPED)
            reason="$(grep -E '^SKIP' "$log" | tail -1)"
            say "  ${Y}SKIPPED${N} (${dur}s) — $reason" ;;
        NO_RESULT)
            say "  ${Y}NO RESULT${N} (GRADE, rc=$rc) -> $log" ;;
        FAIL_ZERO_TESTS)
            # A test step that matched NOTHING exits 0. Checkpointing that is
            # exactly the failure mode this repo is built to prevent: a green
            # that proves nothing, then permanently skipped on every resume.
            say "  ${R}FAIL${N} (${dur}s) — ZERO tests executed; refusing to checkpoint a vacuous pass"
            say "       filter: $filter"
            OVERALL=1 ;;
        *)
            say "  ${R}$status${N} rc=$rc (${dur}s) -> $log"
            tail -30 "$log" | sed 's/^/    /'
            OVERALL=1 ;;
    esac
    local trx=""
    [ -f "$LOG_DIR/$name.trx" ] && trx="$LOG_DIR/$name.trx"
    runner_ledger_record "$name" "$status" "$rc" "$dur" "kind=$kind" "target=$target" "filter=$filter" "log=$log" \
        "trx=$trx" "testsExecuted=${RUNNER_TESTS_EXECUTED:-}" "class=$class" "knownIssue=$known" "prereq=$prereq" "reason=$reason"

    # Release the build-server processes ONCE, right after the only step that
    # uses them. Every later step is --no-build, and MSBUILDDISABLENODEREUSE=1
    # already stops nodes persisting — so calling this per step would pay
    # process-teardown latency 49 more times for nothing.
    [ "$name" = "build" ] && runner_reclaim
    return 0
}

while IFS=$'\t' read -r name kind target filter class known prereq policy ckpt ratchet; do
    case "$name" in ''|'#'*) continue ;; esac
    run_one "$name" "$kind" "$target" "$filter" "$class" "$known" "$prereq" "$policy"
done < "$PLAN_FILE"

# ---------------------------------------------------------------------------
# Summary — the report below is the verdict; this is the telemetry
# ---------------------------------------------------------------------------
SUMMARY="$LOG_DIR/summary.tsv"
# name status detail, derived from the ledger so the two can never disagree.
awk -F'"' '/"name"/ {
    n = ""; s = ""; rc = ""; d = ""
    for (i = 1; i < NF; i++) {
        if ($i == "name") n = $(i+2)
        if ($i == "status") s = $(i+2)
    }
    match($0, /"rc":[0-9-]+/); rc = substr($0, RSTART + 5, RLENGTH - 5)
    match($0, /"durationSeconds":[0-9.]+/); d = substr($0, RSTART + 18, RLENGTH - 18)
    printf "%s\t%s\t%s\n", n, s, (s == "SKIP_CHECKPOINTED" ? "checkpointed" : "rc=" rc " " d "s")
}' "$LOG_DIR/ledger.jsonl" > "$SUMMARY"
say ""
say "${B}=================================================${N}"
say "${B} Telemetry${N}"
say "${B}=================================================${N}"
# --- resource hotspots ---------------------------------------------------
# Ranked, not gated. A step at the top of this list is a question ("why does
# that need 2GB?"), not a failure. Real enforcement lives in
# tests/perf-budgets/workflow-budgets.json (allocation-anchored).
if [ -s "$RUSAGE_TSV" ]; then
    say "${B}Resource hotspots${N} ${D}(observation only — the gate is check-perf-budgets.sh)${N}"
    say "  top peak RSS:"
    sort -t"$(printf '\t')" -k2,2nr "$RUSAGE_TSV" 2>/dev/null | head -5 \
      | while IFS="$(printf '\t')" read -r n rss cpu; do
            [ -n "${rss:-}" ] && say "    $(printf '%6s MB  %s' "$rss" "$n")"
        done
    say "  top CPU seconds:"
    sort -t"$(printf '\t')" -k3,3nr "$RUSAGE_TSV" 2>/dev/null | head -5 \
      | while IFS="$(printf '\t')" read -r n rss cpu; do
            [ -n "${cpu:-}" ] && say "    $(printf '%6s s   %s' "$cpu" "$n")"
        done
    say "  full per-step data: $RUSAGE_TSV"
fi

# --- corpus coverage (#958) -----------------------------------------------
# Printed unconditionally when present — not just on the missing/partial
# branch — so a FULL run states that too, and "pages covered" never has to be
# taken on faith.
if [ "${#CORPUS_COVERAGE_ROWS[@]}" -gt 0 ]; then
    say ""
    say "${B}Corpus coverage${N} ${D}(pages present / pages in the expectation manifest)${N}"
    _corpus_partial=0
    for _row in "${CORPUS_COVERAGE_ROWS[@]:-}"; do
        [ -n "$_row" ] || continue
        IFS=$'\t' read -r _row_dir _row_present _row_expected <<< "$_row"
        if [ "${_row_present:-0}" = "${_row_expected:-0}" ] && [ "${_row_expected:-0}" != "0" ]; then
            say "  ${G}$(printf '%-24s %5s / %5s pages' "$_row_dir" "$_row_present" "$_row_expected")${N}"
        else
            _corpus_partial=1
            say "  ${Y}$(printf '%-24s %5s / %5s pages' "$_row_dir" "$_row_present" "$_row_expected")${N}"
        fi
    done
    if [ "$_corpus_partial" = "1" ]; then
        if [ "${CORPUS_RAN_WITH_ALLOW_MISSING:-0}" = "1" ]; then
            say "  ${Y}Partial corpus sweep — one or more corpora were missing and${N}"
            say "  ${Y}--allow-missing-corpora let the run proceed anyway.${N}"
        else
            say "  ${Y}Partial corpus sweep — a corpus directory is under-downloaded (present${N}"
            say "  ${Y}but short of its expectation-manifest count). Re-run the matching${N}"
            say "  ${Y}scripts/download-*.sh to fill it in.${N}"
        fi
    fi
fi

say ""
say "Resources: $(runner_resource_report)"
say "Logs    : $LOG_DIR"
say "Summary : $SUMMARY"
say "Ledger  : $LOG_DIR/ledger.jsonl"
if [ "$OVERALL" != "0" ]; then
    say "${Y}Re-run the same command to retry only the failed/pending steps.${N}"
fi

# Tidy up after ourselves (#858). --blame hang dumps under TestResults reached
# 36 GB on a maintainer machine because nothing ever pruned them, on a volume
# that macOS also grows swap on. Keeps the newest few runs so a failure is
# still diagnosable. Set EXCISE_NO_ARTIFACT_PRUNE=1 to keep everything.
if [ "${EXCISE_NO_ARTIFACT_PRUNE:-0}" != "1" ] && [ -x scripts/clean-test-artifacts.sh ]; then
    say ""
    scripts/clean-test-artifacts.sh --keep 5 2>/dev/null | tail -2
fi

# The report is the verdict, and its exit code is this script's: 0 clean
# (possibly with SKIPPED rows), 1 a NEW red or a STALE acceptance, 3 a row
# that never ran, 2 nothing to report. A full pass of the whole plan records
# the tier-pass base for full, t1 and t0 (LOCAL_GATES.md "Base selection");
# a --only run is partial and records nothing.
say ""
scripts/report-gates.sh "$LOG_DIR"
rc=$?
if [ "$rc" = 0 ] && [ "$PLANNED" = "$OF" ]; then
    runner_tier_base_record_chain full
fi
exit $rc
