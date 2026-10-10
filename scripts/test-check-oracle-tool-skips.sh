#!/usr/bin/env bash
#
# Self-test for check-oracle-tool-skips.sh (#1781, #1012): proves the gate can
# FAIL before its green is trusted. Synthetic trx files in mktemp -d; no build,
# no test run, milliseconds.
#
#   * a clean run (passes + a corpus-absence skip that merely names veraPDF's
#     corpus) is green -- the baseline, and the corpus exclusion;
#   * each real tool-absence reason shape from Excise.Core.Tests is red, and the
#     failure names the test: "mutool is not on PATH", "veraPDF not installed",
#     "qpdf and mutool must be on PATH", and a reworded "pdfimages unavailable";
#   * a tool skip in ONE chunk of a union is red;
#   * a missing trx, an empty trx, a torn trx, a zero-result trx and no --trx
#     at all are each red, never "no skips".
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
GATE="$HERE/check-oracle-tool-skips.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# trx <file> then lines "outcome|testName|reason" on stdin.
trx() {
  local out="$1"
  {
    echo '<?xml version="1.0" encoding="utf-8"?>'
    echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>'
    while IFS='|' read -r outcome name reason; do
      [[ -n "$outcome" ]] || continue
      if [[ -n "$reason" ]]; then
        printf '<UnitTestResult testName="%s" outcome="%s"><Output><ErrorInfo><Message>%s</Message></ErrorInfo></Output></UnitTestResult>\n' \
          "$name" "$outcome" "$reason"
      else
        printf '<UnitTestResult testName="%s" outcome="%s" />\n' "$name" "$outcome"
      fi
    done
    echo '</Results></TestRun>'
  } > "$out"
}

expect_green() {
  local stage="$1"; shift
  "$GATE" "$@" >"$WORK/$stage.log" 2>&1 || { cat "$WORK/$stage.log" >&2; fail "$stage: expected green"; }
}
expect_red() {
  local stage="$1" needle="$2"; shift 2
  if "$GATE" "$@" >"$WORK/$stage.log" 2>&1; then
    cat "$WORK/$stage.log" >&2; fail "$stage: expected red, the gate passed"
  fi
  grep -qF -- "$needle" "$WORK/$stage.log" || { cat "$WORK/$stage.log" >&2; fail "$stage: red, but did not name '$needle'"; }
}

trx "$WORK/clean.trx" <<'EOF'
Passed|Core.RecoveryOracleTests.HiddenTextRecovery_IsCorroboratedByTwoIndependentExtractors|
Passed|Core.PdfUaVeraPdfCrossCheckTests.ExciseVerdict_AgreesWithVeraPdf_OnUntaggedFixture|
NotExecuted|Core.RealPdfTests.Opens|veraPDF corpus not present [excise-searched: /abs/test-pdfs/verapdf-corpus]
NotExecuted|Core.RecapProblemDocumentTests.Reproduces|recap_neg.pdf is unavailable, so #1670 cannot be reproduced.
EOF
expect_green baseline --trx "$WORK/clean.trx"

n=0
while IFS='|' read -r name reason; do
  n=$((n + 1))
  { echo "Passed|Core.Other.Fine|"; echo "NotExecuted|$name|$reason"; } | trx "$WORK/tool$n.trx"
  expect_red "tool$n" "$name" --trx "$WORK/tool$n.trx"
done <<'EOF'
Core.RecoveryOracleTests.HiddenTextRecovery_IsCorroboratedByTwoIndependentExtractors|mutool is not on PATH
Core.PdfUaVeraPdfCrossCheckTests.ExciseVerdict_AgreesWithVeraPdf_OnUntaggedFixture|veraPDF not installed (~/verapdf/verapdf or PATH)
Core.PdfDocumentOptimizerTests.Encrypts|qpdf and mutool must be on PATH: the verdict comes from independent readers
Core.RecoveryOracleTests.ImageUnderBox_TheXObjectSurvives_ConfirmedByPdfimages|pdfimages unavailable
EOF

# One chunk of a union carries the tool skip; the others are clean.
{ echo "NotExecuted|Core.FailureModeChannelTests.InvisibleTextRenderMode3_IsRecovered|mutool is not on PATH"; } \
  | trx "$WORK/chunk2.trx"
expect_red union "InvisibleTextRenderMode3_IsRecovered" --trx "$WORK/clean.trx" --trx "$WORK/chunk2.trx"

# Vacuous inputs are failures, never "no skips".
expect_red missing "no trx at" --trx "$WORK/does-not-exist.trx"
: > "$WORK/empty.trx"
expect_red empty "no trx at" --trx "$WORK/clean.trx" --trx "$WORK/empty.trx"
printf '<?xml version="1.0"?><TestRun><Results><UnitTestResult' > "$WORK/torn.trx"
expect_red torn "not a readable trx" --trx "$WORK/torn.trx"
trx "$WORK/zero.trx" < /dev/null
expect_red zero "ZERO results" --trx "$WORK/zero.trx"
expect_red no-args "no --trx given"

echo "PASS: check-oracle-tool-skips fails on every tool-absence skip shape, in one chunk of a union," \
     "and on missing/empty/torn/zero-result input; a corpus skip naming veraPDF stays green (#1781)"
