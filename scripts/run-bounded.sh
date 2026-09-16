#!/bin/bash
# scripts/run-bounded.sh — the OUTER wall-clock bound on a gate row (#1283).
#
#   run-bounded.sh <budget_seconds|-> <diag_dir> <row_name> <command> [args...]
#
# Why this exists, and why `--blame-hang-timeout` is not enough.
#
# Measured 2026-09-16 by fault injection (#1283): when the xUnit v3 worker
# process STALLS (alive, not progressing), blame's inactivity timer fires
# exactly on schedule — and the run still does not end. In the reproduction,
# with --blame-hang-timeout 60000, blame fired at T+60s, ran `createdump` on
# the *testhost relay* (pid 43982) rather than the stalled worker (pid 43988),
# wrote a 5.8 GB full dump into $TMPDIR, reported "Target process is alive",
# and `dotnet test` was STILL RUNNING at T+365s — 6x the timeout. The real
# 2026-09-10 instance of this ran for NINE HOURS.
#
# Worker DEATH is bounded correctly (xUnit reports "Test process crashed with
# exit code 137" and blame writes a Sequence file). Worker STALL is not bounded
# at all. So blame is a diagnostics mechanism, and this is the thing that
# actually ends the run.
#
# Three properties worth not breaking:
#
#  1. A budget of `-` or 0 means UNBOUNDED, and takes the exec path — byte-for-
#     byte today's behaviour. Defaults must reproduce prior behaviour (#1187).
#  2. We kill the PROCESS GROUP, not the pid. `dotnet test` is a 5-process tree
#     (dotnet test -> vstest.console -> {datacollector, testhost} -> the xUnit
#     v3 apphost that actually runs tests) and the apphost held 7.3 GB in the
#     runs measured. Killing the leader alone orphans it.
#  3. Diagnostics are collected BEFORE the kill, from the WORKER, and the very
#     first thing recorded is %CPU + state + cputime TWICE. That delta is the
#     discriminator the 2026-09-10 record lacked: cputime frozen => stalled or
#     deadlocked; cputime climbing => livelock or a legitimately slow test.
#     `sample` on the relay is what sent #1283's own investigation astray.
#
# Exits 124 (GNU timeout's convention) when the budget is exceeded;
# runner_step_status maps that to FAIL_BOUND_EXCEEDED so report-gates can tell
# a bound from a test failure.

set -u
BUDGET="${1:-}"; DIAG_DIR="${2:-}"; ROW="${3:-row}"
shift 3 2>/dev/null || { echo "run-bounded.sh: usage: <budget|-> <diag_dir> <name> <cmd...>" >&2; exit 2; }
[ "$#" -gt 0 ] || { echo "run-bounded.sh: no command given" >&2; exit 2; }

# Unbounded: behave exactly as before, with no extra process in the tree.
case "$BUDGET" in
    -|0|"") exec "$@" ;;
    *[!0-9]*) echo "run-bounded.sh: budget must be seconds or '-': $BUDGET" >&2; exit 2 ;;
esac

KILL_GRACE="${RUNNER_BOUND_KILL_GRACE:-20}"
DIAG="$DIAG_DIR/$ROW.bound-diagnostics.txt"
mkdir -p "$DIAG_DIR" 2>/dev/null

# Own process group so we can kill the whole tree. `set -m` makes the next
# background job a process-group leader; its pid is then also its pgid.
set -m
"$@" &
CHILD=$!
set +m

# ---------------------------------------------------------------------------
# descendants <pid> — the pid's subtree, breadth-first, via one ps snapshot.
# ---------------------------------------------------------------------------
descendants() {
    ps -Ao pid,ppid | awk -v root="$1" '
        NR > 1 { kid[$2] = kid[$2] " " $1 }
        END {
            n = split(kid[root], q, " "); out = ""
            for (i = 1; i <= n; i++) if (q[i] != "") stack[++top] = q[i]
            while (top > 0) {
                p = stack[top--]; out = out " " p
                m = split(kid[p], r, " ")
                for (j = 1; j <= m; j++) if (r[j] != "") stack[++top] = r[j]
            }
            print out
        }'
}

# The WORKER is the descendant that is not part of the VSTest relay chain:
# relays are `dotnet`, `sh`, `time`; the xUnit v3 apphost is the test assembly
# itself (e.g. .../Excise.App.Tests/bin/Debug/net10.0/Excise.App.Tests).
find_worker() {
    local p
    for p in $(descendants "$CHILD"); do
        case "$(ps -o comm= -p "$p" 2>/dev/null | xargs -r basename 2>/dev/null)" in
            dotnet|sh|bash|time|"") ;;
            *) echo "$p"; return 0 ;;
        esac
    done
    return 1
}

snap() { ps -o pid,pcpu,rss,state,time,comm -p "$1" 2>/dev/null | tail -1; }

collect_diagnostics() {
    local worker=""
    {
        echo "=============================================================="
        echo "BOUND EXCEEDED — row '$ROW' exceeded its ${BUDGET}s wall-clock budget"
        echo "when: $(date '+%Y-%m-%d %H:%M:%S %z')"
        echo "budget source: tests/gates.tsv 'budget' column (#1283)"
        echo "=============================================================="
        echo
        echo "--- process tree under 'dotnet test' (pid $CHILD) ---"
        ps -o pid,ppid,pcpu,rss,state,time,comm -p "$CHILD" 2>/dev/null
        for p in $(descendants "$CHILD"); do snap "$p"; done
        echo
        worker="$(find_worker || true)"
        if [ -z "$worker" ]; then
            echo "--- NO WORKER FOUND ---"
            echo "No non-relay descendant. Either the xUnit v3 worker already died"
            echo "(relays then wait forever — the #1283 shape) or it never started."
        else
            echo "--- THE DISCRIMINATOR: worker pid $worker, two samples 3s apart ---"
            echo "cputime FROZEN   => stalled / deadlocked (a SIGSTOP-shaped hang)"
            echo "cputime CLIMBING => livelock, or a legitimately slow test"
            echo "t0: $(snap "$worker")"
            sleep 3
            echo "t1: $(snap "$worker")"
            echo
            echo "--- managed stacks (dotnet-stack; 'sample' cannot resolve JIT frames) ---"
            local ds=""
            for c in "$HOME/.dotnet/tools/dotnet-stack" "$(command -v dotnet-stack 2>/dev/null)"; do
                [ -n "$c" ] && [ -x "$c" ] && { ds="$c"; break; }
            done
            if [ -n "$ds" ]; then
                # Bound the diagnostic itself: it must never become the hang.
                if command -v gtimeout >/dev/null 2>&1; then
                    gtimeout 120 "$ds" report -p "$worker" 2>&1 | head -400
                else
                    "$ds" report -p "$worker" 2>&1 | head -400
                fi
            else
                echo "(dotnet-stack not installed: dotnet tool install -g dotnet-stack)"
                echo "(falling back to native frames, which cannot show managed locks)"
                command -v sample >/dev/null 2>&1 && sample "$worker" 3 -mayDie 2>&1 | head -200
            fi
        fi
        echo
        echo "--- blame hang dumps this run left behind ---"
        # Blame writes to $TMPDIR/<guid>/, NOT to the log dir and NOT to
        # <project>/TestResults — which is why #1283 concluded "no dump".
        find "${TMPDIR:-/tmp}" -maxdepth 2 -name '*hangdump.dmp' -newer "$DIAG" \
            -exec ls -la {} \; 2>/dev/null | head -20
        echo "(a dump of a RELAY is ~6 GB and diagnostically worthless; the"
        echo " worker stacks above are the evidence that matters)"
    } >> "$DIAG" 2>&1
}

# ---------------------------------------------------------------------------
# Watchdog. Poll rather than sleep-then-check so a fast row exits immediately.
# ---------------------------------------------------------------------------
ELAPSED=0
while [ "$ELAPSED" -lt "$BUDGET" ]; do
    kill -0 "$CHILD" 2>/dev/null || break
    sleep 1
    ELAPSED=$(( ELAPSED + 1 ))
done

if kill -0 "$CHILD" 2>/dev/null; then
    : > "$DIAG"                       # also the -newer reference for dump discovery
    collect_diagnostics
    echo "run-bounded.sh: BOUND EXCEEDED after ${BUDGET}s; diagnostics: $DIAG" >&2
    sed -n '1,40p' "$DIAG" >&2        # the discriminator lands in the step log too
    kill -TERM -"$CHILD" 2>/dev/null || kill -TERM "$CHILD" 2>/dev/null
    WAITED=0
    while [ "$WAITED" -lt "$KILL_GRACE" ] && kill -0 "$CHILD" 2>/dev/null; do
        sleep 1; WAITED=$(( WAITED + 1 ))
    done
    kill -KILL -"$CHILD" 2>/dev/null || kill -KILL "$CHILD" 2>/dev/null
    wait "$CHILD" 2>/dev/null
    exit 124
fi

wait "$CHILD"; exit $?
