#!/usr/bin/env bash
# check-oracle-floor.sh <trx> <min-passed> <label>
#
# Assert that an independent-oracle test subset ACTUALLY EXECUTED.
#
# WHY A FLOOR AND NOT JUST AN EXIT CODE
#
# `dotnet test` exits 0 when every test in the filter SKIPPED, and exits 0 when
# every test PASSED. Those two outcomes are indistinguishable to the caller and
# opposite in meaning: one is corroboration by a tool that is not excise, the
# other is a self-oracle wearing its colours.
#
# The tests in these subsets are gated on Assert.SkipUnless(IsAvailable). If a
# reference tool disappears — an upgrade renames a binary, a corpus is cleaned
# up — they all quietly become skips and the gate goes green while verifying
# nothing. Three shipped redaction leaks (#636, #608, #637) passed a green
# suite; this is one of the mechanisms that lets that happen.
#
# A FLOOR, not an exact match: a legitimately-added oracle test should not
# require bumping this in lockstep. But a drop to a handful (tools not
# resolving) or to zero (the filter no longer matches anything after a rename)
# fails loud.
#
# Ported from rendering-linux.yml's "Assert the ... actually executed" steps
# (#1360).
set -uo pipefail
TRX="${1:?usage: check-oracle-floor.sh <trx> <min-passed> <label>}"
MIN="${2:?}"
LABEL="${3:-oracle subset}"

if [ ! -f "$TRX" ]; then
    echo "FAIL: $LABEL produced no TRX at $TRX" >&2
    echo "      The test run did not happen, so its green means nothing." >&2
    exit 1
fi

passed=$(grep -o 'outcome="Passed"' "$TRX" | wc -l | tr -d ' ')
skipped=$(grep -o 'outcome="NotExecuted"' "$TRX" | wc -l | tr -d ' ')
failed=$(grep -o 'outcome="Failed"' "$TRX" | wc -l | tr -d ' ')
echo "$LABEL: passed=$passed skipped=$skipped failed=$failed (floor $MIN)"

if [ "$passed" -lt "$MIN" ]; then
    cat >&2 <<MSG

FAIL: only $passed $LABEL test(s) passed; the floor is $MIN.

  $skipped skipped. On a machine with the reference tools and corpora present
  these run; a number this low means the tools, the corpus, or the FILTER
  ITSELF did not resolve — so this green is a self-oracle, not real coverage.

  Check scripts/check-oracle-tools.sh first, then whether a rename broke the
  filter. Do NOT lower the floor to make this pass.
MSG
    exit 1
fi
echo "  ok"
