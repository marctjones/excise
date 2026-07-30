#!/usr/bin/env bash
#
# Regression test for #663.
#
# scripts/check-skip-budget.sh --update is supposed to preserve each
# allowlist entry's hand-written justification (the `# why` comment) across
# a regeneration, comparing entries on NAME only. It didn't: the compound
# command's `> "$ALLOWLIST"` redirection truncates the file before the loop
# body's `grep ... "$ALLOWLIST"` runs, so the "old reason" lookup always saw
# an empty file and every entry was rewritten to `# TODO: justify or fix`,
# even for names that hadn't changed at all.
#
# This is a standalone reproduction script (no bats/shunit2 convention exists
# in this repo for shell scripts) that runs the real script's --update path
# against a synthetic allowlist + trx in an isolated temp directory, then
# asserts the ORIGINAL reason text is still present verbatim afterward.
#
# Also covers a second bug found while verifying the #663 fix against the
# real tests/skip-allowlist/Excise.App.Tests.txt: the reason-extraction sed
# (`s/.*#.../`) is greedy and matches through to the LAST `#` on the line,
# so a reason that itself references another issue number (a documented
# convention in this codebase's justifications, e.g. "#653: ...") had
# everything up to and including that inner `#` silently stripped too. Fixed
# alongside #663 in the same edit (`s/[^#]*#.../`, matching the FIRST `#`).
#
# Usage: scripts/test-check-skip-budget.sh
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Isolated repo skeleton: the script under test resolves its allowlist path
# relative to its OWN location (ROOT="$(dirname .../..)"), so copying it
# into $WORK/scripts/ with a sibling $WORK/tests/skip-allowlist/ is enough
# to keep this test from touching the real tests/skip-allowlist/*.txt.
mkdir -p "$WORK/scripts" "$WORK/tests/skip-allowlist"
cp "$HERE/check-skip-budget.sh" "$WORK/scripts/check-skip-budget.sh"
chmod +x "$WORK/scripts/check-skip-budget.sh"

PROJECT="$WORK/Demo.Tests.csproj"
touch "$PROJECT"
ALLOWLIST="$WORK/tests/skip-allowlist/Demo.Tests.txt"

cat > "$ALLOWLIST" <<'EOF'
# Skips allow-listed for Demo.Tests. See scripts/check-skip-budget.sh (#619).
# Every line is coverage we are NOT getting. Justify it or delete it.
# Format:  TestName   # why
#
Demo.Tests.FooTests.Skip1   # real hand-written justification A
# --- grouping note: the following are veraPDF-dependent (#668) ---
# second line of the same hand-written comment block
Demo.Tests.FooTests.Skip2   # real hand-written justification B
Demo.Tests.FooTests.Skip3   # #123: references another issue, and (see #456) a second one too
EOF

BEFORE="$(cat "$ALLOWLIST")"

# Synthetic trx reporting the SAME two skip names as the allowlist (names
# unchanged from the prior run) — a correct --update must round-trip both
# reasons verbatim. --trx lets us avoid actually running `dotnet test`.
TRX="$WORK/r.trx"
cat > "$TRX" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo.Tests.FooTests.Skip1" outcome="NotExecuted" />
    <UnitTestResult testName="Demo.Tests.FooTests.Skip2" outcome="NotExecuted" />
    <UnitTestResult testName="Demo.Tests.FooTests.Skip3" outcome="NotExecuted" />
  </Results>
</TestRun>
EOF

"$WORK/scripts/check-skip-budget.sh" "$PROJECT" --update --trx "$TRX" >"$WORK/update.log" 2>&1

AFTER="$(cat "$ALLOWLIST")"

FAIL=0

if ! grep -qF 'Demo.Tests.FooTests.Skip1   # real hand-written justification A' <<<"$AFTER"; then
  echo "FAIL: Skip1's justification did not survive --update"
  FAIL=1
fi

if ! grep -qF 'Demo.Tests.FooTests.Skip2   # real hand-written justification B' <<<"$AFTER"; then
  echo "FAIL: Skip2's justification did not survive --update"
  FAIL=1
fi

if ! grep -qF 'Demo.Tests.FooTests.Skip3   # #123: references another issue, and (see #456) a second one too' <<<"$AFTER"; then
  echo "FAIL: Skip3's justification (which itself contains '#' characters) did not survive --update intact"
  FAIL=1
fi

if grep -q 'TODO: justify or fix' <<<"$AFTER"; then
  echo "FAIL: an entry whose name was unchanged was rewritten to the TODO placeholder"
  FAIL=1
fi

# #668: hand-written comment BLOCKS (not just per-entry reasons) must survive
# --update, attached to the entry they precede.
if ! grep -qF '# --- grouping note: the following are veraPDF-dependent (#668) ---' <<<"$AFTER"; then
  echo "FAIL: a hand-written comment block was discarded by --update (#668)"
  FAIL=1
fi
if ! grep -qF '# second line of the same hand-written comment block' <<<"$AFTER"; then
  echo "FAIL: a multi-line hand-written comment block was only partially preserved (#668)"
  FAIL=1
fi
# The preserved block must sit immediately before the entry it annotated (Skip2)
# — checked portably (BSD grep has no -P): the two block lines then the entry,
# consecutively.
if ! printf '%s\n' "$AFTER" | awk '
  /^# --- grouping note: the following are veraPDF-dependent \(#668\) ---$/ { s = 1; next }
  s == 1 && /^# second line of the same hand-written comment block$/ { s = 2; next }
  s == 2 && /^Demo\.Tests\.FooTests\.Skip2 / { ok = 1 }
  { s = 0 }
  END { exit ok ? 0 : 1 }'; then
  echo "FAIL: the preserved comment block is not positioned immediately before its entry (#668)"
  FAIL=1
fi

if [[ $FAIL -ne 0 ]]; then
  echo
  echo "--- allowlist BEFORE --update ---"
  echo "$BEFORE"
  echo "--- allowlist AFTER --update ---"
  echo "$AFTER"
  echo "--- script output ---"
  cat "$WORK/update.log"
  exit 1
fi

echo "PASS: check-skip-budget.sh --update preserves justifications (#663), reasons that"
echo "      contain '#' (#665), and hand-written comment blocks (#668)"

# ===========================================================================
# #854: per-entry prerequisite conditioning
# ===========================================================================
# The allowlist is calibrated for a corpus-less CI runner. Most entries gate on
# a gitignored corpus or an optional tool, so on a corpus-equipped dev machine
# those tests RUN and the reverse check ("allow-listed but no longer skipping")
# fired on every local run — all three projects, both directions. A gate that
# always fails locally is a gate nobody reads.
#
# The absent-prerequisite branch cannot be tested by running the gate here
# (every prerequisite IS present) and must NOT be tested by hiding 888MB of
# corpora. So test the RESOLVER: SKIP_BUDGET_FORCE_ABSENT makes a spec resolve
# absent deterministically, with no filesystem changes.
#
# Contract, in full:
#   conditioned + prereq present + not skipping -> silent   (the new behaviour)
#   conditioned + prereq absent  + not skipping -> FAIL     (unchanged)
#   unconditioned              + not skipping   -> FAIL     (unchanged)
#   skipping but not allow-listed               -> FAIL     (forward check, never relaxed)
#   --update must not DELETE a conditioned entry whose prereq is present

P2="$WORK/Demo2.Tests.csproj"; touch "$P2"
AL2="$WORK/tests/skip-allowlist/Demo2.Tests.txt"

# A trx in which NOTHING skipped, so every allowlist entry is "no longer
# skipping" and the reverse check is what decides each one's fate.
EMPTY_TRX="$WORK/empty.trx"
cat > "$EMPTY_TRX" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results />
</TestRun>
EOF

cat > "$AL2" <<'EOF'
#
Demo2.Tests.A.CondPresent   # #999: needs a tool that exists [requires: tool:ls]
Demo2.Tests.A.CondAbsent   # needs a tool that does not [requires: tool:excise-no-such-tool-xyz]
Demo2.Tests.A.Uncond   # no marker at all
EOF

OUT2="$WORK/cond.log"
RC2=0
"$WORK/scripts/check-skip-budget.sh" "$P2" --trx "$EMPTY_TRX" >"$OUT2" 2>&1 || RC2=$?

F2=0
[[ "$RC2" -ne 0 ]] || { echo "FAIL(#854): gate passed despite an unconditioned entry no longer skipping"; F2=1; }
grep -qF -- '- Demo2.Tests.A.CondAbsent' "$OUT2" || { echo "FAIL(#854): conditioned entry with an ABSENT prereq was not reported by the reverse check"; F2=1; }
grep -qF -- '- Demo2.Tests.A.Uncond' "$OUT2" || { echo "FAIL(#854): unconditioned entry was not reported by the reverse check"; F2=1; }
if grep -qF -- '- Demo2.Tests.A.CondPresent' "$OUT2"; then
  echo "FAIL(#854): conditioned entry whose prereq is PRESENT was reported as a failure"; F2=1
fi

# Same allowlist, but force the present tool to resolve absent: now ALL THREE
# must be reported. This is the CI-side branch.
OUT3="$WORK/cond-absent.log"
RC3=0
SKIP_BUDGET_FORCE_ABSENT="tool:ls" \
  "$WORK/scripts/check-skip-budget.sh" "$P2" --trx "$EMPTY_TRX" >"$OUT3" 2>&1 || RC3=$?
[[ "$RC3" -ne 0 ]] || { echo "FAIL(#854): gate passed with every prerequisite absent"; F2=1; }
grep -qF -- '- Demo2.Tests.A.CondPresent' "$OUT3" || {
  echo "FAIL(#854): with its prereq forced ABSENT, the conditioned entry was still exempted"
  echo "            — conditioning is unconditional, i.e. it silently weakened the gate"; F2=1; }

# Only a satisfied conditioned entry -> the gate must be clean.
cat > "$AL2" <<'EOF'
#
Demo2.Tests.A.CondPresent   # needs a tool that exists [requires: tool:ls]
EOF
OUT4="$WORK/cond-clean.log"
RC4=0
"$WORK/scripts/check-skip-budget.sh" "$P2" --trx "$EMPTY_TRX" >"$OUT4" 2>&1 || RC4=$?
[[ "$RC4" -eq 0 ]] || { echo "FAIL(#854): gate did not pass when the only entry's prereq is satisfied"; cat "$OUT4"; F2=1; }

# The FORWARD check is never relaxed: a skip that is not allow-listed fails
# even in an allowlist made entirely of satisfied conditioned entries.
NEW_TRX="$WORK/newskip.trx"
cat > "$NEW_TRX" <<'EOF'
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="Demo2.Tests.A.BrandNewSkip" outcome="NotExecuted" />
  </Results>
</TestRun>
EOF
OUT5="$WORK/forward.log"
RC5=0
"$WORK/scripts/check-skip-budget.sh" "$P2" --trx "$NEW_TRX" >"$OUT5" 2>&1 || RC5=$?
[[ "$RC5" -ne 0 ]] || { echo "FAIL(#854): forward check was relaxed — an un-allow-listed skip passed"; F2=1; }
grep -qF -- '+ Demo2.Tests.A.BrandNewSkip' "$OUT5" || { echo "FAIL(#854): un-allow-listed skip was not reported"; F2=1; }

# --update must not DELETE a conditioned entry that is running here. Otherwise
# running --update on a corpus-equipped machine strips the entries CI needs —
# turning the flag from "won't add" into "removes what you had".
"$WORK/scripts/check-skip-budget.sh" "$P2" --update --trx "$EMPTY_TRX" >"$WORK/cond-update.log" 2>&1
AFTER2="$(cat "$AL2")"
grep -qF 'Demo2.Tests.A.CondPresent' <<<"$AFTER2" || {
  echo "FAIL(#854): --update DELETED a conditioned entry whose prerequisite is present"; F2=1; }
grep -qF '[requires: tool:ls]' <<<"$AFTER2" || {
  echo "FAIL(#854): --update dropped the [requires: ...] marker"; F2=1; }

if [[ $F2 -ne 0 ]]; then
  echo
  echo "--- conditioned (prereq present) ---"; cat "$OUT2"
  echo "--- conditioned (prereq forced absent) ---"; cat "$OUT3"
  echo "--- allowlist after --update ---"; echo "$AFTER2"
  exit 1
fi

echo "PASS: per-entry [requires: ...] conditioning relaxes ONLY the reverse check and"
echo "      only when the prerequisite is present; forward check and --update are safe (#854)"
