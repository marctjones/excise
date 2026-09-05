#!/usr/bin/env bash
# Test tiers (#646): a single, defined answer to "what do I run before X?"
#
# The FRONT DOOR for every local gate. The gates themselves are declared in
# ONE place — tests/gates.tsv (LOCAL_GATES.md) — and this script holds no step
# list of its own: run_tier derives the plan from the manifest through
# runner_manifest_plan <tier> (scripts/lib-runner.sh), which validates it and
# refuses to run a defective plan. The same file is what a future GitHub
# Action will consume; nothing is built for Actions here.
#
# Tier is selected by BLAST RADIUS — who gets hurt if this is wrong — not by
# convenience (measured 2026-09-04; the first manifest-driven runs write the
# ledger that settles t1 and full):
#
#   t0    ~5 min    "did I break it"        pre-push, no excuse not to run it
#                   (2–4 min measured over eight warm runs; ~5 with a cold build)
#   t1    ~20–25m   merge gate              before anything lands on develop
#   full  ≈3 h      everything, chunked and resumable — scripts/run-full-suite.sh
#   t2    ~30 min   release candidate       scripts/release-smoke.sh --release-tests
#   t3    —         t2, then the macOS-only note (LOCAL_GATES.md)
#
# Chain semantics: t0 ⊂ t1 ⊂ full. t2 is a curated Release-config set, not a
# superset. Every run ends with scripts/report-gates.sh, whose exit code IS
# this script's exit code: it separates a NEW red (blocks) from a KNOWN one
# (an open issue named in the manifest), surfaces SKIPPED prerequisites, and
# prints the GRADES against the reference tools.
#
# excise-specific rule: YOU ARE YOUR OWN THIRD PARTY. A local build you redact
# a real document with is a binary whose failure hurts someone, silently — no
# crash, no error, the name is just still in the file. The redaction gate is
# therefore non-negotiable at every tier that produces a binary anyone will
# redact with, including a purely local build. t0 includes the static
# redaction-architecture guard (verify-true-redaction.sh, near-free); t1
# includes the full redaction test suites and accepts no flag to skip them —
# their rows are checkpoint=never in the manifest, so --resume re-runs them.
#
# Usage: scripts/test-tier.sh {t0|t1|full|t2|t3} [--resume] [full options]
#        scripts/test-tier.sh --list [tier]
#        scripts/test-tier.sh --report [LOG_DIR|--latest] [--full] [--no-gh]
#        scripts/test-tier.sh --install-hook
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

if [ -t 1 ]; then
    R='\033[0;31m'; G='\033[0;32m'; Y='\033[1;33m'; B='\033[0;36m'; N='\033[0m'
else
    R=''; G=''; Y=''; B=''; N=''
fi

say() { echo -e "$1"; }

source "$ROOT/scripts/lib-runner.sh"

TIER=""
INSTALL_HOOK=0
# Opt-in crash-resumable mode. Default 0 keeps the pre-push hook skipping
# nothing. run-full-suite.sh (tier full) checkpoints on its own.
RESUME=0
LIST=0
REPORT=0
REPORT_DIR=""
REPORT_FULL=""
REPORT_GH=""
PASS_ARGS=()
while [ "$#" -gt 0 ]; do
    case "$1" in
        t0|t1|t2|t3|full) TIER="$1" ;;
        --install-hook) INSTALL_HOOK=1 ;;
        --resume) RESUME=1; PASS_ARGS+=("$1") ;;
        --list) LIST=1 ;;
        --report) REPORT=1 ;;
        --latest) REPORT_DIR="--latest" ;;
        --full) REPORT_FULL="--full" ;;
        --no-gh) REPORT_GH="--no-gh" ;;
        # run-full-suite.sh options pass through untouched.
        --fresh|--everything|--allow-missing-corpora|--skip-chunking|--status) PASS_ARGS+=("$1") ;;
        --only) PASS_ARGS+=("$1" "${2:-}"); shift ;;
        -h|--help) TIER=""; break ;;
        -*) echo "unknown option: $1" >&2; TIER=""; break ;;
        *) REPORT_DIR="$1" ;;
    esac
    shift
done

usage() {
    cat <<'EOF'
Usage: scripts/test-tier.sh {t0|t1|full|t2|t3} [--resume]
       scripts/test-tier.sh --list [tier]
       scripts/test-tier.sh --report [LOG_DIR|--latest] [--full] [--no-gh]
       scripts/test-tier.sh --install-hook

  t0     ~5 min   build + Core/Cli/Avalonia tests + the static gates (doc
                  freshness, gate-asymmetry, redaction architecture, registries,
                  selftests). Pre-push: the installed hook runs exactly this.
  t1     ~20–25m  t0 + the redaction suites + Rendering (deterministic AND the
                  independent-oracle subsets with their floors) + parity ratchets
                  + skip budgets + the full Excise.App.Tests run. Merge gate.
  full   ≈3 h     t1 + every project chunked + the corpus scans + the release
                  smoke rows + the GRADE benches. exec's scripts/run-full-suite.sh
                  under caffeinate; resumable there (--fresh restarts; --only <re>
                  narrows; --allow-missing-corpora runs without a corpus).
  t2     ~30 min  release candidate — exec's scripts/release-smoke.sh --release-tests.
  t3              t2 on this machine, then the macOS-only note. Linux/Windows
                  packaging is a separate issue (LOCAL_GATES.md).

  --list [tier]   print the tier's rows from tests/gates.tsv and run nothing.
  --report        print the report for a run directory (default: the latest run)
                  without running anything: NEW vs KNOWN reds, SKIPPED
                  prerequisites, IMPROVE ratchets, GRADES vs the reference tools.
                  --full lists every row. --no-gh skips the open-issue check.
  --install-hook  install t0 as .git/hooks/pre-push and exit. Re-run once after
                  updating this script: the hook reads the push range on stdin.
  --resume        skip t0/t1 steps that already passed for this exact command
                  and tree (redaction rows never skip). full resumes by default.

Every gate is a row in tests/gates.tsv; LOCAL_GATES.md explains the columns.
EOF
}

if [ "$INSTALL_HOOK" = "1" ]; then
    HOOK="$ROOT/.git/hooks/pre-push"
    cat > "$HOOK" <<'HOOKEOF'
#!/usr/bin/env bash
# Installed by scripts/test-tier.sh --install-hook (#646).
#
# ONE job: run t0 before every push.
#
# It earns that. Today alone it blocked two pushes carrying unreviewed public
# API changes and one carrying a broken Excise.Avalonia test — each a real
# defect, caught before it left the machine.
#
# WHAT THIS HOOK USED TO ALSO DO, AND WHY IT NO LONGER DOES
#
# It refused any `v*` tag that was lightweight or lacked a Release-Evidence
# trailer, to force release tags through scripts/tag-release.sh. Removed
# because it guarded a path nothing had ever taken:
#
#   * v3.6.0, v3.7.0 and v3.8.0 all have ZERO Release-Evidence trailers —
#     every existing release tag was made the way the clause forbade.
#   * The clause never fired. No v* push has been attempted since it was
#     installed.
#   * It redirected to scripts/tag-release.sh, whose happy path has never
#     run (#968, closed as won't-do). So the only sanctioned route was an
#     unrehearsed script, and the guard's whole cost landed on someone
#     trying to tag a release.
#
# scripts/tag-release.sh has since been deleted outright, along with the
# Release-Evidence trailers it wrote. Tag by hand: `git tag -a vX.Y.Z`.
#
# THE PUSH RANGE. git feeds "<local ref> <local sha> <remote ref> <remote sha>"
# per pushed ref on stdin. The remote sha is the base the gate-asymmetry gate
# is defined over ("two pushes, not two commits", #618) — so it is exported
# as GATE_ASYMMETRY_BASE. An all-zero sha (a new remote branch) falls through
# to the runner's own base selection (LOCAL_GATES.md).
base=""
if [ ! -t 0 ]; then
    while read -r _lref _lsha _rref rsha; do
        case "$rsha" in *[!0]*) base="$rsha" ;; esac
    done
fi
[ -n "$base" ] && export GATE_ASYMMETRY_BASE="$base"
exec "$(git rev-parse --show-toplevel)/scripts/test-tier.sh" t0
HOOKEOF
    chmod +x "$HOOK"
    say "${G}Installed${N} $HOOK"
    [ -z "$TIER" ] && exit 0
fi

# --list: the tier's rows, straight from the manifest. Runs nothing.
list_gates() {
    local tier="$1" rows
    rows="$(runner_manifest_plan "$tier")" || return 2
    printf '%-32s %-8s %-15s %-6s %-5s %-28s %s\n' NAME CLASS KIND POLICY CKPT KNOWN-ISSUE PREREQ
    printf '%s\n' "$rows" | awk -F'\t' '{ printf "%-32s %-8s %-15s %-6s %-5s %-28s %s\n", $1, $5, $2, $8, $9, $6, $7 }'
    echo
    echo "$(printf '%s\n' "$rows" | grep -c .) rows in tier $tier — tests/gates.tsv $(runner_manifest_fingerprint)"
}

if [ "$LIST" = "1" ]; then
    list_gates "${TIER:-t0}"
    exit $?
fi
if [ "$REPORT" = "1" ]; then
    # shellcheck disable=SC2086
    exec scripts/report-gates.sh "${REPORT_DIR:---latest}" $REPORT_FULL $REPORT_GH
fi

case "$TIER" in
    t0|t1) ;;
    full)
        # The whole suite, chunked, memory-bounded and resumable. caffeinate
        # keeps the machine awake for the ≈3 h it takes.
        if command -v caffeinate >/dev/null 2>&1; then
            exec caffeinate -i scripts/run-full-suite.sh ${PASS_ARGS[@]+"${PASS_ARGS[@]}"}
        fi
        exec scripts/run-full-suite.sh ${PASS_ARGS[@]+"${PASS_ARGS[@]}"}
        ;;
    t2)
        # Branch explicitly rather than expanding a possibly-empty array: under
        # bash 3.2 (macOS /bin/bash) "${arr[@]:-}" on an empty array yields one
        # empty-string argument, which release-smoke.sh rejects as unknown.
        say "${B}[t2]${N} delegating to scripts/release-smoke.sh --release-tests"
        if [ "$RESUME" = "1" ]; then
            exec scripts/release-smoke.sh --release-tests --resume
        else
            exec scripts/release-smoke.sh --release-tests
        fi
        ;;
    t3)
        say "${B}[t3]${N} running t2 locally (this machine's platform only)"
        RS_OK=0
        if [ "$RESUME" = "1" ]; then
            scripts/release-smoke.sh --release-tests --resume || RS_OK=1
        else
            scripts/release-smoke.sh --release-tests || RS_OK=1
        fi
        say ""
        say "${Y}t3 is macOS only.${N} Linux/Windows packaging is a separate issue"
        say "(LOCAL_GATES.md); this script runs on one machine and cannot execute"
        say "another platform's job."
        exit $RS_OK
        ;;
    *) usage; exit 2 ;;
esac

TS="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/test-tier_${TIER}_$TS"
mkdir -p "$LOG_DIR"

# The environment every row may reference (tests/gates.tsv "target").
CONFIG="Debug"
RUNNER_BUILD_ARGS=""
RUNNER_OPTS=""
BLAME_HANG_TIMEOUT="${BLAME_HANG_TIMEOUT:-900000}"
export CONFIG LOG_DIR RUNNER_BUILD_ARGS RUNNER_OPTS BLAME_HANG_TIMEOUT
runner_identify_tree "$CONFIG"
if [ "$RESUME" = "1" ]; then
    runner_state_init "test-tier-$TIER" "$CONFIG"
    runner_export_lean_env
fi
runner_ledger_init "$LOG_DIR/ledger.jsonl"
runner_export_oracle_env
runner_export_release_env
GATE_ASYMMETRY_BASE="$(runner_gate_asymmetry_base "$TIER")"
export GATE_ASYMMETRY_BASE

# run_step <name> <kind> <target> <filter> <class> <knownIssue> <prereq> <policy> <ckpt>
# One row of the plan. The ledger row it writes carries the row's class and
# knownIssue, so the report can tell a NEW red from a KNOWN one without
# re-reading the manifest.
run_step() {
    local name="$1" kind="$2" target="$3" filter="$4" class="$5" known="$6" prereq="$7" policy="$8"
    local log="$LOG_DIR/$name.log" cmdline hash rc=0 dur start reason="" status frc=0

    cmdline="$(runner_step_cmdline "$name" "$kind" "$target" "$filter")"
    hash="$(runner_target_hash "$kind" "$target" "$filter")"

    # --resume: skip a row that already passed for this exact command and
    # tree. Rows declared checkpoint=never (the redaction family, build) are
    # never skipped — t1 accepts no flag that skips them.
    if [ "$RESUME" = "1" ] && ! runner_step_should_run "$name" "$hash"; then
        say "${B}[$name]${N} ${G}SKIP${N} - checkpointed"
        runner_ledger_record "$name" SKIP_CHECKPOINTED 0 0 "kind=$kind" "target=$target" "filter=$filter" \
            "class=$class" "knownIssue=$known" "prereq=$prereq" \
            "evidenceFrom=$(runner_marker_path "$name")" "evidenceFinished=$(runner_marker_value "$name" finished)" \
            "evidenceLog=$(runner_marker_value "$name" log)" "evidenceSha=$(runner_marker_value "$name" sha)"
        say ""
        return
    fi
    [ "$RESUME" = "1" ] && runner_mem_guard "$name"

    say "${B}[$name]${N} $cmdline"
    start="$(date +%s)"

    if reason="$(runner_prereq_missing "$prereq")"; then
        # The same verdict the exit-77 protocol gives, without paying for the run.
        rc="$RUNNER_EXIT_SKIP"
        printf 'SKIPPED: prerequisite missing: %s\n' "$reason" > "$log"
    else
        # Freshness: never eval a cell. Project/test rows are checked directly;
        # a script row is word-split (leading `env K=V` dropped, placeholders
        # expanded without a shell) so a `dotnet test --no-build` inside it is
        # still guarded.
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
            say ""
            return
        fi
        sh -c "$cmdline" > "$log" 2>&1
        rc=$?
    fi
    dur=$(( $(date +%s) - start ))

    status="$(runner_step_status "$kind" "$class" "$policy" "$rc" "$log" "$cmdline")"
    case "$status" in
        PASS)
            [ "$RESUME" = "1" ] && runner_step_mark "$name" "$rc" "$dur" "$hash" "$log"
            say "  ${G}PASS${N} (${dur}s) -> $log" ;;
        SKIPPED)
            reason="$(grep -E '^SKIP' "$log" | tail -1)"
            say "  ${Y}SKIPPED${N} (${dur}s) — $reason" ;;
        NO_RESULT)
            say "  ${Y}NO RESULT${N} (GRADE, rc=$rc) -> $log" ;;
        FAIL_ZERO_TESTS)
            say "  ${R}FAIL${N} (${dur}s) — ZERO tests executed; a vacuous pass is a failure"
            say "       filter: $filter" ;;
        *)
            say "  ${R}$status${N} rc=$rc (${dur}s) -> $log"
            tail -40 "$log" | sed 's/^/    /' ;;
    esac
    local trx=""
    [ -f "$LOG_DIR/$name.trx" ] && trx="$LOG_DIR/$name.trx"
    runner_ledger_record "$name" "$status" "$rc" "$dur" "kind=$kind" "target=$target" "filter=$filter" "log=$log" \
        "trx=$trx" "testsExecuted=${RUNNER_TESTS_EXECUTED:-}" "class=$class" "knownIssue=$known" "prereq=$prereq" "reason=$reason"
    say ""
}

# run_tier <t0|t1> — derive the plan from the manifest, expand the trx
# references, run every row in file order.
run_tier() {
    local plan="$LOG_DIR/plan.tsv" n
    runner_manifest_plan "$1" > "$plan.rows" || { echo "test-tier: tests/gates.tsv is defective; nothing ran." >&2; exit 2; }
    n="$(grep -c . "$plan.rows")"
    runner_plan_write "$plan" "$1" "$plan.rows" "$n" "$n" "-"
    rm -f "$plan.rows"
    runner_plan_expand_trx "$plan" "$LOG_DIR" || exit 2
    say "${B}[$1]${N} $n rows from tests/gates.tsv ($(runner_manifest_fingerprint)) @${RUNNER_SHA:0:12}$([ "$RUNNER_TREE_DIRTY" = yes ] && echo ' DIRTY') base=$GATE_ASYMMETRY_BASE"
    say "Logs: $LOG_DIR"
    say ""
    local name kind target filter class known prereq policy ckpt ratchet
    while IFS=$'\t' read -r name kind target filter class known prereq policy ckpt ratchet; do
        case "$name" in ''|'#'*) continue ;; esac
        run_step "$name" "$kind" "$target" "$filter" "$class" "$known" "$prereq" "$policy" "$ckpt"
    done < "$plan"
}

run_tier "$TIER"

# The report is the summary, and its exit code is this script's exit code:
# 0 clean (possibly with SKIPPED rows), 1 a NEW red or a STALE acceptance,
# 3 a row that never ran, 2 nothing to report.
scripts/report-gates.sh "$LOG_DIR"
rc=$?
if [ "$rc" = 0 ]; then
    runner_tier_base_record_chain "$TIER"
fi
exit $rc
