#!/usr/bin/env bash
# TOOLING — not a gate (tests/gates-tooling.txt): the trx verdict the two advisory GitHub runners (#1593/#1594) apply to their own runs; locally the same two hazards are covered by lib-runner's zero-test guard and report-gates.sh
#
# WHY THIS EXISTS
#
# Two failure modes make a `dotnet test` exit code useless on a runner, and
# both of them fail TOWARD GREEN:
#
#   1. A `--filter` that matches nothing exits 0. Every platform-specific step
#      in the Windows and Linux workflows is filtered by class name, so a
#      renamed or deleted class turns its step into a silent no-op that still
#      reports success. This is the same hazard scripts/run-full-suite.sh
#      guards locally ("a step matching zero tests is a FAILURE, not a pass" —
#      CLAUDE.md, restartable full runs, property 3). The first draft of that
#      runner had exactly this bug; so would these workflows without this.
#
#   2. The Linux Avalonia/Skia test host exits 1 on NATIVE TEARDOWN after every
#      test has already passed. The deleted scripts/run-avalonia-tests-linux.sh
#      existed for precisely this (#752): a segfault during process shutdown
#      cannot be intercepted from managed code, so the raw exit code reds a
#      green suite. Its verdict was "non-zero exit is acceptable only if the
#      trx positively confirms >0 passed and 0 failed" — that is the verdict
#      below, restated for the runner that replaced it.
#
# So the workflows do not trust `dotnet test`'s exit code for those steps. They
# ask the trx what happened, and this script is the only thing that decides.
#
# USAGE
#
#   scripts/assert-trx-green.sh <file.trx> [more.trx ...] [--min-passed N]
#   scripts/assert-trx-green.sh --self-test
#
# Several trx files are a UNION (a chunked or multi-step run), summed before
# the verdict, the same convention as check-skip-budget.sh --trx.
#
# VERDICT: every named file must exist, be non-empty and carry a <Counters>
# element; and across the union  passed >= --min-passed (default 1)  AND
# failed + error + timeout + aborted + passedButRunAborted == 0.
#
# NotExecuted (skipped) is deliberately NOT part of the verdict: whether a skip
# is legitimate is #1172's question, answered by scripts/check-skip-budget.sh
# against the same trx. This script only answers "did real tests run and did
# they pass".
#
# --self-test runs the verdict against synthetic trx fixtures (green, failed,
# zero-matched, missing, torn) and asserts each outcome. The workflows run it
# before trusting the script, because a checker nobody ever watched fail is a
# checker that cannot fail (see MEMORY "checks that cannot fail" / #1527).
set -uo pipefail

SELF="${BASH_SOURCE[0]}"

# The one reader of a trx in this file. Counters is a single self-closing
# element; newlines are flattened first so its attributes are reachable
# whatever the writer's line breaking.
counter() { # <trx> <attribute>
  tr '\n' ' ' < "$1" \
    | grep -o '<Counters[^>]*>' | head -1 \
    | grep -o "$2=\"[0-9]*\"" | head -1 \
    | grep -o '[0-9][0-9]*'
}

verdict() { # <min-passed> <trx...>  -> 0 green, 1 red (reasons on stdout)
  local min="$1"; shift
  local total=0 passed=0 bad=0 notexec=0 files=0 rc=0

  for f in "$@"; do
    if [ ! -f "$f" ]; then
      echo "  MISSING  $f — the step that should have written it did not run"
      rc=1; continue
    fi
    if [ ! -s "$f" ]; then
      echo "  EMPTY    $f"
      rc=1; continue
    fi
    local c_total c_passed c_failed c_error c_timeout c_aborted c_pbra c_notexec
    c_total="$(counter "$f" total)"
    if [ -z "$c_total" ]; then
      # A trx truncated by a killed host has no Counters. That is a dead host,
      # not a pass (MEMORY: "host death vs real failure").
      echo "  NO-COUNTERS  $f — truncated trx; the test host probably died"
      rc=1; continue
    fi
    c_passed="$(counter "$f" passed)";   c_failed="$(counter "$f" failed)"
    c_error="$(counter "$f" error)";     c_timeout="$(counter "$f" timeout)"
    c_aborted="$(counter "$f" aborted)"; c_pbra="$(counter "$f" passedButRunAborted)"
    c_notexec="$(counter "$f" notExecuted)"
    local this_bad=$(( ${c_failed:-0} + ${c_error:-0} + ${c_timeout:-0} + ${c_aborted:-0} + ${c_pbra:-0} ))
    total=$(( total + c_total ))
    passed=$(( passed + ${c_passed:-0} ))
    notexec=$(( notexec + ${c_notexec:-0} ))
    bad=$(( bad + this_bad ))
    files=$(( files + 1 ))
    printf '  %-9s %s  total=%s passed=%s failed=%s notExecuted=%s\n' \
      "$([ "$this_bad" -eq 0 ] && echo OK || echo FAILING)" \
      "$f" "$c_total" "${c_passed:-0}" "${c_failed:-0}" "${c_notexec:-0}"
  done

  if [ "$files" -eq 0 ]; then
    echo "  no readable trx in the union"
    return 1
  fi
  if [ "$bad" -gt 0 ]; then
    echo "  VERDICT RED: $bad failing/errored/aborted result(s)"
    rc=1
  fi
  if [ "$passed" -lt "$min" ]; then
    echo "  VERDICT RED: $passed passed, expected at least $min — a filter that"
    echo "               matches nothing exits 0, so this is how that shows up"
    rc=1
  fi
  [ "$rc" -eq 0 ] && echo "  VERDICT GREEN: $passed passed, 0 failing, $notexec skipped, across $files trx"
  return "$rc"
}

# ---------------------------------------------------------------------------
# --self-test: the verdict against synthetic trx, one case per way it must fail
# ---------------------------------------------------------------------------
if [ "${1:-}" = "--self-test" ]; then
  TMP="$(mktemp -d "${TMPDIR:-/tmp}/assert-trx-green-selftest.XXXXXX")"
  trap 'rm -rf "$TMP"' EXIT
  FAILS=0
  emit() { # <file> <counters-attrs>
    { echo '<?xml version="1.0" encoding="UTF-8"?>'
      echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
      echo '  <ResultSummary outcome="Completed">'
      echo "    <Counters $1 />"
      echo '  </ResultSummary>'
      echo '</TestRun>'
    } > "$2"
  }
  case_is() { # <label> <expected rc> <args...>
    local label="$1" want="$2"; shift 2
    local out; out="$(verdict "$@" 2>&1)"; local got=$?
    if [ "$got" -eq "$want" ]; then printf 'PASS  %s\n' "$label"
    else printf 'FAIL  %s (rc %s, wanted %s)\n%s\n' "$label" "$got" "$want" "$out"; FAILS=$((FAILS + 1)); fi
  }

  emit 'total="7" executed="7" passed="7" failed="0" error="0" timeout="0" aborted="0" passedButRunAborted="0" notExecuted="0"' "$TMP/green.trx"
  emit 'total="7" executed="7" passed="6" failed="1" error="0" timeout="0" aborted="0" passedButRunAborted="0" notExecuted="0"' "$TMP/failed.trx"
  emit 'total="0" executed="0" passed="0" failed="0" error="0" timeout="0" aborted="0" passedButRunAborted="0" notExecuted="0"' "$TMP/zero.trx"
  emit 'total="9" executed="4" passed="4" failed="0" error="0" timeout="0" aborted="0" passedButRunAborted="0" notExecuted="5"' "$TMP/skips.trx"
  emit 'total="3" executed="3" passed="2" failed="0" error="0" timeout="0" aborted="1" passedButRunAborted="0" notExecuted="0"' "$TMP/aborted.trx"
  printf '<?xml version="1.0"?><TestRun><ResultSum' > "$TMP/torn.trx"
  : > "$TMP/empty.trx"

  case_is 'a green run passes'                                 0 1 "$TMP/green.trx"
  case_is 'one failing test reds it'                           1 1 "$TMP/failed.trx"
  case_is 'ZERO MATCHED TESTS reds it (the exit-0 filter trap)' 1 1 "$TMP/zero.trx"
  case_is 'an aborted result reds it'                          1 1 "$TMP/aborted.trx"
  case_is 'declared skips alone do NOT red it (that is #1172)' 0 1 "$TMP/skips.trx"
  case_is 'a truncated trx reds it (dead host, not a pass)'    1 1 "$TMP/torn.trx"
  case_is 'an empty trx reds it'                               1 1 "$TMP/empty.trx"
  case_is 'a missing trx reds it'                              1 1 "$TMP/absent.trx"
  case_is 'a union sums (green + failing = red)'               1 1 "$TMP/green.trx" "$TMP/failed.trx"
  case_is 'a union sums (green + skips = green)'               0 1 "$TMP/green.trx" "$TMP/skips.trx"
  case_is '--min-passed is enforced against the union'         1 20 "$TMP/green.trx"

  echo
  if [ "$FAILS" -eq 0 ]; then echo "assert-trx-green --self-test: all cases pass"; exit 0; fi
  echo "assert-trx-green --self-test: $FAILS case(s) FAILED"; exit 1
fi

# ---------------------------------------------------------------------------
# normal invocation
# ---------------------------------------------------------------------------
MIN_PASSED=1
TRX=()
while [ $# -gt 0 ]; do
  case "$1" in
    --min-passed) MIN_PASSED="${2:?--min-passed needs a number}"; shift 2 ;;
    --self-test)  echo "assert-trx-green: --self-test takes no other arguments" >&2; exit 2 ;;
    -h|--help)    sed -n '20,40p' "$SELF"; exit 0 ;;
    -*)           echo "assert-trx-green: unknown option: $1" >&2; exit 2 ;;
    *)            TRX+=("$1"); shift ;;
  esac
done

if [ "${#TRX[@]}" -eq 0 ]; then
  echo "usage: assert-trx-green.sh <file.trx> [more.trx ...] [--min-passed N]" >&2
  echo "       assert-trx-green.sh --self-test" >&2
  exit 2
fi

echo "assert-trx-green: ${#TRX[@]} trx, min passed $MIN_PASSED"
verdict "$MIN_PASSED" "${TRX[@]}"
