#!/usr/bin/env bash
# scripts/test-run-bounded.sh — falsifiability for the wall-clock bound (#1283/#1012).
#
# A bound that has never fired is not a bound. This pins run-bounded.sh's
# behaviour against synthetic commands, needs no dotnet, no corpus and no test
# host, and runs in a few seconds — so t0 can afford to prove, on every push,
# that the mechanism protecting a 15-minute row still works.
#
# What it must get right, and why each one is here:
#   1. budget '-' / 0  -> pass straight through, rc preserved. Defaults must
#      reproduce prior behaviour (#1187); a bound that changes unbounded rows
#      is a bound nobody will keep.
#   2. under budget    -> rc preserved, no diagnostics file. The common case
#      must be invisible.
#   3. over budget     -> rc 124, diagnostics written, "BOUND EXCEEDED" in the
#      file AND on stderr (so it lands in the step log the runner keeps).
#   4. the whole PROCESS GROUP dies, not just the leader. `dotnet test` is a
#      5-process tree and the xUnit v3 worker held 7.3 GB in the runs measured
#      (#1283); killing the leader alone orphans it.
#   5. a bad budget    -> rc 2, not a silent unbounded run.
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
BOUND="$ROOT/scripts/run-bounded.sh"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"; pkill -f "excise-bound-selftest" 2>/dev/null' EXIT
fails=0
ok()   { echo "  ok   — $1"; }
bad()  { echo "  FAIL — $1"; fails=$(( fails + 1 )); }

echo "test-run-bounded.sh: the wall-clock bound must still be able to fire"
[ -x "$BOUND" ] || { echo "  FAIL — $BOUND is not executable"; exit 1; }

# 1. unbounded passes through, both spellings, rc preserved exactly
for b in - 0; do
    "$BOUND" "$b" "$TMP" unbounded-pass sh -c 'exit 0' >/dev/null 2>&1
    [ $? = 0 ] && ok "budget '$b': rc 0 preserved" || bad "budget '$b': rc 0 not preserved"
    "$BOUND" "$b" "$TMP" unbounded-fail sh -c 'exit 7' >/dev/null 2>&1
    [ $? = 7 ] && ok "budget '$b': rc 7 preserved" || bad "budget '$b': rc 7 not preserved"
done
[ -e "$TMP/unbounded-fail.bound-diagnostics.txt" ] && bad "unbounded run wrote diagnostics" \
    || ok "unbounded run writes no diagnostics"

# 2. under budget: invisible
"$BOUND" 60 "$TMP" quick sh -c 'exit 3' >/dev/null 2>&1
[ $? = 3 ] && ok "under budget: rc 3 preserved" || bad "under budget: rc 3 not preserved"
[ -e "$TMP/quick.bound-diagnostics.txt" ] && bad "under-budget run wrote diagnostics" \
    || ok "under-budget run writes no diagnostics"

# 3 + 4. over budget: rc 124, diagnostics, and the whole tree dies.
# The child spawns a grandchild that would outlive a leader-only kill.
cat > "$TMP/tree.sh" <<'EOS'
#!/bin/bash
sh -c 'exec -a excise-bound-selftest-grandchild sleep 300' &
sleep 300
EOS
chmod +x "$TMP/tree.sh"
"$BOUND" 3 "$TMP" hung "$TMP/tree.sh" > "$TMP/hung.out" 2> "$TMP/hung.err"
rc=$?
[ "$rc" = 124 ] && ok "over budget: rc 124 (GNU timeout convention)" || bad "over budget: rc $rc, expected 124"
D="$TMP/hung.bound-diagnostics.txt"
[ -s "$D" ] && ok "over budget: diagnostics file written" || bad "over budget: no diagnostics file"
grep -q "BOUND EXCEEDED" "$D" 2>/dev/null && ok "diagnostics say BOUND EXCEEDED" || bad "diagnostics missing BOUND EXCEEDED"
grep -q "BOUND EXCEEDED" "$TMP/hung.err" 2>/dev/null && ok "BOUND EXCEEDED also on stderr (lands in the step log)" \
    || bad "BOUND EXCEEDED absent from stderr; runner_step_status greps the log for it"
grep -q "DISCRIMINATOR\|NO WORKER FOUND" "$D" 2>/dev/null \
    && ok "diagnostics record the worker discriminator (or say none was found)" \
    || bad "diagnostics record neither worker state nor 'no worker'"
sleep 1
if pgrep -f "excise-bound-selftest-grandchild" >/dev/null 2>&1; then
    bad "grandchild SURVIVED the bound — the process group was not killed"
    pkill -f "excise-bound-selftest-grandchild" 2>/dev/null
else
    ok "whole process group killed (no orphaned grandchild)"
fi

# 5. a malformed budget must fail loudly, never run unbounded
"$BOUND" 30x "$TMP" badbudget sh -c 'exit 0' >/dev/null 2>&1
[ $? = 2 ] && ok "malformed budget: rc 2, refuses to run" || bad "malformed budget did not exit 2"

echo
if [ "$fails" = 0 ]; then
    echo "test-run-bounded.sh: PASS"
    exit 0
fi
echo "test-run-bounded.sh: $fails assertion(s) FAILED — the #1283 bound cannot be trusted"
exit 1
