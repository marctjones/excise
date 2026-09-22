#!/usr/bin/env bash
# lib-runner.sh — crash-survivable checkpointing + memory guards for long runs.
#
# WHY THIS EXISTS
# ---------------
# On 2026-07-29 this machine took a kernel panic ("watchdog timeout: no
# checkins from watchdogd in 91 seconds", 17 swapfiles, LOW swap space) and
# killed five concurrent sessions mid-run. A ~30-minute release-tier run that
# has to restart from zero after every such event never finishes. This library
# makes a long run resumable and bounds what it does to the machine.
#
# It is deliberately agnostic about the panic's root cause. Checkpointing is
# the load-bearing part and it helps no matter WHY the run died (panic, Ctrl-C,
# closed laptop, OOM kill). The memory guards are cheap insurance layered on
# top, not the primary mechanism.
#
# THE CORRECTNESS RULE THAT MATTERS
# ---------------------------------
# A kernel panic loses buffered page-cache writes. A naive `echo done > mark`
# can leave a zero-length file whose metadata survived — which reads back as
# "this step passed" for a step that never ran. In this repo that is a false
# green on a redaction gate, i.e. the exact failure mode CLAUDE.md is written
# to prevent.
#
# So every marker is:
#   1. written to a .tmp file,
#   2. flushed with sync(8) BEFORE being published,
#   3. published by atomic rename,
#   4. validated on read: non-empty AND terminal sentinel present AND the
#      recorded commit matches HEAD.
# Any torn, truncated, or stale marker fails validation and the step RE-RUNS.
# The failure direction is always "do the work again", never "skip it".
#
# Usage:
#   source "$(dirname "$0")/lib-runner.sh"
#   runner_state_init "full-suite" "Release"
#   if runner_step_should_run "core-tests"; then
#       ... run it ...
#       runner_step_mark "core-tests" "$rc" "$dur"
#   fi

# ---------------------------------------------------------------------------
# Configuration (override via environment)
# ---------------------------------------------------------------------------

# Steps matching this regex are NEVER checkpointed — they re-run on every
# invocation even when a valid marker exists.
#
# CLAUDE.md: "t1's redaction test suites run unconditionally and there is no
# flag to skip them." A checkpoint that skips a redaction gate on resume IS
# that flag, so the redaction gates are excluded from resume by construction.
# They are also the cheapest gates relative to their blast radius.
RUNNER_NEVER_CHECKPOINT="${RUNNER_NEVER_CHECKPOINT:-redaction|true-redaction|glyph|extraction-parity}"

# Abort the run when the data volume has less headroom than this (GiB).
# macOS grows dynamic swap on the data volume; starving it is how a memory
# spike becomes a watchdog panic instead of an OOM kill.
RUNNER_MIN_FREE_GIB="${RUNNER_MIN_FREE_GIB:-20}"

# Memory-pressure gate. kern.memorystatus_vm_pressure_level: 1=normal,
# 2=warning, 4=critical. Wait rather than pile on when the machine is already
# under pressure.
RUNNER_MAX_PRESSURE="${RUNNER_MAX_PRESSURE:-2}"
RUNNER_PRESSURE_RETRIES="${RUNNER_PRESSURE_RETRIES:-10}"
RUNNER_PRESSURE_SLEEP="${RUNNER_PRESSURE_SLEEP:-30}"

# Per-testhost GC heap cap in GiB. DEFAULT 0 (off) — see the measurement note
# on runner_export_lean_env. Set e.g. RUNNER_HEAP_CAP_GIB=6 to install a
# runaway backstop: exceeding it raises OutOfMemoryException in that testhost,
# a clean re-runnable chunk failure instead of a machine-wide swap storm. Off by
# default because it is unproven here and can only *add* failure modes: measured
# peak RSS is ~450MB (Excise.Core.Tests) to ~700MB (an Excise.Rendering.Tests
# chunk), nowhere near any sane cap, and SkiaSharp's bitmaps are largely NATIVE
# allocations that a managed heap limit does not govern anyway.
RUNNER_HEAP_CAP_GIB="${RUNNER_HEAP_CAP_GIB:-0}"

# Opt-in GC tuning. DEFAULT 0 (off) because it was measured NOT to help.
RUNNER_TUNE_GC="${RUNNER_TUNE_GC:-0}"

# Distinct exit code for "aborted on resource guard", so a wrapper can tell
# "the machine was unsafe" apart from "a test failed".
RUNNER_EXIT_RESOURCE=75

RUNNER_SENTINEL="--CKPT-OK--"

RUNNER_STATE_DIR=""
RUNNER_SHA=""
RUNNER_TREE_DIRTY="unknown"
RUNNER_LABEL=""
RUNNER_CONFIG=""
RUNNER_SKIPPED_COUNT=0
RUNNER_STALE_SHA_COUNT=0

runner_say() { echo -e "$1"; }

# ---------------------------------------------------------------------------
# --no-build freshness guard
# ---------------------------------------------------------------------------

runner_command_has_arg() {
    local needle="$1"
    shift
    local arg
    for arg in "$@"; do
        [ "$arg" = "$needle" ] && return 0
    done
    return 1
}

runner_dotnet_configuration() {
    local config="Debug"
    while [ "$#" -gt 0 ]; do
        case "$1" in
            -c|--configuration)
                config="${2:-Debug}"
                shift 2
                ;;
            --configuration=*)
                config="${1#--configuration=}"
                shift
                ;;
            *)
                shift
                ;;
        esac
    done
    printf '%s\n' "$config"
}

runner_dotnet_targets_for_no_build() {
    local verb="$1"
    shift
    local skip_next=0
    local targets=()

    while [ "$#" -gt 0 ]; do
        if [ "$skip_next" = "1" ]; then
            skip_next=0
            shift
            continue
        fi

        case "$1" in
            --project)
                [ -n "${2:-}" ] && targets+=("$2")
                skip_next=1
                ;;
            -c|--configuration|--filter|--logger|--results-directory|--settings|--collect|--blame-hang-timeout)
                skip_next=1
                ;;
            --configuration=*|--filter=*|--logger=*|--results-directory=*|--settings=*|--collect=*|--blame-hang-timeout=*)
                ;;
            --*)
                ;;
            -*)
                ;;
            *)
                if [ "$verb" = "test" ] && [ "${#targets[@]}" -eq 0 ]; then
                    targets+=("$1")
                fi
                ;;
        esac
        shift
    done

    printf '%s\n' "${targets[@]}"
}

runner_assert_fresh_build() {
    local config="$1"
    shift
    if [ "${EXCISE_ALLOW_STALE_NO_BUILD:-0}" = "1" ]; then
        return 0
    fi
    "$PWD/scripts/assert-fresh.sh" --configuration "$config" "$@"
}

runner_guard_no_build_command() {
    [ "${1:-}" = "dotnet" ] || return 0
    [ "${2:-}" = "test" ] || [ "${2:-}" = "run" ] || return 0
    runner_command_has_arg "--no-build" "$@" || return 0

    local verb="$2"
    shift 2
    local config targets
    config="$(runner_dotnet_configuration "$@")"
    targets="$(runner_dotnet_targets_for_no_build "$verb" "$@")"

    if [ -n "$targets" ]; then
        # shellcheck disable=SC2086
        runner_assert_fresh_build "$config" $targets
    else
        runner_assert_fresh_build "$config"
    fi
}

# ---------------------------------------------------------------------------
# State directory
# ---------------------------------------------------------------------------

# runner_state_init <label> <config>
#
# The state key binds a resume to the exact tree it was started against:
# label + config + HEAD + dirty-ness. A different commit gets a different key,
# so you can never resume a run onto code it did not test.
runner_state_init() {
    RUNNER_LABEL="$1"
    RUNNER_CONFIG="${2:-Debug}"

    # Exported so the ledger can state it: a sha alone reads as "this commit"
    # when the run may have measured uncommitted changes on top of it (#994).
    runner_identify_tree "$RUNNER_CONFIG"
    local dirty=""
    [ "$RUNNER_TREE_DIRTY" = yes ] && dirty="-dirty"

    # #1027: the key is label + config + BRANCH + dirtiness — deliberately NOT
    # the commit. It used to include ${RUNNER_SHA:0:12}, which meant a commit
    # did not merely invalidate markers, it moved the whole run into a fresh
    # empty state directory. Combined with the per-marker sha check that is now
    # gone, a 90-minute suite could only ever finish by passing on the first
    # attempt with no commits during it — fix step 60, commit the fix, and the
    # 59 passing steps were not stale, they were unreachable. It never finished.
    #
    # Branch stays in the key so a resume cannot silently adopt another
    # branch's results, and dirtiness stays so uncommitted work cannot be
    # mistaken for a clean tree. The commit each step actually ran at is
    # recorded in the marker and reported by runner_marker_span.
    local branch
    branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null | tr -c 'A-Za-z0-9._-' '_' || echo nobranch)"
    local key="${RUNNER_LABEL}_${RUNNER_CONFIG}_${branch}${dirty}"
    RUNNER_STATE_DIR="${RUNNER_STATE_ROOT:-$PWD/logs/runner-state}/$key"
    mkdir -p "$RUNNER_STATE_DIR"

    # A dirty tree still gets a stable key (…-dirty) so an interrupted run on
    # uncommitted work is resumable — but the key cannot collide with the
    # clean-tree run of the same commit, and any commit changes the key.
    {
        echo "label=$RUNNER_LABEL"
        echo "config=$RUNNER_CONFIG"
        echo "sha=$RUNNER_SHA"
        echo "dirty=${dirty:-no}"
        echo "started=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        echo "host=$(hostname)"
    } >> "$RUNNER_STATE_DIR/meta"

    runner_say "State: $RUNNER_STATE_DIR"
    if [ -n "$dirty" ]; then
        runner_say "  (working tree is DIRTY — resume key is pinned to this dirty state)"
    fi
}

runner_state_dir() { echo "$RUNNER_STATE_DIR"; }

# Marker path for a step. Step names are slugified so a filter string used as
# a name cannot escape the state dir.
runner_marker_path() {
    local slug
    slug="$(printf '%s' "$1" | tr -c 'A-Za-z0-9._-' '_')"
    echo "$RUNNER_STATE_DIR/$slug.ckpt"
}

# The manifest's checkpoint column decides (tests/gates.tsv); the name regex
# stays only as a backstop for a step no manifest declares. A row is read
# through runner_manifest_field so Foo.chunkNN inherits Foo's answer.
runner_is_never_checkpointed() {
    local col=""
    [ -s "${RUNNER_MANIFEST:-}" ] && col="$(runner_manifest_field "$1" checkpoint 2>/dev/null)"
    case "$col" in never) return 0 ;; ok) return 1 ;; esac
    printf '%s' "$1" | grep -qiE "$RUNNER_NEVER_CHECKPOINT"
}

# ---------------------------------------------------------------------------
# Checkpoint read/write
# ---------------------------------------------------------------------------

# runner_step_mark <name> <rc> <duration_seconds> [target-hash] [log]
# Writes a marker ONLY for a passing step. A failure leaves no marker, so the
# step re-runs next time. The target hash says WHAT ran (kind|target|filter,
# runner_target_hash) so a row whose command changed re-runs (#1362 applied to
# markers); the log path lets a resumed run's report read the evidence.
runner_step_mark() {
    local name="$1" rc="$2" dur="${3:-0}" target="${4:-}" log="${5:-}"
    [ -n "$RUNNER_STATE_DIR" ] || return 0
    [ "$rc" = "0" ] || return 0

    local f tmp
    f="$(runner_marker_path "$name")"
    tmp="$f.tmp.$$"

    {
        echo "name=$name"
        echo "sha=$RUNNER_SHA"
        echo "config=$RUNNER_CONFIG"
        echo "rc=$rc"
        echo "duration=$dur"
        echo "finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        echo "target=$target"
        echo "log=$log"
        echo "$RUNNER_SENTINEL"
    } > "$tmp"

    # Flush content to stable storage BEFORE publishing the name. macOS dd has
    # no conv=fsync, so use sync(8) — it is coarse but this runs once per step,
    # not per test. If the panic lands between sync and rename we lose the
    # marker and re-run the step; that is the safe direction.
    sync
    mv -f "$tmp" "$f"
    sync
}

# runner_step_mark_known <name> <target-hash> <knownIssue> — checkpoint a
# FAILING step whose failure scripts/report_gates.py classified KNOWN (an
# OPEN cited issue whose qualifier matched the failure). Written as a POST-
# PASS by runner_checkpoint_known_failures, never by run_one itself: the
# KNOWN verdict only exists once report_gates.py has classified the whole
# run, so a single failing step cannot know at fail-time whether its own
# failure will end up accepted (#1371 — a step that only ever gets a marker
# on rc=0 re-runs its accepted failure on every --resume; measured at 2.5h
# for render-quality-scan).
runner_step_mark_known() {
    local name="$1" target="$2" known="$3" log="${4:-}"
    [ -n "$RUNNER_STATE_DIR" ] || return 0
    [ -n "$known" ] && [ "$known" != "-" ] || return 0

    local f tmp
    f="$(runner_marker_path "$name")"
    tmp="$f.tmp.$$"

    {
        echo "name=$name"
        echo "sha=$RUNNER_SHA"
        echo "config=$RUNNER_CONFIG"
        echo "status=KNOWN"
        echo "target=$target"
        echo "knownIssue=$known"
        echo "log=$log"
        echo "finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        echo "$RUNNER_SENTINEL"
    } > "$tmp"

    sync
    mv -f "$tmp" "$f"
    sync
}

# runner_ledger_row_fields <ledger.jsonl> <name> — "kind<TAB>target<TAB>filter"
# for the last ledger line recording that step (SKIP_CHECKPOINTED lines carry
# the same triple forward, so last-wins agrees with whatever actually ran).
runner_ledger_row_fields() {
    local ledger="$1" name="$2"
    [ -s "$ledger" ] || return 0
    awk -v want="$name" '
        function field(key,    re, s) {
            re = "\"" key "\":\"([^\"]*)\""
            if (match($0, re)) {
                s = substr($0, RSTART, RLENGTH)
                sub("^\"" key "\":\"", "", s); sub("\"$", "", s)
                return s
            }
            return ""
        }
        { if (field("name") == want) { k = field("kind"); t = field("target"); f = field("filter") } }
        END { printf "%s\t%s\t%s\n", k, t, f }
    ' "$ledger"
}

# runner_checkpoint_known_failures <log_dir> — call ONCE, immediately after
# `scripts/report-gates.sh "$log_dir"` has written <log_dir>/report.json.
# Walks the rows report-gates.sh classified KNOWN (a FAILING row whose
# knownIssue cite is an OPEN GitHub issue and whose qualifier matched — see
# report_gates.py classify_rows/_classify_failure) and checkpoints each one,
# so the NEXT --resume skips an accepted failure instead of re-running it.
# checkpoint=never rows are never marked: they must always re-run regardless
# of acceptance (the redaction gates' guarantee).
runner_checkpoint_known_failures() {
    local log_dir="$1" report="$1/report.json"
    [ -n "$RUNNER_STATE_DIR" ] || return 0
    [ -s "$report" ] || return 0

    local name known evlog
    while IFS=$'\t' read -r name known evlog; do
        [ -n "$name" ] || continue
        runner_is_never_checkpointed "$name" && continue
        [ -n "$known" ] && [ "$known" != "-" ] || continue

        local kind target filter
        IFS=$'\t' read -r kind target filter < <(runner_ledger_row_fields "$log_dir/ledger.jsonl" "$name")
        [ -n "$kind" ] || continue

        runner_step_mark_known "$name" "$(runner_target_hash "$kind" "$target" "$filter")" "$known" "$evlog"
    done < <(python3 -c '
import json, sys
try:
    report = json.load(open(sys.argv[1], encoding="utf-8"))
except (OSError, ValueError):
    sys.exit(0)
for row in report.get("rows", []):
    if row.get("verdict") == "KNOWN":
        print("%s\t%s\t%s" % (row["name"], row.get("knownIssue") or "-", row.get("log") or ""))
' "$report" 2>/dev/null)
}

# runner_step_should_run <name> [target-hash] — 0 (true) if the step must run.
runner_step_should_run() {
    local name="$1"
    [ -n "$RUNNER_STATE_DIR" ] || return 0

    if runner_is_never_checkpointed "$name"; then
        return 0
    fi

    local f
    f="$(runner_marker_path "$name")"

    # Every one of these checks failing means RE-RUN.
    [ -s "$f" ] || return 0                                    # missing or zero-length
    [ "$(tail -n 1 "$f" 2>/dev/null)" = "$RUNNER_SENTINEL" ] || return 0   # torn write

    # The marker says WHAT ran. A changed row re-runs; a marker without the
    # line (written before the manifest existed) re-runs once.
    if [ -n "${2:-}" ]; then
        local have
        have="$(sed -n 's/^target=//p' "$f" 2>/dev/null | head -1)"
        [ "$have" = "$2" ] || return 0
    fi

    # A KNOWN marker (an accepted failure, #1371 — see
    # runner_checkpoint_known_failures) carries two live checks a PASS marker
    # does not: the acceptance in tests/gates.tsv must still read exactly what
    # was accepted (a knownIssue cell that changed since — narrowed, widened,
    # or dropped — must re-run), and the cited issue must STILL be OPEN. Both
    # checks are read-only: no gh call here. report_gates.py's own cache
    # (logs/runner-state/known-issues/<N>.rec, refreshed by its IssueVerifier
    # the run BEFORE this one, same sentinel/state vocabulary it writes for
    # itself) is the single source of truth, so the runner never re-derives
    # "is #N open" on its own. Anything absent, torn, or answering anything but
    # OPEN means RE-RUN — checkpoints fail toward re-running, never skipping.
    local mstatus
    mstatus="$(sed -n 's/^status=//p' "$f" 2>/dev/null | head -1)"
    if [ "$mstatus" = "KNOWN" ]; then
        local mknown curknown n rec state
        mknown="$(sed -n 's/^knownIssue=//p' "$f" 2>/dev/null | head -1)"
        curknown="$(runner_manifest_field "$name" knownIssue 2>/dev/null)"
        [ -n "$curknown" ] && [ "$mknown" = "$curknown" ] || return 0

        n="$(printf '%s' "$mknown" | sed -n 's/^#\([0-9][0-9]*\).*/\1/p')"
        [ -n "$n" ] || return 0
        rec="$RUNNER_ROOT/logs/runner-state/known-issues/$n.rec"
        [ -s "$rec" ] || return 0
        [ "$(tail -n 1 "$rec" 2>/dev/null)" = "$RUNNER_SENTINEL" ] || return 0
        state="$(sed -n 's/^state=//p' "$rec" 2>/dev/null | head -1)"
        [ "$state" = "OPEN" ] || return 0
    fi

    # A marker from a DIFFERENT commit is still accepted, and this is a
    # deliberate reversal (#1027).
    #
    # The old rule required sha == HEAD. That made the suite unable to finish
    # by construction: a 90-minute run whose step 60 fails must be fixed, the
    # fix must be committed, and committing invalidated all 59 passing markers.
    # The only way to complete was to pass on the first attempt with zero
    # commits throughout. It never did.
    #
    # The rule was never load-bearing either — it has never caught a defect. It
    # was protecting against "this step passed on different code", which is a
    # real hazard, so the marker's own commit is RECORDED and the span is
    # REPORTED (runner_marker_span) rather than pretended away. A reader can see
    # exactly which steps ran at which commit and judge; a rule that forces a
    # restart instead gives them nothing to judge, because there is no run.
    #
    # What still re-runs unconditionally, and must: the redaction gates
    # (RUNNER_NEVER_CHECKPOINT), which is the guarantee that actually matters.
    local marker_sha
    marker_sha="$(sed -n 's/^sha=//p' "$f" 2>/dev/null | head -1)"
    if [ -n "$marker_sha" ] && [ "$marker_sha" != "$RUNNER_SHA" ]; then
        RUNNER_STALE_SHA_COUNT=$(( RUNNER_STALE_SHA_COUNT + 1 ))
    fi

    RUNNER_SKIPPED_COUNT=$(( RUNNER_SKIPPED_COUNT + 1 ))
    return 1
}

runner_skipped_count() { echo "$RUNNER_SKIPPED_COUNT"; }

# How many skipped steps were checkpointed at a DIFFERENT commit than HEAD.
# Zero means the whole run is at one commit; anything else must be reported,
# never silently accepted (#1027).
runner_stale_sha_count() { echo "$RUNNER_STALE_SHA_COUNT"; }

# The distinct commits the current checkpoint set spans, oldest recorded first.
runner_marker_span() {
    [ -n "$RUNNER_STATE_DIR" ] || return 0
    sed -n 's/^sha=//p' "$RUNNER_STATE_DIR"/*.ckpt 2>/dev/null | sort -u
}

# ---------------------------------------------------------------------------
# Run ledger (#994)
# ---------------------------------------------------------------------------
# The markers above are the ENFORCEMENT channel: sha-keyed, torn-write-safe,
# durable across invocations, and deliberately minimal. They answer "may this
# step be skipped on resume?" and nothing else.
#
# They cannot answer the question #994 is about — "did everything that claims
# to gate this build actually run, and against real inputs?" — because a marker
# is written only for a pass and records nothing about what the step consumed.
# That answer existed in scattered places (console output, RESULTS, summary.tsv,
# resources.tsv, per-gate stdout) and nothing collected it.
#
# The ledger is the human-and-tool-readable record of one invocation: one JSON
# object per step, including steps that were SKIPPED as already-checkpointed,
# with the provenance of the marker that skipped them. It is WRITE-ONLY here —
# deliberately no consumer yet. Markers stay the enforcement channel; making
# release evidence depend on a per-run artifact under a timestamped log
# directory would need a durable cross-invocation store, which is what the
# markers already are.
#
# runner_ledger_init <path>
runner_ledger_init() {
    RUNNER_LEDGER="${1:-}"
    [ -n "$RUNNER_LEDGER" ] || return 0
    : > "$RUNNER_LEDGER"
}

# runner_ledger_record <name> <status> <rc> <duration> [key=value ...]
# Values are recorded as JSON strings; no key or value may contain a newline.
runner_ledger_record() {
    [ -n "${RUNNER_LEDGER:-}" ] || return 0
    local name="$1" status="$2" rc="$3" dur="$4"
    shift 4

    # Escape for JSON: backslash first, then quote, then strip control chars.
    _ledger_esc() {
        printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g' -e 's/[[:cntrl:]]//g'
    }

    {
        printf '{"name":"%s","status":"%s","rc":%s,"durationSeconds":%s' \
            "$(_ledger_esc "$name")" "$(_ledger_esc "$status")" \
            "${rc:-0}" "${dur:-0}"
        printf ',"sha":"%s","treeDirty":"%s","config":"%s","recorded":"%s"' \
            "$(_ledger_esc "${RUNNER_SHA:-}")" "$(_ledger_esc "${RUNNER_TREE_DIRTY:-unknown}")" \
            "$(_ledger_esc "${RUNNER_CONFIG:-}")" \
            "$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        local kv k v
        for kv in "$@"; do
            k="${kv%%=*}"; v="${kv#*=}"
            [ -n "$k" ] || continue
            printf ',"%s":"%s"' "$(_ledger_esc "$k")" "$(_ledger_esc "$v")"
        done
        printf '}\n'
    } >> "$RUNNER_LEDGER"
}

# Drop all markers — used by --fresh.
runner_state_reset() {
    [ -n "$RUNNER_STATE_DIR" ] || return 0
    rm -f "$RUNNER_STATE_DIR"/*.ckpt 2>/dev/null || true
    runner_say "Checkpoints cleared: $RUNNER_STATE_DIR"
}

# ---------------------------------------------------------------------------
# Memory / disk guard
# ---------------------------------------------------------------------------

runner_pressure_level() {
    sysctl -n kern.memorystatus_vm_pressure_level 2>/dev/null || echo 1
}

runner_swap_used_gib() {
    # vm.swapusage reads "total = 0.00M used = 0.00M free = 0.00M" right after
    # a boot because macOS grows swap lazily. "Not yet grown" is normal, not an
    # error — we only ever report this number, never gate on it.
    sysctl -n vm.swapusage 2>/dev/null \
        | sed -n 's/.*used = \([0-9.]*\)M.*/\1/p' \
        | awk '{ printf "%.1f", $1/1024 }'
}

runner_free_gib() {
    { df -k /System/Volumes/Data 2>/dev/null || df -k /; } \
        | awk 'NR==2 { printf "%.0f", $4/1024/1024 }'
}

runner_resource_report() {
    local p s f
    p="$(runner_pressure_level)"; s="$(runner_swap_used_gib)"; f="$(runner_free_gib)"
    echo "pressure=$p swap_used=${s:-0}GiB free_disk=${f:-?}GiB"
}

# runner_mem_guard [context]
# Waits out transient pressure; aborts the whole run (exit
# $RUNNER_EXIT_RESOURCE) if the machine stays unsafe. A clean abort with
# checkpoints intact is strictly better than a panic that loses the run.
runner_mem_guard() {
    local ctx="${1:-}"
    local tries=0

    while :; do
        local free pressure
        free="$(runner_free_gib)"
        pressure="$(runner_pressure_level)"

        if [ -n "$free" ] && [ "$free" -lt "$RUNNER_MIN_FREE_GIB" ] 2>/dev/null; then
            runner_say "ABORT: only ${free}GiB free on the data volume (need ${RUNNER_MIN_FREE_GIB}GiB)."
            runner_say "  macOS grows swap here; without headroom a memory spike panics the box"
            runner_say "  instead of failing a test. Free space, then resume — checkpoints are kept."
            exit "$RUNNER_EXIT_RESOURCE"
        fi

        if [ -n "$pressure" ] && [ "$pressure" -le "$RUNNER_MAX_PRESSURE" ] 2>/dev/null; then
            return 0
        fi

        tries=$(( tries + 1 ))
        if [ "$tries" -gt "$RUNNER_PRESSURE_RETRIES" ]; then
            runner_say "ABORT: memory pressure stayed at $pressure after $tries checks${ctx:+ (before $ctx)}."
            runner_say "  Resume when the machine is idle — checkpoints are kept."
            exit "$RUNNER_EXIT_RESOURCE"
        fi
        runner_say "  memory pressure $pressure — waiting ${RUNNER_PRESSURE_SLEEP}s ($tries/$RUNNER_PRESSURE_RETRIES)${ctx:+ before $ctx}"
        sleep "$RUNNER_PRESSURE_SLEEP"
    done
}

# ---------------------------------------------------------------------------
# Memory-frugal dotnet environment
# ---------------------------------------------------------------------------

# MEASURED, NOT ASSUMED (2026-07-29, Excise.Core.Tests, 3840 tests, macOS/10 cores):
#
#   stock env                                   446 MB peak RSS, 42.5s
#   DOTNET_gcServer=0 + GCConserveMemory=5
#     + GCRetainVM=0 + 6GiB heap cap            552 MB peak RSS, 39.4s
#
# The GC tuning made peak memory ~24% WORSE, not better. `dotnet test`'s
# testhost already runs Workstation GC by default here, so gcServer=0 is a
# no-op, and GCConserveMemory/gen0size tuning cost more than it saved. The
# earlier claim that Server GC's per-core heaps were the problem was wrong for
# this repo — a single testhost peaks at ~450MB (Core) to ~700MB (a Rendering
# chunk), which is not what took the machine down.
#
# What actually bounds memory here is structural, not a GC flag:
#   * exactly ONE dotnet process at a time (this runner is strictly serial),
#   * short-lived testhosts (chunking), so nothing accumulates across a 30m run,
#   * the pressure/disk guard, which refuses to pile onto a machine already in
#     trouble instead of contributing to a swap death spiral.
#
# So the GC knobs are OFF by default and opt-in via RUNNER_TUNE_GC=1. Kept
# rather than deleted so the measurement above is reproducible.
runner_export_lean_env() {
    if [ "${RUNNER_TUNE_GC:-0}" = "1" ]; then
        export DOTNET_gcServer=0
        export DOTNET_GCConserveMemory=5
        export DOTNET_GCRetainVM=0
    fi

    if [ "${RUNNER_HEAP_CAP_GIB:-0}" != "0" ]; then
        # DOTNET_GCHeapHardLimit is HEX bytes.
        export DOTNET_GCHeapHardLimit
        DOTNET_GCHeapHardLimit="$(printf '%x' $(( RUNNER_HEAP_CAP_GIB * 1024 * 1024 * 1024 )))"
    fi

    # Don't leave msbuild/Roslyn server processes resident between steps. This
    # one is uncontroversial and cheap.
    export MSBUILDDISABLENODEREUSE=1
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
}

# Release build-server memory between phases.
runner_reclaim() {
    dotnet build-server shutdown >/dev/null 2>&1 || true
}

# ---------------------------------------------------------------------------
# The gate manifest — tests/gates.tsv (LOCAL_GATES.md)
# ---------------------------------------------------------------------------
# Every runner derives its plan from the manifest through runner_manifest_plan
# <tier>; none holds a step list of its own. The loader VALIDATES before it
# projects and returns 2 on any defect, so a runner never executes a plan the
# manifest could not describe. Chain semantics: t0 ⊂ t1 ⊂ full; t2 only when
# listed. File order is execution order.
RUNNER_EXIT_SKIP=77          # a gate's "prerequisite missing" — never 0. prereqPolicy decides what it means.
RUNNER_EXIT_BOUND=124        # run-bounded.sh: the row exceeded its wall-clock budget (#1283). GNU timeout's code.

# --blame-hang-timeout, in ms: how long NO test event may pass before blame
# collects and kills. THE one source of truth -- until 2026-09-16 each of the
# three runners carried its own `${BLAME_HANG_TIMEOUT:-900000}` and exported
# it, so the library default below could never win and the 60000 claimed in
# d1e21572 was dead code. Set it here; the runners only pass it through.
#
# ⚠️ 900s, and 60s is WRONG -- reverted 2026-09-16 the same night it landed,
# after it aborted three healthy rows on the merge gate (Excise.Core.Tests,
# redaction-suites, and test-count-core downstream of the first). #1283 lane B
# set 60s on a real measurement -- across three healthy unfiltered App.Tests
# runs the worst gap between consecutive test events was 1.8s over 4455 gaps,
# none above 10s -- and the measurement does not generalise, because
# **blame's timer cannot tell a stalled worker from ONE long-running test**.
# The constraint is therefore the LONGEST LEGITIMATE SINGLE TEST, not the mean
# inter-event gap. Measured over every trx in logs/ (12 runs, several predating
# tonight): single tests at 81-277s complete normally, led by
# CorpusConformanceTests.Corpus_ParsesWithoutCrash_AllPdfs (277s) and
# ReferenceRedactorComparisonTests (271s). Even App.Tests has an 89s test in a
# run lane B did not sample. 277s x the 2-3x load factor CLAUDE.md documents
# exceeds 600s, so 900s is the floor a default can safely take: ~3.2x over the
# worst observed.
#
# Lane B sampled the one project with no corpus-wide tests -- AND sampled it
# from a tree where the corpus rows collected nothing (#1527, fixed hours
# earlier the same night). A margin measured on the wrong population reads
# exactly like a margin.
#
# A tight timeout is still right PER ROW, in (longest single test, budget);
# that is #1541, not a global constant.
#
# This is DIAGNOSTICS, not the remedy. Measured 2026-09-16: on a STALLED
# worker blame fires exactly on schedule, dumps the testhost RELAY instead of
# the worker, and the run keeps going -- 6x past the timeout in the
# reproduction, nine hours in the real 2026-09-10 incident. The `budget`
# column and run-bounded.sh are what actually END a hung row. Do not remove
# the bound on the grounds that blame exists -- and note this cuts FOR a
# generous blame timeout: shortening it buys no containment, only an earlier
# dump of the wrong process, at the price of killing healthy long tests.
RUNNER_BLAME_HANG_DEFAULT=900000
BLAME_HANG_TIMEOUT="${BLAME_HANG_TIMEOUT:-$RUNNER_BLAME_HANG_DEFAULT}"
RUNNER_ROOT="${RUNNER_ROOT:-$PWD}"
RUNNER_MANIFEST="${RUNNER_MANIFEST:-$RUNNER_ROOT/tests/gates.tsv}"
RUNNER_MANIFEST_HEADER=$'name\tclass\ttiers\tkind\ttarget\tfilter\tratchet\tknownIssue\tprereq\tprereqPolicy\tcheckpoint\toracle\tbudget\tnote'
RUNNER_TESTS_EXECUTED=""

runner_manifest_fingerprint() { shasum -a 256 "$RUNNER_MANIFEST" | cut -c1-16; }

# runner_identify_tree [config] — RUNNER_SHA / RUNNER_TREE_DIRTY / RUNNER_CONFIG
# without creating a state directory (runner_state_init builds on it).
runner_identify_tree() {
    RUNNER_CONFIG="${1:-${RUNNER_CONFIG:-Debug}}"
    RUNNER_SHA="$(git rev-parse HEAD 2>/dev/null || echo nogit)"
    if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
        RUNNER_TREE_DIRTY=yes
    else
        RUNNER_TREE_DIRTY=no
    fi
}

# runner_manifest_plan <tier> — validate the manifest, then print the tier's
# rows as 10 tab-separated columns:
#   name kind target filter class knownIssue prereq prereqPolicy checkpoint ratchet
# Returns 2 on any defect (every defect is printed, file:line, to stderr).
runner_manifest_plan() {
    local tier="$1" f="$RUNNER_MANIFEST"
    [ -s "$f" ] || { echo "runner: manifest missing or empty: $f" >&2; return 2; }
    case "$tier" in t0|t1|full|t2) ;; *) echo "runner: unknown tier '$tier'" >&2; return 2 ;; esac
    awk -F'\t' -v tier="$tier" -v root="$RUNNER_ROOT" -v hdr="$RUNNER_MANIFEST_HEADER" \
        -v never="$(printf '%s' "$RUNNER_NEVER_CHECKPOINT" | tr 'A-Z' 'a-z')" '
    function bad(m) { printf "%s:%d: %s\n", FILENAME, NR, m > "/dev/stderr"; ok = 0 }
    function rank(t) { return t == "t0" ? 0 : t == "t1" ? 1 : t == "full" ? 2 : -1 }
    function selected(tiers, want,    n, i, ts) {
        n = split(tiers, ts, ",")
        for (i = 1; i <= n; i++) {
            if (ts[i] == want) return 1
            if (rank(want) >= 0 && rank(ts[i]) >= 0 && rank(ts[i]) <= rank(want)) return 1
        }
        return 0
    }
    function exists(p) { return system("test -e \"" root "/" p "\"") == 0 }
    BEGIN { ok = 1; n = 0 }
    /^#/ || /^[ \t]*$/ { next }
    !seen { seen = 1; if ($0 != hdr) bad("header must be exactly: " hdr); next }
    {
        if (NF != 14) { bad("expected 14 columns, got " NF); next }
        name = $1; class = $2; tiers = $3; kind = $4; target = $5; filter = $6; ratchet = $7
        known = $8; prereq = $9; policy = $10; ckpt = $11; oracle = $12; budget = $13; note = $14
        if (name !~ /^[A-Za-z0-9][A-Za-z0-9._-]*$/) bad("name must be a slug: " name)
        if (name in names) bad("duplicate name " name)
        names[name] = NR
        if (class !~ /^(BLOCK|IMPROVE|GRADE|SELFTEST)$/) bad("class " class)
        if (kind !~ /^(script|test|project|project-chunked|fn)$/) bad("kind " kind)
        if (tiers !~ /^(t0|t1|full|t2)(,(t0|t1|full|t2))*$/) bad("tiers " tiers)
        if (known !~ /^(#[0-9]+(\/[^\t]+)?|-)$/) bad("knownIssue " known)
        if (policy !~ /^(fail|skip)$/) bad("prereqPolicy " policy)
        if (ckpt !~ /^(ok|never)$/) bad("checkpoint " ckpt)
        if (oracle !~ /^(independent|spec|self|none|na)$/) bad("oracle " oracle)
        # budget: wall-clock seconds, or - for unbounded (prior behaviour).
        # Only rows the runner turns into a dotnet test can carry one; a
        # script row is its own process tree and bounds itself (#1283).
        # NOTE: no apostrophes or backticks in this awk program -- it is inside
        # a single-quoted shell string and an apostrophe terminates it.
        if (budget !~ /^(-|[1-9][0-9]*)$/) bad("budget must be seconds or '-': " budget)
        if (budget != "-" && kind !~ /^(test|project|project-chunked)$/) bad("only dotnet-test rows carry a budget; " kind " rows bound themselves")
        if (budget != "-" && budget + 0 < 60) bad("budget under 60s will false-fire; measured worst inter-test gap is 1.8s but startup is not free: " budget)
        if (note == "-" || note == "") bad("every row carries a note")
        if (kind == "fn" && tiers != "t2") bad("fn rows are release-smoke (t2) only")
        if (kind == "fn" && target !~ /^run_[a-z_]+_gate$/) bad("fn target must be a run_*_gate function")
        if ((kind == "script" || kind == "fn" || kind == "project" || kind == "project-chunked") && filter != "-") bad("only test rows carry a filter")
        if (kind == "test" && filter == "-") bad("test rows need a filter (kind=project for a whole csproj)")
        if (class == "IMPROVE" && ratchet == "-") bad("IMPROVE rows name their ratchet")
        if (ratchet != "-" && !exists(ratchet)) bad("ratchet missing: " ratchet)
        if (tolower(name) ~ never && ckpt == "ok" && class != "GRADE") bad("name matches RUNNER_NEVER_CHECKPOINT; only a GRADE row may be checkpoint=ok")
        if (kind == "script") { split(target, w, " "); if (w[1] ~ /^scripts\// && system("test -x \"" root "/" w[1] "\"") != 0) bad("not executable: " w[1]) }
        if (kind ~ /^(test|project|project-chunked)$/ && !exists(target)) bad("target missing: " target)
        s = target
        while (match(s, /\{TRX(ARGS)?\??:[A-Za-z0-9._-]+\}/)) {
            ref = substr(s, RSTART, RLENGTH); s = substr(s, RSTART + RLENGTH)
            p = ref; sub(/^\{TRX(ARGS)?\??:/, "", p); sub(/\}$/, "", p)
            if (!(p in names)) bad(ref " references " p ", which is not an EARLIER row")
            else if (ref ~ /^\{TRX:/ && kinds[p] != "test" && kinds[p] != "project") bad(ref " needs one unchunked trx; " p " is " kinds[p])
            else if (ref !~ /\?:/) { m = split(tiers, tt, ","); for (i = 1; i <= m; i++) if (!selected(rowtiers[p], tt[i])) bad(ref " has no producer in tier " tt[i]) }
        }
        kinds[name] = kind; rowtiers[name] = tiers
        if (selected(tiers, tier)) rows[++n] = name "\t" kind "\t" target "\t" filter "\t" class "\t" known "\t" prereq "\t" policy "\t" ckpt "\t" ratchet
    }
    END {
        if (!seen) { print "manifest has no header" > "/dev/stderr"; exit 2 }
        if (!ok) exit 2
        for (i = 1; i <= n; i++) print rows[i]
    }' "$f"
}

# runner_manifest_field <name> <column> — one cell. Foo.chunkNN resolves to Foo.
runner_manifest_field() {
    local name="$1" col="$2"
    case "$name" in *.chunk[0-9][0-9]) name="${name%.chunk[0-9][0-9]}" ;; esac
    awk -F'\t' -v n="$name" -v c="$col" '
        /^#/ || /^[ \t]*$/ { next }
        !h { h = 1; for (i = 1; i <= NF; i++) idx[$i] = i; next }
        $1 == n { print $(idx[c]); exit }' "$RUNNER_MANIFEST"
}

# runner_plan_write <plan.tsv> <tier> <rows-file> <planned> <of> <only-pattern>
# Header line FIRST, then the rows (no sed -i; a plan is written once).
runner_plan_write() {
    {
        printf '# tier=%s planned=%s of=%s only=%s manifest=%s\n' "$2" "$4" "$5" "${6:--}" "$(runner_manifest_fingerprint)"
        cat "$3"
    } > "$1"
}

# ---------------------------------------------------------------------------
# Plan-time trx expansion, the one command-line builder, the zero-tests
# guard, content-keyed markers (moved here from run-full-suite.sh run_one so
# all three runners share ONE implementation)
# ---------------------------------------------------------------------------

# runner_plan_expand_trx <plan.tsv> <log_dir> — call LAST, after --only
# filtering and chunk expansion. {TRX:x} → $LOG_DIR/x.trx (x unchunked);
# {TRXARGS:x} → "--trx $LOG_DIR/x.trx" or the chunk union; {TRXARGS?:x} →
# the same, or nothing when x is not in this plan.
runner_plan_expand_trx() {
    local plan="$1" L="$2" tmp="$1.expand.$$" map="$1.trxmap.$$"
    # Where each producer's trx WILL be: this run's LOG_DIR when the row runs;
    # beside the evidence log in the earlier run directory when --resume takes
    # the row from a checkpoint. The consumer used to assume LOG_DIR and read
    # "no trx ... cannot tell which tests reported" on every resume whose
    # producer was checkpointed (test-count-core/cli/avalonia, 2026-09-05).
    # Rows whose evidence trx is gone fall back to LOG_DIR and fail loudly.
    : > "$map"
    local name kind target filter rest hash evlog evtrx
    while IFS=$'\t' read -r name kind target filter rest; do
        [ -n "$name" ] && [ "${name#\#}" = "$name" ] || continue
        case "$kind" in test|project|project-chunked) ;; *) continue ;; esac
        evtrx="$L/$name.trx"
        hash="$(runner_target_hash "$kind" "$target" "$filter")"
        if ! runner_step_should_run "$name" "$hash"; then
            evlog="$(runner_marker_value "$name" log)"
            if [ -n "$evlog" ] && [ -f "${evlog%.log}.trx" ]; then evtrx="${evlog%.log}.trx"; fi
        fi
        printf '%s\t%s\n' "$name" "$evtrx" >> "$map"
    done < "$plan"
    awk -F'\t' -v OFS='\t' -v L="$L" -v M="$map" '
    FILENAME == M { trx[$1] = $2; next }
    FNR == 1 && FILENAME != M { pass++ }
    pass == 1 {
        if ($0 !~ /^#/ && NF) {
            present[$1] = 1
            if ($1 ~ /\.chunk[0-9][0-9]$/) { b = $1; sub(/\.chunk[0-9][0-9]$/, "", b); chunks[b] = chunks[b] " --trx " (($1 in trx) ? trx[$1] : L "/" $1 ".trx") }
        }
        next
    }
    /^#/ || !NF { print; next }
    {
        t = $3
        while (match(t, /\{TRX(ARGS)?\??:[A-Za-z0-9._-]+\}/)) {
            ref = substr(t, RSTART, RLENGTH); p = ref; sub(/^\{TRX(ARGS)?\??:/, "", p); sub(/\}$/, "", p)
            if (ref ~ /^\{TRX:/)  { if (p in present) rep = ((p in trx) ? trx[p] : L "/" p ".trx"); else { printf "plan: %s needs the trx of %s, which is not in this plan (with --only, include it: --only \"%s|%s\")\n", $1, p, p, $1 > "/dev/stderr"; bad = 1; rep = "" } }
            else if (p in present) rep = "--trx " ((p in trx) ? trx[p] : L "/" p ".trx")
            else if (p in chunks)  rep = substr(chunks[p], 2)
            else if (ref ~ /\?:/)  rep = ""
            else { printf "plan: %s needs the trx of %s, which is not in this plan (with --only, include it: --only \"%s|%s\")\n", $1, p, p, $1 > "/dev/stderr"; bad = 1; rep = "" }
            t = substr(t, 1, RSTART - 1) rep substr(t, RSTART + RLENGTH)
        }
        $3 = t; print
    }
    END { if (bad) exit 2 }' "$map" "$plan" "$plan" > "$tmp" || { rm -f "$tmp" "$map"; return 2; }
    rm -f "$map"
    mv -f "$tmp" "$plan"
}

# runner_step_cmdline <name> <kind> <target> <filter> — the ONE place a row
# becomes a command. Unfiltered project rows emit a trx: check-test-count.sh
# (#894) only accepts an unfiltered trx by construction.
#
# A solution-wide target (excise.sln) runs one vstest invocation PER PROJECT
# under the hood; a single fixed --logger trx;LogFileName= is one file every
# project overwrites in turn, so only the last project's results survive
# (#1368: measured 1121 of 1522 results kept from a 2026-09-05 run). Give a
# .sln target its own results directory instead and let vstest auto-name each
# project's trx inside it -- one file per project, nothing overwritten. Not
# done for project/project-chunked targets: those already name a single
# project, so the collision this guards against cannot happen there, and
# check-test-count.sh's --no-build freshness/consumer code expects the exact
# $LOG_DIR/$name.trx path for those kinds.
#
# BOTH dotnet-test branches carry the wall-clock bound, not just `project`:
# the row that was mid-flight while #1283 was being diagnosed was a `test` row
# in --filter/--results-directory mode, and a stall there is just as unbounded.
# A `budget` of `-` emits the UNWRAPPED command — byte-for-byte today's
# behaviour, and not even an extra process in the tree (#1187).
#
# --blame-hang-timeout defaults to 900s. A 60s default was tried on
# 2026-09-16 and aborted three healthy rows within hours; blame's timer cannot
# distinguish a stalled worker from one long-running test, and single tests
# here run to 277s. See RUNNER_BLAME_HANG_DEFAULT above for the measurement.
# Blame's Sequence file — which names the tests in flight — is only written on
# the worker-DEATH path. It is NOT the thing that ends a stalled run;
# run-bounded.sh is. Blame fires on time and the run hangs anyway.
runner_step_cmdline() {
    local name="$1" kind="$2" target="$3" filter="${4:--}" hang="${BLAME_HANG_TIMEOUT:-$RUNNER_BLAME_HANG_DEFAULT}"
    local budget bound=""
    case "$kind" in
        test|project|project-chunked)
            budget="$(runner_manifest_field "$name" budget 2>/dev/null)"
            case "${budget:--}" in
                -|"") bound="" ;;
                *) bound="$(printf 'scripts/run-bounded.sh %s "%s" "%s" ' "$budget" "$LOG_DIR" "$name")" ;;
            esac ;;
    esac
    case "$kind" in
        script|fn) printf '%s\n' "$target" ;;
        project|project-chunked)
            printf '%sdotnet test "%s" --no-build -c "%s" --blame-hang-timeout %s --logger "console;verbosity=minimal" --logger "trx;LogFileName=%s/%s.trx"\n' \
                "$bound" "$target" "$CONFIG" "$hang" "$LOG_DIR" "$name" ;;
        test)
            if [ "${target%.sln}" != "$target" ]; then
                printf '%sdotnet test "%s" --no-build -c "%s" --filter "%s" --blame-hang-timeout %s --logger "console;verbosity=minimal" --logger "trx" --results-directory "%s/%s"\n' \
                    "$bound" "$target" "$CONFIG" "$filter" "$hang" "$LOG_DIR" "$name"
            else
                printf '%sdotnet test "%s" --no-build -c "%s" --filter "%s" --blame-hang-timeout %s --logger "console;verbosity=minimal" --logger "trx;LogFileName=%s/%s.trx"\n' \
                    "$bound" "$target" "$CONFIG" "$filter" "$hang" "$LOG_DIR" "$name"
            fi ;;
    esac
}

# runner_expand_placeholders <text> — the six documented environment
# placeholders, expanded WITHOUT eval (used only to inspect a command line;
# the run itself goes through sh -c, which expands from the environment).
runner_expand_placeholders() {
    local t="$1"
    t="${t//\$CONFIG/${CONFIG:-}}"; t="${t//\$LOG_DIR/${LOG_DIR:-}}"
    t="${t//\$GATE_ASYMMETRY_BASE/${GATE_ASYMMETRY_BASE:-}}"; t="${t//\$RELEASE_VERSION/${RELEASE_VERSION:-}}"
    t="${t//\$AOT_EXTRA_ARGS/${AOT_EXTRA_ARGS:-}}"; t="${t//\$RUNNER_BUILD_ARGS/${RUNNER_BUILD_ARGS:-}}"
    printf '%s\n' "$t"
}

# runner_zero_tests_executed <log> — true when a dotnet-test command executed
# ZERO tests (#941). The signal is the executed count, NOT the presence of "No
# test matches": a solution-wide filter legitimately prints that line for every
# assembly holding none of the targeted tests. Sets RUNNER_TESTS_EXECUTED.
runner_zero_tests_executed() {
    local executed
    executed="$(grep -oE 'Total: *[0-9]+' "$1" 2>/dev/null | grep -oE '[0-9]+' | awk '{s+=$1} END {print s+0}')"
    [ "${executed:-0}" = "0" ] && executed="$(grep -oE 'Total tests: *[0-9]+' "$1" 2>/dev/null | grep -oE '[0-9]+' | awk '{s+=$1} END {print s+0}')"
    RUNNER_TESTS_EXECUTED="${executed:-0}"
    [ "${executed:-0}" = "0" ]
}

# runner_fixture_fingerprint — hash of HOW tests find their fixtures.
#
# #1527: a checkpoint marker validates a hash of the row's command (kind,
# target, filter), and none of those change when fixture RESOLUTION changes.
# So a resumed run could skip a corpus row on a marker written when the row
# collected 88 rows instead of 101 — a vacuous green inherited by a row that
# is not itself a redaction gate (those are checkpoint=never and re-run
# regardless, which is the design working). Folding the locator and the
# collected-row floor registry into the hash makes every marker invalid the
# moment either changes, which is the only point at which the count can be
# known to have moved. Cheap: two small files, cached per process.
runner_fixture_fingerprint() {
    if [ -z "${RUNNER_FIXTURE_FP:-}" ]; then
        # RUNNER_ROOT may be unset when only part of this library is sourced
        # (scripts/test-runner-plan-expand.sh does that); an absent pair of
        # files hashes to a stable value, so the fingerprint degrades to a
        # constant rather than breaking the hash under `set -u`.
        local _r="${RUNNER_ROOT:-.}"
        RUNNER_FIXTURE_FP="$(
            cat "$_r/Excise.Core.Tests/TestSupport/TestRepoLayout.cs" \
                "$_r/Excise.Rendering.Tests/Differential/CorpusRowFloorGateTests.cs" \
                2>/dev/null | shasum -a 256 | cut -c1-16
        )"
    fi
    printf '%s' "$RUNNER_FIXTURE_FP"
}

# runner_target_hash <kind> <target> <filter>
runner_target_hash() {
    printf '%s|%s|%s|%s' "$1" "$2" "$3" "$(runner_fixture_fingerprint)" | shasum -a 256 | cut -c1-16
}

# runner_marker_value <name> <key> — one line of a step's marker, or nothing.
runner_marker_value() {
    [ -n "${RUNNER_STATE_DIR:-}" ] || return 0
    sed -n "s/^$2=//p" "$(runner_marker_path "$1")" 2>/dev/null | head -1
}

# ---------------------------------------------------------------------------
# Prerequisites — the [requires:]/prereq vocabulary gates.tsv rows use for
# their opt:/tool:/corpus:/env:/file: column, resolved the same way for
# every runner and gate.
#   tool:NAME    on PATH          corpus:NAME  test-pdfs/NAME non-empty
#   env:NAME     variable set     file:GLOB    a repo-relative glob matches
#   opt:NAME     the runner was invoked with --NAME (RUNNER_OPTS)
# A typo resolves ABSENT on purpose. RUNNER_FORCE_ABSENT forces a spec absent
# for selftests. (Originally introduced for the skip-budget gate's own
# [requires: ...] allowlist markers, #854 — that allowlist is gone since
# #1172, but this resolver is now general-purpose infrastructure other
# gates/runners depend on; see runner_prereq_missing's callers.)
# ---------------------------------------------------------------------------
RUNNER_PREREQ_CACHE=""
runner_prereq_present() {
    local spec="$1"
    case ",${RUNNER_FORCE_ABSENT:-}," in *",$spec,"*) return 1 ;; esac
    [ -n "$RUNNER_PREREQ_CACHE" ] || RUNNER_PREREQ_CACHE="$(mktemp -d)"
    local key="$RUNNER_PREREQ_CACHE/${spec//[^A-Za-z0-9._-]/_}" cached
    if [ -f "$key" ]; then read -r cached < "$key"; [ "$cached" = "1" ]; return; fi
    local kind="${spec%%:*}" val="${spec#*:}" ok=1
    case "$kind" in
        tool)   command -v "$val" >/dev/null 2>&1 || ok=0 ;;
        corpus) if [ -d "$RUNNER_ROOT/test-pdfs/$val" ] && [ -n "$(find "$RUNNER_ROOT/test-pdfs/$val" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]; then ok=1; else ok=0; fi ;;
        env)    [ -n "${!val:-}" ] || ok=0 ;;
        file)   compgen -G "$RUNNER_ROOT/$val" >/dev/null 2>&1 || ok=0 ;;
        opt)    case ",${RUNNER_OPTS:-}," in *",$val,"*) ok=1 ;; *) ok=0 ;; esac ;;
        *)      ok=0 ;;
    esac
    printf '%s' "$ok" > "$key"
    [ "$ok" = "1" ]
}

# runner_prereq_missing "<specs>" — prints the first absent spec and returns
# 0; returns 1 when every spec is present (or the column is "-").
runner_prereq_missing() {
    local spec
    for spec in $1; do
        [ "$spec" = "-" ] && continue
        runner_prereq_present "$spec" || { printf '%s\n' "$spec"; return 0; }
    done
    return 1
}

# runner_step_status <kind> <class> <policy> <rc> <log> <cmdline>
#   → prints one TAB-separated line: STATUS<TAB>TESTS_EXECUTED
#   STATUS is PASS | FAIL | FAIL_ZERO_TESTS | FAIL_BOUND_EXCEEDED | SKIPPED | NO_RESULT
# Exit 77 is a gate saying "prerequisite missing"; prereqPolicy decides what
# that means. A GRADE row's failure is NO_RESULT: it never sets a verdict.
#
# TESTS_EXECUTED is printed rather than left as a RUNNER_TESTS_EXECUTED side
# effect (t0-gates review, 2026-09-21): every caller invokes this function
# through `status="$(runner_step_status ...)"` -- a command-substitution
# subshell -- so a variable this function SETS never reaches the caller; the
# count was silently empty in every ledger.jsonl ever written. Printing it as
# a second field on the one line of stdout survives the subshell the same way
# runner_ledger_row_fields's tab-separated output already does -- match that
# pattern rather than invent a temp-file handoff.
runner_step_status() {
    local kind="$1" class="$2" policy="$3" rc="$4" log="$5" cmdline="$6"
    local executed="" is_dotnet_test=0
    case "$cmdline" in *"dotnet test"*) is_dotnet_test=1 ;; esac
    [ "$is_dotnet_test" = 1 ] && { runner_zero_tests_executed "$log"; executed="$RUNNER_TESTS_EXECUTED"; }

    if [ "$rc" = "$RUNNER_EXIT_SKIP" ]; then
        if [ "$policy" = skip ]; then printf 'SKIPPED\t%s\n' "$executed"; else printf 'FAIL\t%s\n' "$executed"; fi
        return
    fi
    # A row killed by its own wall-clock bound is NOT a test failure, and must
    # never be read as one: no test asserted anything, the trx is absent or
    # partial, and the cause is upstream of the assertions (#1283). Distinct
    # status so report-gates can say so and point at the diagnostics.
    if [ "$rc" = "$RUNNER_EXIT_BOUND" ] && grep -q "BOUND EXCEEDED" "$log" 2>/dev/null; then
        printf 'FAIL_BOUND_EXCEEDED\t%s\n' "$executed"
        return
    fi
    if [ "$rc" = "0" ]; then
        if [ "$is_dotnet_test" = 1 ] && [ "$executed" = "0" ]; then
            printf 'FAIL_ZERO_TESTS\t%s\n' "$executed"
            return
        fi
        printf 'PASS\t%s\n' "$executed"
        return
    fi
    if [ "$class" = GRADE ]; then printf 'NO_RESULT\t%s\n' "$executed"; else printf 'FAIL\t%s\n' "$executed"; fi
}

# ---------------------------------------------------------------------------
# Tier-pass records — the base a manual gate-asymmetry run compares against
# (LOCAL_GATES.md "Base selection"). Content-validated: the recorded sha must
# be a commit that is an ancestor of HEAD, and the manifest must be the same.
# ---------------------------------------------------------------------------
runner_tier_pass_path() { echo "${RUNNER_STATE_ROOT:-$RUNNER_ROOT/logs/runner-state}/tier-pass/$1.rec"; }

runner_tier_base_read() {   # <tier> — prints the sha, or returns 1
    local rec sha mf
    rec="$(runner_tier_pass_path "$1")"
    [ -s "$rec" ] && [ "$(tail -n 1 "$rec" 2>/dev/null)" = "$RUNNER_SENTINEL" ] || return 1
    sha="$(sed -n 's/^sha=//p' "$rec" | head -1)"
    mf="$(sed -n 's/^manifest=//p' "$rec" | head -1)"
    [ -n "$sha" ] && git cat-file -e "$sha^{commit}" 2>/dev/null && git merge-base --is-ancestor "$sha" HEAD 2>/dev/null || return 1
    [ "$mf" = "$(runner_manifest_fingerprint)" ] || return 1
    printf '%s\n' "$sha"
}

runner_tier_base_record() {   # <tier> — HEAD is the pass point; marker discipline
    local rec tmp
    rec="$(runner_tier_pass_path "$1")"; tmp="$rec.tmp.$$"
    mkdir -p "$(dirname "$rec")"
    {
        echo "tier=$1"
        echo "sha=$(git rev-parse HEAD)"
        echo "manifest=$(runner_manifest_fingerprint)"
        echo "treeDirty=${RUNNER_TREE_DIRTY:-unknown}"
        echo "finished=$(date -u +%Y-%m-%dT%H:%M:%SZ)"
        echo "$RUNNER_SENTINEL"
    } > "$tmp"
    sync; mv -f "$tmp" "$rec"; sync
}

# A pass of a wider tier is a pass of every narrower tier it contains.
runner_tier_base_record_chain() {
    case "$1" in
        full) runner_tier_base_record full; runner_tier_base_record t1; runner_tier_base_record t0 ;;
        t1)   runner_tier_base_record t1; runner_tier_base_record t0 ;;
        *)    runner_tier_base_record "$1" ;;
    esac
}

# runner_gate_asymmetry_base <tier>: 1. an explicit GATE_ASYMMETRY_BASE (the
# pre-push hook sets it from the push range on stdin); 2. the last sha this
# tier passed at; 3. merge-base with origin/develop (first run on a machine).
runner_gate_asymmetry_base() {
    if [ -n "${GATE_ASYMMETRY_BASE:-}" ]; then printf '%s\n' "$GATE_ASYMMETRY_BASE"; return 0; fi
    local r
    if r="$(runner_tier_base_read "$1")" && [ -n "$r" ]; then printf '%s\n' "$r"; return 0; fi
    git merge-base origin/develop HEAD 2>/dev/null || echo origin/develop
}

# ---------------------------------------------------------------------------
# Environment every runner exports before its loop
# ---------------------------------------------------------------------------
# PDFBox is gated on the variable, not the jar (#935): a checked-out jar buys
# nothing unless this is exported. Moved here from test-tier.sh so full and
# t2 get it too.
runner_export_oracle_env() {
    if [ -z "${EXCISE_PDFBOX_JAR:-}" ]; then
        local jar
        jar="$(ls "$RUNNER_ROOT"/tools/vendor/pdfbox-app-*.jar 2>/dev/null | sort | tail -1)"
        [ -n "$jar" ] && export EXCISE_PDFBOX_JAR="$jar"
    fi
    return 0
}

runner_export_release_env() {   # [version]
    RELEASE_VERSION="${1:-${RELEASE_VERSION:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//')}}"
    [ -n "$RELEASE_VERSION" ] || RELEASE_VERSION="0.0.0-local"
    export RELEASE_VERSION AOT_EXTRA_ARGS="${AOT_EXTRA_ARGS:-}"
}
