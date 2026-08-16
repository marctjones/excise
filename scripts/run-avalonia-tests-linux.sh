#!/usr/bin/env bash
#
# Linux CI runner for Excise.Avalonia.Tests (#752).
#
# On the displayless Linux runner the suite now runs green under xvfb-run +
# HeadlessSessionGuard (62/62 as of PR #764), but the test host still EXITS
# NON-ZERO on native teardown: Avalonia/SkiaSharp headless cleanup segfaults
# on process shutdown AFTER every test has passed. The guard converts
# *managed* failures to skips; it cannot intercept a *native* crash in
# process teardown. So the step was red on the exit code with zero test
# failures.
#
# This script trusts the TEST OUTCOME over the teardown exit code — but only
# when the TRX positively proves the run was green:
#
#   1. Run the suite with a TRX logger and capture the exit code.
#   2. Exit code 0 → pass (nothing to tolerate).
#   3. Exit code non-zero → parse the TRX. PASS only if it positively shows
#      >0 passed, 0 failed, and every result outcome is Passed or NotExecuted
#      (skips are the guard's by-design output). Anything else — a Failed
#      outcome, an empty/missing/unparseable TRX, no ResultSummary — FAILS
#      with the real exit code. Default is FAIL: the tolerance must never
#      mask a genuine test failure.
#
# Linux-only by design: macOS and Windows exit cleanly and stay strict on the
# raw `dotnet test` exit code in ci.yml. Invoke this under `xvfb-run -a`
# (same #752 precedent as the Excise.App.Tests step).
#
# Usage:
#   scripts/run-avalonia-tests-linux.sh              # run suite with tolerance
#   scripts/run-avalonia-tests-linux.sh --self-test  # prove the TRX verdict
#                                                    # logic on synthetic
#                                                    # fixtures (no dotnet run)
#   scripts/run-avalonia-tests-linux.sh --check-trx <file>
#                                                    # verdict on an existing
#                                                    # TRX (debugging aid)
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

# trx_verdict <trx-file>
# Exit 0 iff the TRX positively confirms an all-green run. Conservative:
# any parse problem, any non-Passed/NotExecuted outcome, zero passed tests,
# missing counters, or a non-zero failed/error/timeout/aborted counter fails.
trx_verdict() {
  python3 - "$1" <<'PY'
import sys, xml.etree.ElementTree as ET

path = sys.argv[1]
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}

def fail(msg):
    print(f"TRX verdict: NOT GREEN — {msg}")
    sys.exit(1)

try:
    root = ET.parse(path).getroot()
except Exception as e:  # missing, truncated, or malformed — never tolerate
    fail(f"cannot parse {path}: {e}")

outcomes = [r.get("outcome") or "(none)"
            for r in root.iter()
            if r.tag.endswith("UnitTestResult")]
if not outcomes:
    fail("no test results recorded")

# Skips (NotExecuted) are the HeadlessSessionGuard's by-design output and are
# budgeted separately (#619). Everything else non-Passed is a failure.
bad = sorted(set(o for o in outcomes if o not in ("Passed", "NotExecuted")))
if bad:
    fail(f"non-green outcome(s) {bad} among {len(outcomes)} results")

passed = sum(o == "Passed" for o in outcomes)
if passed == 0:
    fail("zero tests passed")

counters = root.find(".//t:ResultSummary/t:Counters", ns)
if counters is None:
    fail("no ResultSummary/Counters — run did not complete")
nonzero = {k: counters.get(k)
           for k in ("failed", "error", "timeout", "aborted")
           if counters.get(k, "0") not in ("0", None)}
if nonzero:
    fail(f"counters report {nonzero}")

skipped = len(outcomes) - passed
print(f"TRX verdict: GREEN — {passed} passed, {skipped} skipped, 0 failed")
PY
}

self_test() {
  local fixtures="$TMP/fixtures" rc=0
  mkdir -p "$fixtures"

  trx_head='<?xml version="1.0" encoding="utf-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>'
  trx_result() { printf '    <UnitTestResult testName="%s" outcome="%s" />\n' "$1" "$2"; }
  trx_tail() {
    printf '  </Results>\n  <ResultSummary outcome="%s">\n' "$1"
    printf '    <Counters total="%s" executed="%s" passed="%s" failed="%s" error="0" timeout="0" aborted="0" notExecuted="%s" />\n' \
      "$2" "$3" "$4" "$5" "$6"
    printf '  </ResultSummary>\n</TestRun>\n'
  }

  { echo "$trx_head"
    trx_result A Passed; trx_result B Passed; trx_result C NotExecuted
    trx_tail Completed 3 2 2 0 1
  } > "$fixtures/all-passed.trx"

  { echo "$trx_head"
    trx_result A Passed; trx_result B Failed
    trx_tail Failed 2 2 1 1 0
  } > "$fixtures/one-failed.trx"

  { echo "$trx_head"
    trx_tail Completed 0 0 0 0 0
  } > "$fixtures/no-results.trx"

  { echo "$trx_head"
    trx_result A NotExecuted
    trx_tail Completed 1 0 0 0 1
  } > "$fixtures/skips-only.trx"

  # No ResultSummary at all — a run that never completed.
  { echo "$trx_head"
    trx_result A Passed
    printf '  </Results>\n</TestRun>\n'
  } > "$fixtures/no-summary.trx"

  echo "not xml at all" > "$fixtures/garbage.trx"

  expect() { # expect <pass|fail> <fixture>
    local want="$1" fixture="$2" got
    if trx_verdict "$fixtures/$fixture"; then got=pass; else got=fail; fi
    if [[ "$got" == "$want" ]]; then
      echo "  ok: $fixture -> $got"
    else
      echo "  SELF-TEST FAILURE: $fixture expected $want, got $got"
      rc=1
    fi
  }

  echo "==> self-testing TRX verdict logic"
  expect pass all-passed.trx
  expect fail one-failed.trx
  expect fail no-results.trx
  expect fail skips-only.trx
  expect fail no-summary.trx
  expect fail garbage.trx
  expect fail missing.trx

  if [[ $rc -ne 0 ]]; then
    echo "FAIL: TRX verdict self-test failed — do not trust the tolerance."
    exit 1
  fi
  echo "==> self-test passed: tolerance passes only a positively-green TRX"
}

if [[ "${1:-}" == "--self-test" ]]; then
  self_test
  exit 0
fi

if [[ "${1:-}" == "--check-trx" ]]; then
  trx_verdict "${2:?usage: run-avalonia-tests-linux.sh --check-trx <file>}"
  exit $?
fi

# Always prove the verdict logic before relying on it for a real run.
self_test

# Coverage collection (#909): opt-in and written OUTSIDE $TMP, which the trap
# above deletes on exit. Default stays $TMP/results so nothing changes for a
# caller that doesn't ask for coverage. AVALONIA_TEST_COLLECT_COVERAGE=1 adds
# the coverlet collector flag; the caller is responsible for reading the
# cobertura report back out of AVALONIA_TEST_RESULTS_DIR afterward.
RESULTS_DIR="${AVALONIA_TEST_RESULTS_DIR:-$TMP/results}"
mkdir -p "$RESULTS_DIR"
COLLECT_ARGS=()
if [[ "${AVALONIA_TEST_COLLECT_COVERAGE:-0}" == "1" ]]; then
  COLLECT_ARGS+=(--collect:"XPlat Code Coverage")
fi

echo "==> running Excise.Avalonia.Tests (TRX-verified, native-teardown tolerant)"
rc=0
dotnet test "$ROOT/Excise.Avalonia.Tests" --no-build -c Debug \
  --logger "console;verbosity=normal" \
  --logger "trx;LogFileName=avalonia.trx" \
  --results-directory "$RESULTS_DIR" \
  "${COLLECT_ARGS[@]}" || rc=$?

if [[ $rc -eq 0 ]]; then
  echo "==> Excise.Avalonia.Tests passed with clean exit"
  exit 0
fi

echo "==> test host exited $rc — checking whether the TRX shows a green run"
if trx_verdict "$RESULTS_DIR/avalonia.trx"; then
  echo "==> all recorded tests passed; ignoring native-teardown exit $rc (#752)"
  exit 0
fi

echo "==> real failure: TRX does not confirm a green run — failing with exit $rc"
exit "$rc"
