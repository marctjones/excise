#!/bin/bash
# Copy-whitespace reliability harness (paragraph + list fidelity).
#
# Runs the real selection/copy path (TextSelectionEngine.SortReadingOrder +
# JoinText) over the test-pdfs corpus and compares WORD SPACING and LINE BREAKS
# against poppler pdftotext — the only two dimensions where excise does not
# intend to diverge. Paragraph blank-lines and list indentation are NOT scored
# here (pdftotext emits neither); those are graded by the construction-known
# fixtures in Excise.App.Tests/Unit/CopyWhitespaceModeTests.cs.
#
# Output: prints a per-file agreement table and writes
# tests/copy-whitespace/parity-results.md so the reliability doc's numbers are
# reproducible. Requires pdftotext on PATH and ./scripts/download-test-pdfs.sh
# to have populated test-pdfs/.
set -euo pipefail
cd "$(dirname "$0")/.."

if ! command -v pdftotext >/dev/null 2>&1; then
  echo "pdftotext (poppler) not found on PATH — install it, then re-run." >&2
  exit 1
fi

COPY_WHITESPACE_PARITY=1 dotnet test Excise.Avalonia.Tests/Excise.Avalonia.Tests.csproj \
  --filter "FullyQualifiedName~CopyWhitespaceParityHarness" \
  --logger "console;verbosity=detailed" "$@"

echo
echo "Results written to tests/copy-whitespace/parity-results.md"
