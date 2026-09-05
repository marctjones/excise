#!/usr/bin/env bash
# Run repeatable release-candidate gates before tagging (tier t2).
#
# This script is intentionally non-destructive: it does not create commits,
# tags, GitHub Releases, or upload artifacts. It records logs under logs/ and
# exits non-zero when a required gate fails. See issue #471.
#
# WHERE THE GATES COME FROM
# -------------------------
# tests/gates.tsv (LOCAL_GATES.md), rows whose tiers column lists t2. This
# script holds no gate list of its own: the plan is derived through
# runner_manifest_plan t2 (scripts/lib-runner.sh). t2 is a curated
# Release-config set, not a superset of t1 — `scripts/test-tier.sh --list t2`
# prints it. Rows of kind "fn" name a run_*_gate function below (the four
# gates that need flags, artifacts or a display); every other row is a
# command line. Flags become RUNNER_OPTS, which the rows' opt:NAME
# prerequisites resolve against: a gate whose flag was not passed shows as a
# SKIPPED row in the report, never as a green.
#
# The run ends with scripts/report-gates.sh, whose exit code is this script's.

set -uo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT" || exit 1

CONFIG="Debug"
RUN_FULL_TESTS=1
RUN_VISUAL=0
RUN_PACKAGE=0
RUN_PACKAGED_GUI=0
PACKAGED_GUI_FOCUS_INPUT=0
PACKAGED_GUI_MODE="direct-exec"
RUN_AOT=0
RUN_AOT_GUI_SMOKE=0
NO_AOT=0
NO_BUILD=0
VERSION=""
ONLY=""
# Opt-in crash-resumable mode (#853 follow-up). Default 0 keeps this script's
# behaviour byte-identical to before, so nothing that already invokes it
# changes; --resume is only for a human re-running a ~30-minute gate set after
# an interruption.
RESUME=0

source "$SCRIPT_DIR/lib-runner.sh"

usage() {
    cat <<'EOF'
Run repeatable release-candidate gates before tagging (tier t2 of tests/gates.tsv).

This script is intentionally non-destructive: it does not create commits, tags,
GitHub Releases, or upload artifacts. It records logs under logs/ and exits
non-zero when a required gate fails.

Usage:
  scripts/release-smoke.sh [options]        (= scripts/test-tier.sh t2 with --release-tests)

Options:
  --version <v>       Version to pass to local package builders.
  --release-tests     Run build/test gates in Release instead of Debug.
  --quick             Skip the full solution test pass (the "tests" row).
  --visual            Run the local visual-regression runner (the "visual" row).
  --package           Build local package artifacts for the current platform.
  --packaged-gui      Run packaged-app GUI smoke evidence after package build.
  --aot               Publish/package the GUI Native AOT release lane.
                      This is implied by --package unless --no-aot is passed.
  --no-aot            Opt out of the Native AOT release lane for a package-only investigation.
  --aot-gui-smoke     Also run packaged GUI smoke against the AOT app bundle.
  --packaged-gui-direct-exec
                      Run packaged GUI smoke through the app executable so app-internal timing JSON is reliable.
  --packaged-gui-background-open
                      Run packaged GUI smoke through Launch Services/open for file-activation investigation.
  --packaged-gui-focus-input
                      Also run focus-taking native key/mouse smoke.
  --no-build          Skip the initial build gate.
  --resume            Skip gates that already passed for this exact command and tree
                      (crash-resumable). Redaction gates always re-run; see scripts/lib-runner.sh.
  --only=a,b          Run only the named rows (scripts/test-tier.sh --list t2 prints them).
                      A named flag-gated row (aot, visual, package, packaged-gui, tests) runs
                      as if its flag had been passed. The run is reported PARTIAL.
  -h, --help          Show this help.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --version)
            VERSION="${2:-}"
            if [ -z "$VERSION" ]; then
                echo "--version requires a value" >&2
                exit 2
            fi
            shift 2
            ;;
        --version=*) VERSION="${1#*=}"; shift ;;
        --release-tests) CONFIG="Release"; shift ;;
        --quick) RUN_FULL_TESTS=0; shift ;;
        --visual) RUN_VISUAL=1; shift ;;
        --package) RUN_PACKAGE=1; shift ;;
        --packaged-gui) RUN_PACKAGED_GUI=1; shift ;;
        --aot) RUN_AOT=1; NO_AOT=0; shift ;;
        --no-aot) RUN_AOT=0; NO_AOT=1; shift ;;
        --aot-gui-smoke)
            RUN_AOT=1
            RUN_AOT_GUI_SMOKE=1
            shift
            ;;
        --packaged-gui-direct-exec)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_MODE="direct-exec"
            shift
            ;;
        --packaged-gui-background-open)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_MODE="background-open"
            shift
            ;;
        --packaged-gui-focus-input)
            RUN_PACKAGED_GUI=1
            PACKAGED_GUI_FOCUS_INPUT=1
            shift
            ;;
        --no-build) NO_BUILD=1; shift ;;
        --resume) RESUME=1; shift ;;
        --only=*) ONLY="${1#*=}"; shift ;;
        --only)
            ONLY="${2:-}"
            if [ -z "$ONLY" ]; then
                echo "--only requires a value" >&2
                exit 2
            fi
            shift 2
            ;;
        -h|--help) usage; exit 0 ;;
        *)
            echo "Unknown argument: $1" >&2
            usage >&2
            exit 2
            ;;
    esac
done

if [ "$RUN_PACKAGE" = "1" ] && [ "$NO_AOT" != "1" ]; then
    RUN_AOT=1
fi
if [ "$RUN_AOT" = "1" ] && [ "$RUN_PACKAGED_GUI" = "1" ] && [ "$NO_AOT" != "1" ]; then
    RUN_AOT_GUI_SMOKE=1
fi

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/release-smoke_$TS"
mkdir -p "$LOG_DIR"
ln -sf "release-smoke_$TS" "$ROOT/logs/release-smoke_latest" 2>/dev/null

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; N=''
fi

say() { echo -e "$1"; }

# Flags → RUNNER_OPTS. The manifest's flag-gated rows declare opt:NAME as a
# prerequisite; an absent one becomes a visible SKIPPED row. Every --only name
# is also an opt, so `--only=aot` runs aot without --aot (the semantics the old
# run_aot_gate had).
_opts=""
[ "$RUN_FULL_TESTS" = "1" ] && _opts="$_opts,tests"
[ "$RUN_VISUAL" = "1" ]     && _opts="$_opts,visual"
[ "$RUN_PACKAGE" = "1" ]    && _opts="$_opts,package"
[ "$RUN_PACKAGED_GUI" = "1" ] && _opts="$_opts,packaged-gui"
[ "$RUN_AOT" = "1" ]        && _opts="$_opts,aot"
[ -n "$ONLY" ]              && _opts="$_opts,$(printf '%s' "$ONLY" | tr 'A-Z' 'a-z')"
RUNNER_OPTS="${_opts#,}"
RUNNER_BUILD_ARGS=""
BLAME_HANG_TIMEOUT="${BLAME_HANG_TIMEOUT:-900000}"
export CONFIG LOG_DIR RUNNER_OPTS RUNNER_BUILD_ARGS BLAME_HANG_TIMEOUT
runner_identify_tree "$CONFIG"
if [ "$RESUME" = "1" ]; then
    runner_state_init "release-smoke" "$CONFIG"
    # Deliberately NOT calling runner_export_lean_env here: this script runs the
    # benchmark and perf-budget gates, whose budgets are allocation-anchored on
    # a known machine class. Changing the GC mode under them would move those
    # numbers and manufacture regressions. Memory tuning lives in
    # run-full-suite.sh.
fi
runner_ledger_init "$LOG_DIR/ledger.jsonl"
runner_export_oracle_env
if [ "$RUN_AOT_GUI_SMOKE" = "1" ]; then
    AOT_EXTRA_ARGS="--gui-smoke --gui-mode $PACKAGED_GUI_MODE"
fi
runner_export_release_env "$VERSION"
GATE_ASYMMETRY_BASE="$(runner_gate_asymmetry_base t2)"
export GATE_ASYMMETRY_BASE

# ---------------------------------------------------------------------------
# One outcome path for every gate
# ---------------------------------------------------------------------------
# The row being executed, for the fn gates (which build their own command line
# and call run_gate) and for finish_gate.
ROW_NAME=""; ROW_KIND="fn"; ROW_TARGET=""; ROW_FILTER="-"
ROW_CLASS="BLOCK"; ROW_KNOWN="-"; ROW_PREREQ="-"; ROW_POLICY="fail"
overall=0

# finish_gate <name> <rc> <duration> <log> <cmdline> — status mapping, marker,
# ledger row (with the manifest's class/knownIssue), console line.
finish_gate() {
    local name="$1" rc="$2" dur="$3" log="$4" cmdline="$5" status reason="" trx=""
    status="$(runner_step_status "$ROW_KIND" "$ROW_CLASS" "$ROW_POLICY" "$rc" "$log" "$cmdline")"
    case "$status" in
        PASS)
            [ "$RESUME" = "1" ] && runner_step_mark "$name" "$rc" "$dur" "$(runner_target_hash "$ROW_KIND" "$ROW_TARGET" "$ROW_FILTER")" "$log"
            say "  ${G}PASS${N} (${dur}s) -> $log" ;;
        SKIPPED)
            reason="$(grep -E '^SKIP' "$log" | tail -1)"
            say "  ${Y}SKIPPED${N} — $reason" ;;
        NO_RESULT)
            say "  ${Y}NO RESULT${N} (GRADE, rc=$rc) -> $log" ;;
        FAIL_ZERO_TESTS)
            say "  ${R}FAIL${N} (${dur}s) — ZERO tests executed; a vacuous pass is a failure"
            overall=1 ;;
        *)
            say "  ${R}$status${N} rc=$rc (${dur}s) -> $log"
            tail -40 "$log" | sed 's/^/    /'
            overall=1 ;;
    esac
    [ -f "$LOG_DIR/$name.trx" ] && trx="$LOG_DIR/$name.trx"
    runner_ledger_record "$name" "$status" "$rc" "$dur" "kind=$ROW_KIND" "target=$ROW_TARGET" "filter=$ROW_FILTER" \
        "log=$log" "trx=$trx" "testsExecuted=${RUNNER_TESTS_EXECUTED:-}" \
        "class=$ROW_CLASS" "knownIssue=$ROW_KNOWN" "prereq=$ROW_PREREQ" "reason=$reason"
    say ""
}

# skip_gate <name> <reason> — a gate that cannot run here: SKIPPED under
# policy=skip, FAIL otherwise (the exit-77 protocol, LOCAL_GATES.md).
skip_gate() {
    local name="$1" log="$LOG_DIR/$name.log"
    printf 'SKIPPED: %s\n' "$2" > "$log"
    say "${B}[$name]${N}"
    finish_gate "$name" "$RUNNER_EXIT_SKIP" 0 "$log" "-"
}

# run_gate <name> <command...> — the fn gates' real command.
run_gate() {
    local name="$1"
    shift
    local log="$LOG_DIR/$name.log" start rc
    say "${B}[$name]${N} $*"
    start="$(date +%s)"
    "$@" > "$log" 2>&1
    rc=$?
    finish_gate "$name" "$rc" $(( $(date +%s) - start )) "$log" "$*"
}

# ---------------------------------------------------------------------------
# The fn gates (tests/gates.tsv kind=fn) — the four that need flags,
# artifacts or a display. Their opt:NAME prerequisite is checked by the loop
# before they are called, so none of them tests its own flag any more.
# ---------------------------------------------------------------------------
run_packaged_gui_gate() {
    local app="$ROOT/dist/excise.app"
    local pdf="$ROOT/test-pdfs/smoke/irs-w9.pdf"
    local out="$LOG_DIR/packaged-gui"
    local -a args=("--app" "$app" "--pdf" "$pdf" "--output" "$out" "--mode" "$PACKAGED_GUI_MODE")
    if [ "$PACKAGED_GUI_FOCUS_INPUT" = "1" ]; then
        args+=("--allow-focus-input")
    fi
    run_gate "packaged-gui" scripts/run-packaged-gui-smoke.sh "${args[@]}"
}

run_package_gate() {
    local -a package_args=("--version" "$RELEASE_VERSION" "--output" "dist")
    if [ "$RUN_AOT" = "1" ]; then
        package_args+=("--aot")
    fi
    case "$(uname -s)" in
        Darwin) run_gate "package" scripts/build-macos-app.sh "${package_args[@]}" ;;
        Linux)  run_gate "package" scripts/build-deb.sh "${package_args[@]}" ;;
        *)      skip_gate "package" "local package smoke is supported on macOS/Linux here; Windows packaging is a separate issue (LOCAL_GATES.md)" ;;
    esac
}

run_visual_gate() {
    local -a visual_args=("--no-build")
    [ "$CONFIG" = "Release" ] && visual_args=("--release" "--no-build")
    run_gate "visual" scripts/run-visual-regression-local.sh "${visual_args[@]}"
}

run_dotnet_test_step() {
    local log="$1"
    local label="$2"
    local project="$3"
    local filter="${4:-}"
    local hang_timeout="${5:-}"
    local -a args=("test" "$project" "--no-build" "-c" "$CONFIG" "--logger" "console;verbosity=minimal")

    if [ -n "$filter" ]; then
        args+=("--filter" "$filter")
    fi
    if [ -n "$hang_timeout" ]; then
        args+=("--blame-hang-timeout" "$hang_timeout")
    fi

    say "  -> $label"
    {
        echo "================================================="
        echo "$label"
        echo "================================================="
    } >> "$log"

    local rc=0
    dotnet "${args[@]}" >> "$log" 2>&1 || rc=$?
    if [ "$rc" = "0" ]; then
        say "     PASS"
        return 0
    fi

    say "     ${R}FAIL${N} rc=$rc -> $log"
    tail -80 "$log" | sed 's/^/    /'
    return "$rc"
}

run_excise_gui_display_step() {
    local log="$1"
    local label="Excise.App.Tests GUI display sweep"
    local project="Excise.App.Tests/Excise.App.Tests.csproj"
    local filter="FullyQualifiedName~PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer"
    local report="Excise.App.Tests/bin/$CONFIG/net10.0/UI/test-output/gui-display-suite-renderer-contracts-representative-pages.json"
    local last_progress=""

    say "  -> $label"
    {
        echo "================================================="
        echo "$label"
        echo "================================================="
    } >> "$log"
    rm -f "$report" 2>/dev/null || true

    dotnet test "$project" --no-build -c "$CONFIG" --filter "$filter" --logger "console;verbosity=minimal" >> "$log" 2>&1 &
    local pid=$!
    while kill -0 "$pid" 2>/dev/null; do
        sleep 30
        if [ -f "$report" ] && command -v jq >/dev/null 2>&1; then
            local progress
            progress="$(jq -r '
                def failures: ([.results[]? | select(.status == "FAIL")] | length);
                def nonpass: ([.results[]? | select(.status != "PASS" and .status != "NON_RENDERABLE_ACCEPTED")] | length);
                if .current then
                    "\(.current.ordinal)/\(.current.total) \(.current.path) page \(.current.page), failures \(failures), non-pass \(nonpass)"
                else
                    "\(.results | length) result(s), failures \(failures), non-pass \(nonpass)"
                end
            ' "$report" 2>/dev/null || true)"
            if [ -n "$progress" ] && [ "$progress" != "$last_progress" ]; then
                say "     progress: $progress"
                last_progress="$progress"
            fi
        fi
    done

    local rc=0
    wait "$pid" || rc=$?
    if [ "$rc" = "0" ]; then
        say "     PASS"
        return 0
    fi

    say "     ${R}FAIL${N} rc=$rc -> $log"
    tail -80 "$log" | sed 's/^/    /'
    return "$rc"
}

run_full_tests_gate() {
    local log="$LOG_DIR/tests.log"
    local start
    start="$(date +%s)"
    local rc=0
    local project
    local -a test_projects=(
        "Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj"
        "Excise.Cli.Tests/Excise.Cli.Tests.csproj"
        "Excise.Core.Tests/Excise.Core.Tests.csproj"
        "Excise.Ocr.Tests/Excise.Ocr.Tests.csproj"
        "Excise.Rendering.Tests/Excise.Rendering.Tests.csproj"
    )

    say "${B}[tests]${N} sequential project tests with hang diagnostics"
    : > "$log"
    for project in "${test_projects[@]}"; do
        run_dotnet_test_step "$log" "$project" "$project" "" "5m" || { rc=$?; break; }
    done

    if [ "$rc" = "0" ]; then
        run_dotnet_test_step "$log" "Excise.App.Tests ordinary slice" \
            "Excise.App.Tests/Excise.App.Tests.csproj" \
            "FullyQualifiedName!~KeyboardShortcutTests.CtrlW_ClosesDocument&FullyQualifiedName!~PdfViewerHeadlessRenderTests.PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer" \
            "5m" || rc=$?
    fi
    if [ "$rc" = "0" ]; then
        run_dotnet_test_step "$log" "Excise.App.Tests Ctrl+W shortcut" \
            "Excise.App.Tests/Excise.App.Tests.csproj" \
            "FullyQualifiedName~KeyboardShortcutTests.CtrlW_ClosesDocument" \
            "2m" || rc=$?
    fi
    if [ "$rc" = "0" ]; then
        run_excise_gui_display_step "$log" || rc=$?
    fi

    # The step logs above carry no single "Total:" line, so the zero-tests
    # guard in runner_step_status must not see "dotnet test" in the cmdline.
    finish_gate "tests" "$rc" $(( $(date +%s) - start )) "$log" "sequential project tests"
}

# ---------------------------------------------------------------------------
# One row of the plan
# ---------------------------------------------------------------------------
run_row() {
    ROW_NAME="$1"; ROW_KIND="$2"; ROW_TARGET="$3"; ROW_FILTER="$4"
    ROW_CLASS="$5"; ROW_KNOWN="$6"; ROW_PREREQ="$7"; ROW_POLICY="$8"
    local name="$1" log="$LOG_DIR/$1.log" cmdline hash start rc reason="" frc=0
    cmdline="$(runner_step_cmdline "$ROW_NAME" "$ROW_KIND" "$ROW_TARGET" "$ROW_FILTER")"
    hash="$(runner_target_hash "$ROW_KIND" "$ROW_TARGET" "$ROW_FILTER")"

    # --resume: skip gates with a valid checkpoint for this exact command and
    # tree. The redaction gates are checkpoint=never in the manifest, so they
    # re-run here even on a resume — CLAUDE.md allows no flag that skips them.
    if [ "$RESUME" = "1" ] && ! runner_step_should_run "$name" "$hash"; then
        say "${B}[$name]${N} SKIP - already passed (checkpointed)"
        runner_ledger_record "$name" SKIP_CHECKPOINTED 0 0 "kind=$ROW_KIND" "target=$ROW_TARGET" "filter=$ROW_FILTER" \
            "class=$ROW_CLASS" "knownIssue=$ROW_KNOWN" "prereq=$ROW_PREREQ" \
            "evidenceFrom=$(runner_marker_path "$name")" "evidenceFinished=$(runner_marker_value "$name" finished)" \
            "evidenceLog=$(runner_marker_value "$name" log)" "evidenceSha=$(runner_marker_value "$name" sha)"
        say ""
        return
    fi
    [ "$RESUME" = "1" ] && runner_mem_guard "$name"

    if reason="$(runner_prereq_missing "$ROW_PREREQ")"; then
        case "$reason" in
            opt:*) skip_gate "$name" "pass --${reason#opt:} to run this gate (prerequisite $reason)" ;;
            *)     skip_gate "$name" "prerequisite missing: $reason" ;;
        esac
        return
    fi

    if [ "$ROW_KIND" = "fn" ]; then
        case "$ROW_TARGET" in
            run_full_tests_gate|run_visual_gate|run_package_gate|run_packaged_gui_gate) "$ROW_TARGET" ;;
            *) skip_gate "$name" "unknown fn gate $ROW_TARGET" ;;
        esac
        return
    fi

    # Freshness: never eval a cell (the same guard as test-tier.sh run_step).
    case "$ROW_KIND" in
        test|project|project-chunked)
            case "$ROW_TARGET" in
                *.sln) runner_assert_fresh_build "$CONFIG" > "$log.freshness" 2>&1; frc=$? ;;
                *)     runner_assert_fresh_build "$CONFIG" "$ROW_TARGET" > "$log.freshness" 2>&1; frc=$? ;;
            esac ;;
        *)
            local _w=()
            read -r -a _w <<< "$(runner_expand_placeholders "$ROW_TARGET")"
            while [ "${#_w[@]}" -gt 0 ] && { [ "${_w[0]}" = env ] || case "${_w[0]}" in *=*) true ;; *) false ;; esac; }; do
                _w=("${_w[@]:1}")
            done
            runner_guard_no_build_command ${_w[@]+"${_w[@]}"} > "$log.freshness" 2>&1; frc=$? ;;
    esac
    if [ "$frc" != 0 ]; then
        say "${B}[$name]${N} ${R}FAIL${N} stale --no-build guard rc=$frc -> $log.freshness"
        sed 's/^/    /' "$log.freshness"
        runner_ledger_record "$name" FAIL "$frc" 0 "kind=$ROW_KIND" "target=$ROW_TARGET" "filter=$ROW_FILTER" \
            "log=$log.freshness" "class=$ROW_CLASS" "knownIssue=$ROW_KNOWN" "prereq=$ROW_PREREQ" "reason=stale-no-build"
        overall=1
        say ""
        return
    fi

    say "${B}[$name]${N} $cmdline"
    start="$(date +%s)"
    sh -c "$cmdline" > "$log" 2>&1
    rc=$?
    finish_gate "$name" "$rc" $(( $(date +%s) - start )) "$log" "$cmdline"
}

# ---------------------------------------------------------------------------
# Plan — tier t2 of tests/gates.tsv
# ---------------------------------------------------------------------------
PLAN_FILE="$LOG_DIR/plan.tsv"
runner_manifest_plan t2 > "$PLAN_FILE.rows" || { say "${R}tests/gates.tsv is defective; nothing ran.${N}"; exit 2; }
OF="$(grep -c . "$PLAN_FILE.rows")"
if [ "$NO_BUILD" = "1" ]; then
    grep -v "^build	" "$PLAN_FILE.rows" > "$PLAN_FILE.nobuild" || true
    mv "$PLAN_FILE.nobuild" "$PLAN_FILE.rows"
fi
if [ -n "$ONLY" ]; then
    # Comma-separated names, case-insensitive; the header then says
    # planned<of and the report marks the run PARTIAL.
    awk -F'\t' -v only=",$(printf '%s' "$ONLY" | tr 'A-Z' 'a-z')," 'index(only, "," tolower($1) ",")' "$PLAN_FILE.rows" > "$PLAN_FILE.only" || true
    mv "$PLAN_FILE.only" "$PLAN_FILE.rows"
fi
PLANNED="$(grep -c . "$PLAN_FILE.rows")"
runner_plan_write "$PLAN_FILE" t2 "$PLAN_FILE.rows" "$PLANNED" "$OF" "${ONLY:--}"
rm -f "$PLAN_FILE.rows"
runner_plan_expand_trx "$PLAN_FILE" "$LOG_DIR" || exit 2

say "${B}=================================================${N}"
say "${B} excise release smoke (t2)${N}"
say "${B}=================================================${N}"
say "Started : $(date)"
say "Test config : $CONFIG"
say "Rows    : $PLANNED of $OF (tests/gates.tsv $(runner_manifest_fingerprint))"
say "Tree    : @${RUNNER_SHA:0:12}$([ "$RUNNER_TREE_DIRTY" = yes ] && echo ' DIRTY')"
say "Opts    : ${RUNNER_OPTS:-(none)}"
say "Version : $RELEASE_VERSION"
say "Logs    : $LOG_DIR"
say ""

while IFS=$'\t' read -r name kind target filter class known prereq policy ckpt ratchet; do
    case "$name" in ''|'#'*) continue ;; esac
    run_row "$name" "$kind" "$target" "$filter" "$class" "$known" "$prereq" "$policy"
done < "$PLAN_FILE"

# The report is the verdict, and its exit code is this script's: 0 clean
# (possibly with SKIPPED rows), 1 a NEW red or a STALE acceptance, 3 a row
# that never ran, 2 nothing to report.
scripts/report-gates.sh "$LOG_DIR"
rc=$?
if [ "$rc" = 0 ] && [ "$PLANNED" = "$OF" ]; then
    runner_tier_base_record t2
fi
exit $rc
