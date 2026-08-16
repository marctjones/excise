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
  --assert-green     Exit 0 only when EVERY checkpointable step of the
                     --everything plan has a valid checkpoint at exactly this
                     commit from a CLEAN tree; exit 1 otherwise, listing what
                     is missing. Never-checkpointed steps (the redaction
                     gates) are reported but not required — the caller must
                     re-run those live (scripts/tag-release.sh does). This is
                     the release-tagging evidence check: CI cannot run the
                     corpus/GUI/packaging tiers, so the tag demands proof the
                     local box did.
  --release          Build and test in Release (default: Debug).
  --only <pattern>   Only steps whose name matches this grep -E pattern.
  --everything       Also run the release-only gates (accessibility, automation,
                     ux, benchmark, perf-budget, aot, pdf20) by delegating to
                     release-smoke.sh. Without this, run-full-suite covers every
                     TEST PROJECT but not those script gates. Also runs the
                     four-corpus rendering scan (#862) — REQUIRES all four
                     corpora to be downloaded, or see --allow-missing-corpora.
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
        --assert-green) MODE="assert-green"; EVERYTHING=1; shift ;;
        --release) CONFIG="Release"; shift ;;
        --only) ONLY="${2:-}"; shift 2 ;;
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
mkdir -p "$LOG_DIR"

# BSD (macOS) time uses -l; GNU time uses -v.
if /usr/bin/time -l true >/dev/null 2>&1; then TIME_FLAG="-l"; else TIME_FLAG="-v"; fi
RUSAGE_TSV="$LOG_DIR/resources.tsv"
: > "$RUSAGE_TSV"

runner_state_init "full-suite" "$CONFIG"
# One JSON object per step, including steps skipped as already-checkpointed
# (#994). Write-only: no gate reads it yet, and summary.tsv / resources.tsv /
# tag-release.sh are untouched. It is the reviewable record of what this
# invocation actually ran; the sha-keyed markers remain the enforcement channel.
runner_ledger_init "$LOG_DIR/ledger.jsonl"
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

# Per-corpus "pages present / pages expected" (#958). Populated by the
# preflight check in Phase 5b, printed in the final Summary — declared here
# (unconditionally, empty) so the Summary section can check its length
# without caring whether --everything was passed.
CORPUS_COVERAGE_ROWS=()

# --- Phase 1: build once, then never again -------------------------------
emit "build" script "dotnet build excise.sln -c $CONFIG -m:1" "-"

# --- Phase 2: static / cheap gates ---------------------------------------
emit "doc-claims"           script "scripts/verify-doc-claims.sh" "-"
emit "gate-asymmetry"       script "scripts/check-gate-asymmetry.sh origin/develop" "-"
emit "redaction-architecture" script "scripts/verify-true-redaction.sh" "-"
emit "testdata-sync"        script "scripts/check-testdata-sync.sh" "-"
emit "skip-budget-selftest" script "scripts/test-check-skip-budget.sh" "-"
# Unwired public API (#908). The skip budget and test count catch a TEST that
# stops running; this catches PRODUCTION CODE that never started. Both bugs it
# guards against were written, tested, and then not called: #908 (CffSubsetter —
# 25 test references, zero production callers, so CFF fonts ship unsubsetted)
# and #896 (RedactWithOptions, same shape, and the CLI leaked redacted terms
# into /Info and XMP for as long as nothing called the safe path).
#
# Ratchets against tests/unwired-api-baseline.tsv: accepted entries fail only
# on anything NEW. Cheap (~10s, a text index), so it sits with the static gates
# rather than the suites.
emit "unwired-api"          script "scripts/check-unwired-api.sh --quiet" "-"
# Whole-solution private/internal reachability (#940). This is intentionally
# Roslyn-backed rather than grep: XAML bindings, command string IDs, overrides,
# interface implementations, and the public package surface are seeded before
# computing the unreachable closure.
emit "reachability"         script "scripts/check-reachability.sh --quiet" "-"

# --- Phase 3: the redaction gates (NEVER checkpointed — always re-run) ----
emit "redaction-suites" test "excise.sln" "FullyQualifiedName~Redaction"

# --- Phase 4: every test project ------------------------------------------
for proj in $PROJECTS_SMALL; do
    emit "$proj" test "$proj/$proj.csproj" "-"
done

# NOT an associative array: macOS ships bash 3.2, where `declare -A` is an
# invalid option and the lookup silently reads empty — which is exactly how
# the first draft of the gate below emitted nothing while looking correct.
RENDERING_CHUNK_TRX=""
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
            # Remember every chunk's trx so the #894 discovered-vs-reported
            # gate can consume their UNION below: chunks partition the project
            # by test class, so the union is exactly an unfiltered run, and the
            # gate refuses any single filtered trx by construction.
            if [ "$proj" = "Excise.Rendering.Tests" ]; then
                RENDERING_CHUNK_TRX="$RENDERING_CHUNK_TRX --trx $LOG_DIR/$proj.$cname.trx"
            fi
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

# --- Phase 5b: release-only gates (--everything) --------------------------
# Delegated to release-smoke.sh rather than re-listed here, so the gate set
# cannot drift from the canonical definition. --quick skips its own full test
# pass (every project already ran above) and --no-build reuses this run's build.
#
# Deliberately NOT included even under --everything: `package`, `packaged-gui`
# and `visual`. Those need built platform artifacts, a real app bundle, and (on
# macOS) an Accessibility permission grant for the focus-taking input smoke —
# they cannot run unattended from one command. docs/RELEASE_CHECKLIST.md owns
# them.
if [ "$EVERYTHING" = "1" ]; then
    emit "release-gates" script "scripts/release-smoke.sh --no-build --quick --resume --only=accessibility,automation,ux,benchmark,perf-budget,aot,pdf20" "-"
    # Deep fuzz sweep (#984): StructureAwareFuzzTests is checked in at 250
    # iterations/seed for t0's ~30s push budget, and every escape it has
    # actually found needed thousands (#975 at 5432, others at 5632/7132/
    # ~10955). There is no nightly runner to schedule a deeper pass against
    # (nightly-corpus is status: planned, primaryCommand: null), so this is
    # the cheapest place depth is reached at least once per release: git-
    # tracked fixtures only (no corpus dependency), ~20000 iterations/seed
    # across six rows in well under a minute of test-body time. A failure
    # names its seed/iteration/fixture in the assertion message, which
    # reproduces exactly by restoring EXCISE_FUZZ_ITERATIONS and the seed.
    emit "deep-fuzz-sweep" script "scripts/run-deep-fuzz-sweep.sh" "-"
    # Corpus rendering scans (#862). Each page is classified PASS / PASS_ONE /
    # DIFF / MALFORMED_PDF / ... and the run fails when a status departs from
    # that corpus's expectation manifest. The checked-in password manifest
    # (#864) is used by default, so encrypted fixtures are actually decrypted
    # rather than written off as unsupported.
    #
    # FOUR CORPORA, not one — 3,915 pages. Each is adversarial in a different
    # way, and the spread is the point:
    #
    #   pdf.js   685 pages, 96.1% PASS  Mozilla's regression history
    #   veraPDF 2694 pages, 99.7% PASS  PDF Association PDF/A + PDF/UA suite
    #   Isartor  205 pages,  100% PASS  PDF Association PDF/A-1 violations
    #   PDFium   331 pages,  74.6% PASS Chrome's regression history
    #
    # PDFium's set is by far the harshest, which is exactly why it earns its
    # place: it is the only corpus here assembled from crashes in the most
    # widely deployed PDF renderer there is.
    #
    # --extra-oracles all adds PDFBox and PDFium to the default Ghostscript
    # escalation. This is close to free: extra oracles only run on pages where
    # mutool and pdftocairo have ALREADY disagreed, so the cost lands only
    # where a tie-break is actually needed — which is also where a single
    # oracle is least trustworthy.
    #
    # Under --everything rather than the default run: these take tens of
    # minutes together.
    # name : corpus-relative dir : expectation manifest. Single source of
    # truth for both the preflight check below and the emit loop that
    # follows it — a corpus added to one and not the other is exactly the
    # kind of drift #958 exists to catch elsewhere.
    _CORPUS_SPECS=(
        "corpus-scan-pdfjs:test-pdfs/pdfjs:tests/corpus-expectations.tsv"
        "corpus-scan-verapdf:test-pdfs/verapdf-corpus:tests/corpus-expectations-verapdf.tsv"
        "corpus-scan-isartor:test-pdfs/isartor:tests/corpus-expectations-isartor.tsv"
        "corpus-scan-pdfium:test-pdfs/pdfium:tests/corpus-expectations-pdfium.tsv"
    )

    # --- Preflight: refuse a silently partial corpus sweep (#958) ---------
    # Until now, a missing/empty corpus directory was skipped a few lines
    # below with no failure and no mention in the summary — "--everything"
    # could read as the full 3,915-page / four-corpus sweep while actually
    # covering a fraction of it (e.g. 971 of 3,915 pages, 25%, observed
    # 2026-08-10 with only pdf.js and PDFium present). "Pages expected" is
    # read from each checked-in expectation manifest (non-comment line
    # count) rather than hard-coded, so it can never drift from the manifest
    # the scan actually grades against. "Pages present" counts *.pdf files in
    # the corpus dir, which is the exact quantity --page-mode first (used
    # below) turns into pages scanned: one page per file.
    _missing_corpora=""
    for _corpus_spec in "${_CORPUS_SPECS[@]}"; do
        _cs_name="${_corpus_spec%%:*}"; _cs_rest="${_corpus_spec#*:}"
        _cs_dir="${_cs_rest%%:*}"; _cs_manifest="${_cs_rest#*:}"

        _cs_present=0
        if [ -d "$ROOT/$_cs_dir" ]; then
            _cs_present="$(find "$ROOT/$_cs_dir" -name '*.pdf' 2>/dev/null | wc -l | tr -d ' ')"
        fi
        _cs_expected=0
        if [ -f "$ROOT/$_cs_manifest" ]; then
            _cs_expected="$(grep -vc '^#' "$ROOT/$_cs_manifest" 2>/dev/null | tr -d ' ')"
        fi
        CORPUS_COVERAGE_ROWS+=("$_cs_dir	${_cs_present:-0}	${_cs_expected:-0}")

        if [ "${_cs_present:-0}" = "0" ]; then
            _dl_cmd="scripts/download-test-pdfs.sh"
            case "$_cs_name" in
                corpus-scan-pdfjs)  _dl_cmd="scripts/download-pdfjs-corpus.sh" ;;
                corpus-scan-pdfium) _dl_cmd="scripts/download-pdfium-corpus.sh" ;;
            esac
            _missing_corpora="${_missing_corpora}  $(printf '%-24s' "$_cs_dir") (0/${_cs_expected:-0} pages)  ->  $_dl_cmd\n"
        fi
    done

    CORPUS_RAN_WITH_ALLOW_MISSING=0
    if [ -n "$_missing_corpora" ]; then
        if [ "$ALLOW_MISSING_CORPORA" = "1" ]; then
            CORPUS_RAN_WITH_ALLOW_MISSING=1
            say "${Y}WARNING: --everything corpus sweep is PARTIAL (#958) — missing/empty corpora:${N}"
            printf "%b" "$_missing_corpora"
            say "${Y}Continuing because --allow-missing-corpora was passed. The scans are${N}"
            say "${Y}still in the plan and will FAIL — the flag lets the rest of the suite${N}"
            say "${Y}run, it does not remove them from the evidence.${N}"
            say ""
        else
            say "${R}ABORT: --everything corpus sweep would be silently partial (#958).${N}"
            say "The following corpora are empty or missing:"
            printf "%b" "$_missing_corpora"
            say ""
            say "  Download the missing corpora above, then re-run. Or pass"
            say "  ${B}--allow-missing-corpora${N} to run the rest of the suite anyway; the"
            say "  missing scans then fail as steps, so the run cannot report green."
            exit 1
        fi
    fi

    # EVERY corpus is emitted, present or not. A missing one must be a RED step,
    # never an absent one.
    #
    # It used to be skipped here, on the reasoning that the preflight above had
    # already been loud about it. But the preflight is console output, and the
    # plan is the evidence: --assert-green re-derives this same plan in a later
    # invocation and asks only "does every step in it have a marker?". With the
    # step skipped, the plan shrank on both sides and the answer was yes.
    # Measured 2026-08-16 by hiding test-pdfs/pdfjs: total went 66 -> 65 steps
    # and 4 -> 3 corpus scans, so a release could be tagged "65/65 green" on a
    # build where the pdf.js corpus was never scanned at all.
    #
    # Emitting unconditionally means a missing corpus fails its step (the scan
    # script exits 1 on an absent OR empty directory), the run ends non-zero,
    # no marker is written, and --assert-green reports it missing — with no
    # changes to --assert-green, because the plan can no longer shrink.
    # --allow-missing-corpora now means "don't abort the whole run at preflight,
    # let the rest of the suite run and report these red", not "pretend the plan
    # is smaller".
    for _corpus_spec in "${_CORPUS_SPECS[@]}"; do
        _cs_name="${_corpus_spec%%:*}"; _cs_rest="${_corpus_spec#*:}"
        _cs_dir="${_cs_rest%%:*}"; _cs_manifest="${_cs_rest#*:}"
        emit "$_cs_name" script \
            "scripts/run-exploratory-corpus.sh --corpus $_cs_dir --page-mode first --extra-oracles all --expectation-manifest $_cs_manifest" "-"
    done
fi

# --- Phase 6: unchunked App.Tests as the release evidence -----------------
# Chunking changes which tests share a process. That can hide OR manufacture
# cross-test contamination (a real one was found before: a shared window.json
# view-mode preference leaking between continuous-view tests, which only
# reproduced in a full-suite run). Chunks above give fast, resumable feedback;
# this single serial pass is what actually counts as evidence. CLAUDE.md:
# Excise.App.Tests is serial by design and must run alone.
# #894 for Excise.Rendering.Tests — the project where a DETERMINISTIC
# discovered-but-never-executed case is known to live, and the one project no
# unfiltered run covers: CI filters its step by design and this plan chunks it.
# Rather than duplicating a ~10-minute suite just to count, feed the gate the
# union of the chunk trx files it already produced.
if [ -n "$RENDERING_CHUNK_TRX" ]; then
    emit "test-count-rendering" script \
        "scripts/check-test-count.sh Excise.Rendering.Tests/Excise.Rendering.Tests.csproj$RENDERING_CHUNK_TRX" "-"
fi
emit "app-tests-unchunked-evidence" test "Excise.App.Tests/Excise.App.Tests.csproj" "-"
# #894: the App suite is the largest unfiltered run in the plan, and until now
# nothing checked that every test it DISCOVERED actually reported a result.
# t0 covers Core/Cli/Avalonia; this closes App. The gate re-runs whatever went
# missing, so a transient vstest reporting loss is reported and a genuine
# coverage hole is fatal.
emit "test-count-app" script "scripts/check-test-count.sh Excise.App.Tests/Excise.App.Tests.csproj --trx $LOG_DIR/app-tests-unchunked-evidence.trx" "-"

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
if [ "$MODE" = "assert-green" ]; then
    # Evidence must come from a clean tree: runner_state_init keys a dirty
    # tree as "<sha>-dirty", so its markers can never satisfy this check —
    # but fail explicitly rather than reporting every step "missing".
    if ! git -C "$ROOT" diff --quiet 2>/dev/null || ! git -C "$ROOT" diff --cached --quiet 2>/dev/null; then
        say "${R}ASSERT-GREEN FAIL: working tree is dirty.${N}"
        say "Release evidence must be a full-suite run at a clean, committed HEAD."
        exit 1
    fi

    missing=0; ok=0; always=0
    MISSING_LIST=""
    while IFS=$'\t' read -r name kind target filter; do
        [ -n "$name" ] || continue
        if runner_is_never_checkpointed "$name"; then
            always=$(( always + 1 ))
        elif runner_step_should_run "$name"; then
            missing=$(( missing + 1 ))
            MISSING_LIST="$MISSING_LIST  $name\n"
        else
            ok=$(( ok + 1 ))
        fi
    done < "$PLAN_FILE"

    if [ "$missing" -gt 0 ]; then
        say "${R}ASSERT-GREEN FAIL: $missing of $TOTAL steps have no valid checkpoint at $(git -C "$ROOT" rev-parse --short HEAD).${N}"
        printf "%b" "$MISSING_LIST"
        say ""
        say "Run the full local suite first (restartable — safe to interrupt):"
        say "  caffeinate -i scripts/run-full-suite.sh --everything --resume 2>&1 | tee -a logs/full-suite.log"
        exit 1
    fi

    say "${G}ASSERT-GREEN OK${N}: $ok/$TOTAL steps checkpointed at $(git -C "$ROOT" rev-parse --short HEAD)."
    say "  $always never-checkpointed step(s) (redaction gates) are NOT covered by"
    say "  checkpoints and must be re-run live by the caller."
    exit 0
fi

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

run_one() {
    local name="$1" kind="$2" target="$3" filter="${4:--}"
    IDX=$(( IDX + 1 ))

    if ! runner_step_should_run "$name"; then
        say "${D}[$IDX/$TOTAL] $name — SKIP (checkpointed)${N}"
        RESULTS+=("$name|SKIP|checkpointed")
        # Record WHERE the evidence came from. "PASS" in a resumed run's summary
        # can mean "passed twenty minutes ago on this same commit", and until now
        # nothing wrote down which. The marker is the provenance, so quote it.
        runner_ledger_record "$name" "SKIP_CHECKPOINTED" 0 0 \
            "kind=$kind" "target=$target" "filter=$filter" \
            "evidenceFrom=$(runner_marker_path "$name")" \
            "evidenceFinished=$(grep -h '^finished=' "$(runner_marker_path "$name")" 2>/dev/null | head -1 | cut -d= -f2-)"
        return 0
    fi

    # Never start a step when the machine is already in trouble.
    runner_mem_guard "$name"

    local log="$LOG_DIR/$name.log"
    local start rc dur
    start="$(date +%s)"

    say "${B}[$IDX/$TOTAL] $name${N}"

    # Build the step's command line, then run it under measure_step so every
    # step reports peak RSS and CPU. excise is meant to be lean; a suite run we
    # are doing anyway is free telemetry for spotting bloat.
    local cmdline
    if [ "$kind" = "script" ]; then
        cmdline="$target"
    elif [ "$filter" = "-" ]; then
        # Unfiltered runs also emit a trx: it is what scripts/check-test-count.sh
        # needs to catch a test that was DISCOVERED and then produced no result
        # (#894), and that gate only accepts an unfiltered trx by construction.
        # Filtered/chunked steps deliberately do not get one — feeding the gate
        # a filtered trx would report everything the filter excluded as a hole.
        cmdline="dotnet test \"$target\" --no-build -c \"$CONFIG\" --logger \"console;verbosity=minimal\" --logger \"trx;LogFileName=$LOG_DIR/$name.trx\""
    else
        cmdline="dotnet test \"$target\" --no-build -c \"$CONFIG\" --filter \"$filter\" --logger \"console;verbosity=minimal\" --logger \"trx;LogFileName=$LOG_DIR/$name.trx\""
    fi
    measure_step "$name" "$log" "$cmdline"
    rc=$?

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
            runner_ledger_record "$name" "FAIL_ZERO_TESTS" "$rc" "$dur" \
                "kind=$kind" "target=$target" "filter=$filter" \
                "log=$log" "testsExecuted=0"
            OVERALL=1
            return 0
        fi
    fi

    local ledger_trx=""
    [ -f "$LOG_DIR/$name.trx" ] && ledger_trx="$LOG_DIR/$name.trx"

    if [ "$rc" = "0" ]; then
        runner_step_mark "$name" "$rc" "$dur"
        say "  ${G}PASS${N} (${dur}s)"
        RESULTS+=("$name|PASS|${dur}s")
        runner_ledger_record "$name" "PASS" "$rc" "$dur" \
            "kind=$kind" "target=$target" "filter=$filter" \
            "log=$log" "trx=$ledger_trx" "testsExecuted=${executed:-}"
    else
        say "  ${R}FAIL${N} rc=$rc (${dur}s) -> $log"
        tail -30 "$log" | sed 's/^/    /'
        RESULTS+=("$name|FAIL|rc=$rc ${dur}s")
        runner_ledger_record "$name" "FAIL" "$rc" "$dur" \
            "kind=$kind" "target=$target" "filter=$filter" \
            "log=$log" "trx=$ledger_trx" "testsExecuted=${executed:-}"
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
# --- resource hotspots ---------------------------------------------------
# Ranked, not gated. A step at the top of this list is a question ("why does
# that need 2GB?"), not a failure. Real enforcement lives in
# tests/perf-budgets/workflow-budgets.json (allocation-anchored).
if [ -s "$RUSAGE_TSV" ]; then
    say ""
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
# Populated only under --everything. Printed unconditionally when present —
# not just on the missing/partial branch — so a FULL run states that too,
# and "pages covered" never has to be taken on faith.
if [ "${#CORPUS_COVERAGE_ROWS[@]}" -gt 0 ]; then
    say ""
    say "${B}Corpus coverage${N} ${D}(pages present / pages in the expectation manifest)${N}"
    _corpus_partial=0
    # bash 3.2 (macOS default) treats expanding an EMPTY array under `set -u`
    # as an unbound-variable error, not zero iterations — the same reason
    # RESULTS is expanded as "${RESULTS[@]:-}" below. The length check above
    # already guarantees this array is non-empty when we get here, but the
    # `:-` + empty-guard keeps the same safe shape as the rest of the file.
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
say "pass=$npass fail=$nfail skipped-as-checkpointed=$nskip total=$TOTAL"
say "Resources: $(runner_resource_report)"
say "Logs    : $LOG_DIR"
say "Summary : $SUMMARY"
say "Ledger  : $LOG_DIR/ledger.jsonl"
if [ "$OVERALL" != "0" ]; then
    say ""
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

exit $OVERALL
