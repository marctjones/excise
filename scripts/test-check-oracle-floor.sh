#!/usr/bin/env bash
#
# Self-test for check-oracle-floor.sh (#1781, #1012): the floor behind
# core-oracles-floor, app-oracles-floor and rendering-oracles-floor had no
# planted-failure proof. Synthetic trx files in mktemp -d; milliseconds.
#
#   * the measured shape (14 passed, floor 14) is green;
#   * every tool gone (the same 14 now NotExecuted, `dotnet test` still exit 0) is red;
#   * one oracle test lost (13 passed, floor 14) is red;
#   * a filter that matched nothing (a trx with zero results) is red;
#   * no trx at all is red.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/check-oracle-floor.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# trx <file> <outcome> <count>
trx() {
  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
    local i
    for ((i = 1; i <= $3; i++)); do
      printf '<UnitTestResult testName="Core.PdfDocumentWriterTests.T%d_WhenAvailable" outcome="%s" />\n' "$i" "$2"
    done
    echo '</Results></TestRun>'
  } > "$1"
}

expect() {
  local want="$1" stage="$2"; shift 2
  if "$GATE" "$@" >"$WORK/$stage.log" 2>&1; then got=green; else got=red; fi
  [[ "$got" == "$want" ]] || { cat "$WORK/$stage.log" >&2; fail "$stage: expected $want, got $got"; }
}

trx "$WORK/measured.trx" Passed 14
expect green measured "$WORK/measured.trx" 14 "core tool-gated"

trx "$WORK/all-skipped.trx" NotExecuted 14
expect red all-skipped "$WORK/all-skipped.trx" 14 "core tool-gated"

trx "$WORK/one-lost.trx" Passed 13
expect red one-lost "$WORK/one-lost.trx" 14 "core tool-gated"

trx "$WORK/filter-empty.trx" Passed 0
expect red filter-empty "$WORK/filter-empty.trx" 14 "core tool-gated"

expect red no-trx "$WORK/never-written.trx" 14 "core tool-gated"

echo "PASS: check-oracle-floor fails when every oracle test skipped, when one is lost," \
     "when the filter matched nothing and when no trx was written (#1781)"
