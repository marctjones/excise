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
RUNNER_LABEL=""
RUNNER_CONFIG=""
RUNNER_SKIPPED_COUNT=0

runner_say() { echo -e "$1"; }

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

    RUNNER_SHA="$(git rev-parse HEAD 2>/dev/null || echo nogit)"
    local dirty=""
    if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
        dirty="-dirty"
    fi

    local key="${RUNNER_LABEL}_${RUNNER_CONFIG}_${RUNNER_SHA:0:12}${dirty}"
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

runner_is_never_checkpointed() {
    printf '%s' "$1" | grep -qiE "$RUNNER_NEVER_CHECKPOINT"
}

# ---------------------------------------------------------------------------
# Checkpoint read/write
# ---------------------------------------------------------------------------

# runner_step_mark <name> <rc> <duration_seconds>
# Writes a marker ONLY for a passing step. A failure leaves no marker, so the
# step re-runs next time.
runner_step_mark() {
    local name="$1" rc="$2" dur="${3:-0}"
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

# runner_step_should_run <name> — 0 (true) if the step must run.
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
    grep -q "^sha=$RUNNER_SHA$" "$f" 2>/dev/null || return 0    # different commit

    RUNNER_SKIPPED_COUNT=$(( RUNNER_SKIPPED_COUNT + 1 ))
    return 1
}

runner_skipped_count() { echo "$RUNNER_SKIPPED_COUNT"; }

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
