#!/usr/bin/env bash
# check-advance-parity.sh — #1104 gate wrapper. Runs the per-glyph
# advance-parity ratchet (excise letters vs mutool stext positions) over the
# smoke/sample (embedded /Widths) and redaction-synthetic (standard-14) corpora.
#
# Fail-closed on a provisioned machine, like check-extraction-parity.sh: a gate
# that silently skips is the bug it exists to fix. The synthetic corpus is
# generated here (cheap, deterministic) so it is always present.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"; cd "$ROOT"

command -v mutool >/dev/null 2>&1 || {
  echo "FAIL: mutool not found on PATH. Install mupdf-tools."; exit 1; }
[[ -n "$(ls -A test-pdfs/smoke/*.pdf 2>/dev/null)" ]] || {
  echo "FAIL: no smoke corpus. Run ./scripts/download-smoke-corpus.sh"; exit 1; }

# Standard-14 half of the ruler — regenerate so the gate always has it.
python3 scripts/gen-redaction-corpus.py >/dev/null 2>&1 || {
  echo "FAIL: could not generate the standard-14 corpus (gen-redaction-corpus.py)"; exit 1; }

MODE="${1:-}"
if [[ "$MODE" == "--update" ]]; then export ADVANCE_PARITY_UPDATE=1; fi

echo "==> advance-parity ratchet (excise vs mutool glyph positions)"
scripts/assert-fresh.sh --configuration Debug Excise.Rendering.Tests 2>/dev/null || true
dotnet test Excise.Rendering.Tests --no-build -c Debug \
  --filter "FullyQualifiedName~AdvanceParityTests" \
  --logger "console;verbosity=normal"
