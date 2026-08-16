#!/usr/bin/env bash
#
# Deep fuzz sweep (#984) — actually run StructureAwareFuzzTests at the depth
# where it has found every one of its real escapes, instead of only at t0's
# 250-iteration regression-guard depth.
#
# StructureAwareFuzzTests.Iterations is checked in at 250, sized for t0's ~30s
# push budget. Every escape that suite has actually found needed far more:
#
#   defect                        found at iteration
#   #974's seven escapes          deep development sweep
#   #975 JBIG2 allocation         5432
#   odd-length /Index             5632
#   xref stream with no /W        ~10955
#   /Prev holding a reference     7132
#
# Not one of those is reachable at 250. A green t0 run is a regression guard,
# not evidence of "no defects left" — the class docstring says so. This script
# is the DISCOVERY mechanism: it raises Iterations via the EXCISE_FUZZ_ITERATIONS
# environment variable the test class now reads (see StructureAwareFuzzTests.cs)
# and runs the suite at a depth that has, historically, actually found things.
#
# Determinism (the property that makes a finding actionable, not just scary):
# seeds are fixed in the test file regardless of this script. Mutation N for a
# given seed depends only on how many prior mutations were drawn from that
# seed's Random(seed) — never on how many iterations the row was configured to
# run — so a failure at "seed=9603 iter=8112" reproduces EXACTLY by re-running
# with EXCISE_FUZZ_ITERATIONS at least 8113 and the same seed. Nothing here
# introduces new randomness; it only widens how far the existing deterministic
# sequence is walked.
#
# There is no nightly runner in this repo (nightly-corpus in
# tests/format-compatibility-suite.json is status: planned, primaryCommand:
# null — #960 deliberately did not build one). So the cheapest useful version
# of "run deep, periodically" is this script invoked from
# `scripts/run-full-suite.sh --everything`, which already runs for about an
# hour; a couple of minutes here is affordable and means depth is reached at
# least once per release instead of only when someone remembers to raise the
# constant by hand (which is how #975 was actually found).
#
# Usage:
#   scripts/run-deep-fuzz-sweep.sh              # depth 20000 (default)
#   scripts/run-deep-fuzz-sweep.sh 8000          # explicit depth
#   EXCISE_FUZZ_ITERATIONS=8000 scripts/run-deep-fuzz-sweep.sh   # same, via env
#
# A positional argument wins over the environment variable if both are given.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# 20000: the depth #984 sized this at ("20000/seed is ~76 seconds for the six
# rows here" — cheap enough that there is no reason to run shallower by
# default). Override for a targeted, longer hunt.
DEPTH="${EXCISE_FUZZ_ITERATIONS:-20000}"
if [ "${1:-}" != "" ]; then
  DEPTH="$1"
fi

case "$DEPTH" in
  ''|*[!0-9]*)
    echo "FAIL: iteration depth must be a positive integer, got: $DEPTH" >&2
    exit 2
    ;;
esac
if [ "$DEPTH" -le 0 ]; then
  echo "FAIL: iteration depth must be a positive integer, got: $DEPTH" >&2
  exit 2
fi

echo "==> deep fuzz sweep: StructureAwareFuzzTests at EXCISE_FUZZ_ITERATIONS=$DEPTH"

# `dotnet test --filter` EXITS 0 WHEN IT MATCHES NOTHING (the same trap
# scripts/check-extraction-parity.sh guards against, #941) — a renamed or
# moved StructureAwareFuzzTests would otherwise leave this "sweep" green
# having swept nothing. Capture output and refuse that explicitly.
# `tee`, not capture-and-echo: a 20000-deep sweep runs for a while and a gate
# that prints nothing while it works looks hung.
RUN_LOG="$(mktemp)"
trap 'rm -f "$RUN_LOG"' EXIT
set +e
EXCISE_FUZZ_ITERATIONS="$DEPTH" dotnet test Excise.Core.Tests -c Debug \
  --filter "FullyQualifiedName~StructureAwareFuzzTests" \
  --logger "console;verbosity=normal" 2>&1 | tee "$RUN_LOG"
run_status=${PIPESTATUS[0]}
set -e
run_output="$(cat "$RUN_LOG")"

if grep -q "No test matches the given testcase filter" <<<"$run_output"; then
  echo
  echo "FAIL: the filter matched NO tests — StructureAwareFuzzTests was renamed,"
  echo "      moved, or removed. dotnet test exits 0 in that case, so this would"
  echo "      otherwise be a green sweep that swept nothing."
  exit 1
fi

if [ "$run_status" -ne 0 ]; then
  echo
  echo "FAIL: deep fuzz sweep at depth $DEPTH found a defect (or the run did not"
  echo "      complete cleanly, exit $run_status). The failure message above names"
  echo "      the seed, iteration and fixture — restore EXCISE_FUZZ_ITERATIONS to at"
  echo "      least that iteration number and re-run with the same seed to"
  echo "      reproduce exactly."
  exit 1
fi

echo
echo "==> deep fuzz sweep OK at depth $DEPTH — no defects found this sweep"
echo "    (a green run is a regression guard at this depth, not proof none remain)"
