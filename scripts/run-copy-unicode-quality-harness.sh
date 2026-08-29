#!/usr/bin/env bash
# Copy + Unicode quality harness (#1206).
#
# This is deliberately a small, local evidence gate — not a replacement for
# the broad test suite or a CI job. It joins the tests that protect the actual
# copy path and Unicode display-safety boundary, and refuses a vacuous green
# result when a renamed filter finds zero tests.
#
# Usage:
#   scripts/run-copy-unicode-quality-harness.sh
#   scripts/run-copy-unicode-quality-harness.sh --parity
#
# --parity additionally invokes the Poppler/corpus whitespace comparison. It
# is optional because its external tools and corpus are not universal; its
# script reports an explicit SKIP when prerequisites are unavailable.
set -euo pipefail

cd "$(dirname "$0")/.."

PARITY=0
if [ "${1:-}" = "--parity" ]; then
  PARITY=1
elif [ "$#" -ne 0 ]; then
  echo "Usage: $0 [--parity]" >&2
  exit 2
fi

run_required() {
  local lane="$1"
  local project="$2"
  local filter="$3"
  local log
  log="$(mktemp)"
  trap 'rm -f "$log"' RETURN

  echo
  echo "==> ${lane}"
  # Native AOT/Release packaging deliberately restores Excise.App without
  # Roslyn scripting. That shares the project's obj assets with Debug tests;
  # reestablish the test-only graph here so this harness remains runnable
  # after a release-package smoke.
  if [ "$project" = "Excise.App.Tests/Excise.App.Tests.csproj" ]; then
    dotnet restore "$project" -p:EnableScripting=true
  fi
  set +e
  dotnet test "$project" --no-restore -p:EnableScripting=true --filter "$filter" \
    --logger "console;verbosity=minimal" 2>&1 | tee "$log"
  local status=${PIPESTATUS[0]}
  set -e

  if grep -q "No test matches the given testcase filter" "$log"; then
    echo "FAIL: ${lane} filter matched no tests; refusing a vacuous pass." >&2
    return 1
  fi
  return "$status"
}

echo "Copy and Unicode quality harness"
echo "Raw copied text is tested separately from display-only Unicode safety."

# Actual mouse-selection -> MainWindowViewModel -> ClipboardHistory behavior,
# plus the view-model representation of dangerous invisible controls.
run_required "GUI clipboard and preview safety" \
  Excise.App.Tests/Excise.App.Tests.csproj \
  "FullyQualifiedName~TextSelectionDragTests|FullyQualifiedName~ClipboardEntryUnicodeSafetyTests|FullyQualifiedName~UnicodeTextSafetyTests"

# Arabic/Hebrew logical-order selection remains a pure-engine test until #1203
# adds construction-known mouse-to-clipboard cases.
run_required "RTL logical selection" \
  Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj \
  "FullyQualifiedName~TextSelectionRtlTests"

# Includes Unicode bidi/isolate/joiner/tag display diagnostics, Type0 Identity-V
# extraction baseline, and the policy that vertical pages retain producer order.
run_required "Unicode controls and vertical-writing baseline" \
  Excise.Core.Tests/Excise.Core.Tests.csproj \
  "FullyQualifiedName~TextExtractorType0Tests|FullyQualifiedName~PageTextOrderTests"

echo
echo "Coverage status:"
echo "  PASS: LTR multi-column GUI copy; RTL engine logical order; Unicode preview safety."
echo "  BASELINE ONLY: CJK fixture parsing and Identity-V extraction/order."
echo "  PENDING #1203: RTL/mixed-direction mouse-to-clipboard tests."
echo "  PENDING #1204: CJK and vertical-writing mouse-to-clipboard implementation/tests."
echo "  PENDING #1205: Unicode safety audit beyond clipboard history."

if [ "$PARITY" = "1" ]; then
  echo
  echo "==> Optional Poppler whitespace/line-break parity"
  scripts/check-copy-whitespace-parity.sh
fi

echo
echo "PASS: copy and Unicode quality harness completed."
