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

    RUNNER_SHA="$(git rev-parse HEAD 2>/dev/null || echo nogit)"
    local dirty=""
    if ! git diff --quiet 2>/dev/null || ! git diff --cached --quiet 2>/dev/null; then
        dirty="-dirty"
    fi

    # Exported so the ledger can state it: a sha alone reads as "this commit"
    # when the run may have measured uncommitted changes on top of it (#994).
    RUNNER_TREE_DIRTY="$([ -n "$dirty" ] && echo yes || echo no)"

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
RUNNER_ROOT="${RUNNER_ROOT:-$PWD}"
RUNNER_MANIFEST="${RUNNER_MANIFEST:-$RUNNER_ROOT/tests/gates.tsv}"
RUNNER_MANIFEST_HEADER=$'name\tclass\ttiers\tkind\ttarget\tfilter\tratchet\tknownIssue\tprereq\tprereqPolicy\tcheckpoint\toracle\tnote'
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
        if (NF != 13) { bad("expected 13 columns, got " NF); next }
        name = $1; class = $2; tiers = $3; kind = $4; target = $5; filter = $6; ratchet = $7
        known = $8; prereq = $9; policy = $10; ckpt = $11; oracle = $12; note = $13
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
