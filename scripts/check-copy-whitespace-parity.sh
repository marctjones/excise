#!/usr/bin/env bash
# Copy-whitespace parity gate (#837).
#
# Runs CopyWhitespaceParityHarness (whole GUI copy path:
# SortReadingOrder(ColumnAware) -> JoinText(Smart)) over the corpus and compares
# per-document word/line agreement against poppler `pdftotext`, failing when a
# score drops below its checked-in floor in tests/copy-whitespace/floors.json.
# Turns copy-spacing quality (#833/#836) from anecdote into a non-regressing
# measurement, the same posture as scripts/check-extraction-parity.sh.
#
# Usage:
#   scripts/check-copy-whitespace-parity.sh            # gate: fail on regression
#   scripts/check-copy-whitespace-parity.sh --update    # ratchet floors from now
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v pdftotext >/dev/null 2>&1; then
  echo "SKIP: pdftotext (poppler) not on PATH — cannot run the copy-whitespace parity gate."
  echo "      brew install poppler   (or apt-get install poppler-utils)"
  exit 0
fi
if [ ! -d test-pdfs ]; then
  echo "SKIP: test-pdfs/ corpus absent — run scripts/download-test-pdfs.sh."
  exit 0
fi

export COPY_WHITESPACE_PARITY=1
if [ "${1:-}" = "--update" ]; then
  export COPY_WHITESPACE_PARITY_UPDATE=1
  echo "Ratcheting copy-whitespace parity floors from the current measurement…"
fi

dotnet test Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj \
  -c Debug --filter "FullyQualifiedName~CopyWhitespaceParityHarness" \
  --logger "console;verbosity=minimal"
