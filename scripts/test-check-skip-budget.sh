#!/usr/bin/env bash
#
# Regression test for the #1172 skip-budget gate: every skip must carry a
# declared, non-empty reason in the trx's <Output><ErrorInfo><Message>, and
# the gate must never silently accept one that does not.
#
# Standalone reproduction script (no bats/shunit2 convention exists in this
# repo for shell scripts) that runs the real script against synthetic trx
# files in an isolated temp directory — real xunit.v3 skip reasons were
# verified by hand against a live run (see the header comment in
# check-skip-budget.sh) before writing these fixtures, so the XML shape here
# is not invented.
#
# Usage: scripts/test-check-skip-budget.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# The script under test resolves paths relative to its OWN location
# (ROOT="$(dirname .../..)"), so it must live under a "scripts/" directory
# for that resolution to land somewhere sane even in the branches this test
# does not exercise (e.g. it would look for "$ROOT/scripts/assert-fresh.sh").
mkdir -p "$WORK/scripts"
cp "$HERE/check-skip-budget.sh" "$WORK/scripts/check-skip-budget.sh"
chmod +x "$WORK/scripts/check-skip-budget.sh"
GATE="$WORK/scripts/check-skip-budget.sh"

FAIL=0
PROJECT="$WORK/Demo.Tests.csproj"
touch "$PROJECT"

# ---------------------------------------------------------------------------
# 1. A skip with a declared, non-empty reason passes.
# ---------------------------------------------------------------------------
TRX_OK="$WORK/declared.trx"
cat > "$TRX_OK" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.A.NeedsTool" outcome="NotExecuted" executionId="e1">
      <Output><ErrorInfo><Message>mutool not installed</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testName="Demo.Tests.A.StaticSkip" outcome="NotExecuted" executionId="e2">
      <Output><ErrorInfo><Message>#1381: known renderer gap, see NarrowingPower</Message></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>
EOF

OUT1="$WORK/ok.log"
RC1=0
"$GATE" "$PROJECT" --trx "$TRX_OK" >"$OUT1" 2>&1 || RC1=$?
if [[ "$RC1" -ne 0 ]]; then
  echo "FAIL: gate rejected skips that DO carry a declared reason"
  cat "$OUT1"
  FAIL=1
fi
grep -qF "2 skip(s)" "$OUT1" || { echo "FAIL: gate did not report the expected declared-skip count"; cat "$OUT1"; FAIL=1; }

# ---------------------------------------------------------------------------
# 2. A skip with NO <Output> at all (no reason ever recorded) fails, and is
#    named in the output.
# ---------------------------------------------------------------------------
TRX_NONE="$WORK/no-output.trx"
cat > "$TRX_NONE" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.A.SilentSkip" outcome="NotExecuted" executionId="e1" />
  </Results>
</TestRun>
EOF

OUT2="$WORK/none.log"
RC2=0
"$GATE" "$PROJECT" --trx "$TRX_NONE" >"$OUT2" 2>&1 || RC2=$?
[[ "$RC2" -ne 0 ]] || { echo "FAIL: gate accepted a skip with no <Output> at all"; FAIL=1; }
grep -qF -- '+ Demo.Tests.A.SilentSkip' "$OUT2" || { echo "FAIL: gate did not name the undeclared skip"; cat "$OUT2"; FAIL=1; }

# ---------------------------------------------------------------------------
# 3. A skip whose <Message> is present but blank/whitespace-only fails —
#    an empty string is not a reason.
# ---------------------------------------------------------------------------
TRX_BLANK="$WORK/blank.trx"
cat > "$TRX_BLANK" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.A.BlankReason" outcome="NotExecuted" executionId="e1">
      <Output><ErrorInfo><Message>   </Message></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>
EOF

OUT3="$WORK/blank.log"
RC3=0
"$GATE" "$PROJECT" --trx "$TRX_BLANK" >"$OUT3" 2>&1 || RC3=$?
[[ "$RC3" -ne 0 ]] || { echo "FAIL: gate accepted a skip whose reason is blank/whitespace-only"; FAIL=1; }
grep -qF -- '+ Demo.Tests.A.BlankReason' "$OUT3" || { echo "FAIL: gate did not name the blank-reason skip"; cat "$OUT3"; FAIL=1; }

# ---------------------------------------------------------------------------
# 4. Mixed: one declared, one undeclared, in the SAME run — the declared one
#    must not mask the undeclared one, and vice versa.
# ---------------------------------------------------------------------------
TRX_MIXED="$WORK/mixed.trx"
cat > "$TRX_MIXED" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.A.Good" outcome="NotExecuted" executionId="e1">
      <Output><ErrorInfo><Message>needs poppler corpus</Message></ErrorInfo></Output>
    </UnitTestResult>
    <UnitTestResult testName="Demo.Tests.A.Bad" outcome="NotExecuted" executionId="e2" />
  </Results>
</TestRun>
EOF

OUT4="$WORK/mixed.log"
RC4=0
"$GATE" "$PROJECT" --trx "$TRX_MIXED" >"$OUT4" 2>&1 || RC4=$?
[[ "$RC4" -ne 0 ]] || { echo "FAIL: mixed run (one declared, one not) was accepted"; FAIL=1; }
grep -qF -- '+ Demo.Tests.A.Bad' "$OUT4" || { echo "FAIL: mixed run did not name the undeclared skip"; cat "$OUT4"; FAIL=1; }
if grep -qF -- '+ Demo.Tests.A.Good' "$OUT4"; then
  echo "FAIL: mixed run wrongly flagged the declared skip too"; FAIL=1
fi

# ---------------------------------------------------------------------------
# 5. No trx produced at all is a hard FAIL, never silently "no skips". A
#    --trx pointing at a file that does not exist is what a run whose
#    `dotnet test` never wrote a trx (crashed host, killed process) hands the
#    gate; it is refused up front. Deterministic, no real build.
# ---------------------------------------------------------------------------
OUT5="$WORK/notrx.log"
RC5=0
"$GATE" "$PROJECT" --trx "$WORK/does-not-exist.trx" >"$OUT5" 2>&1 || RC5=$?
[[ "$RC5" -ne 0 ]] || { echo "FAIL: a run that produced no trx silently reported success"; FAIL=1; }
grep -qF "no trx produced" "$OUT5" || { echo "FAIL: the no-trx failure was not clearly diagnosed"; cat "$OUT5"; FAIL=1; }

# ---------------------------------------------------------------------------
# 6. --trx is repeatable and the results union across files (chunked runs).
# ---------------------------------------------------------------------------
TRX_A="$WORK/chunk-a.trx"
TRX_B="$WORK/chunk-b.trx"
cat > "$TRX_A" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.A.ChunkAGood" outcome="NotExecuted" executionId="a1">
      <Output><ErrorInfo><Message>needs corpus X</Message></ErrorInfo></Output>
    </UnitTestResult>
  </Results>
</TestRun>
EOF
cat > "$TRX_B" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.B.ChunkBBad" outcome="NotExecuted" executionId="b1" />
  </Results>
</TestRun>
EOF

OUT6="$WORK/union.log"
RC6=0
"$GATE" "$PROJECT" --trx "$TRX_A" --trx "$TRX_B" >"$OUT6" 2>&1 || RC6=$?
[[ "$RC6" -ne 0 ]] || { echo "FAIL: --trx union did not see the undeclared skip in the second file"; FAIL=1; }
grep -qF -- '+ Demo.Tests.B.ChunkBBad' "$OUT6" || { echo "FAIL: --trx union missed chunk B's undeclared skip"; cat "$OUT6"; FAIL=1; }

# ---------------------------------------------------------------------------
# 7. A union with one part MISSING or EMPTY fails and names that part, even
#    though the part that is present is clean. t1's skip-budget-rendering
#    reads three filtered trx; a lost one used to be dropped by a best-effort
#    `cp` while the rest were read as the whole project.
# ---------------------------------------------------------------------------
OUT7="$WORK/lost-part.log"
RC7=0
"$GATE" "$PROJECT" --trx "$TRX_OK" --trx "$WORK/lost-part.trx" >"$OUT7" 2>&1 || RC7=$?
[[ "$RC7" -ne 0 ]] || { echo "FAIL: a union with a missing part was accepted as the whole run"; cat "$OUT7"; FAIL=1; }
grep -qF "no trx produced at $WORK/lost-part.trx" "$OUT7" || { echo "FAIL: the missing part of the union was not named"; cat "$OUT7"; FAIL=1; }

: > "$WORK/empty-part.trx"
OUT7B="$WORK/empty-part.log"
RC7B=0
"$GATE" "$PROJECT" --trx "$TRX_OK" --trx "$WORK/empty-part.trx" >"$OUT7B" 2>&1 || RC7B=$?
[[ "$RC7B" -ne 0 ]] || { echo "FAIL: a union with a zero-byte part was accepted as the whole run"; cat "$OUT7B"; FAIL=1; }
grep -qF "no trx produced at $WORK/empty-part.trx" "$OUT7B" || { echo "FAIL: the empty part of the union was not named"; cat "$OUT7B"; FAIL=1; }

if [[ $FAIL -ne 0 ]]; then
  exit 1
fi

echo "PASS: check-skip-budget.sh (#1172) accepts every skip with a declared,"
echo "      non-empty in-code reason and fails on any that has none — no"
echo "      <Output> at all, a blank <Message>, mixed in with a good one, or"
echo "      missing entirely across a chunked (--trx, repeated) union — and"
echo "      on a union with one part missing or empty."
