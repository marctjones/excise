#!/usr/bin/env bash
# Fresh-process excise/reference renderer comparison for #1207 and #1208.
# This intentionally never caches a timed excise result or reference render.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
CONFIG="${CONFIG:-Release}"
OUT="${EXCISE_REFERENCE_PERF_OUTPUT_DIR:-logs/reference-performance/latest}"
RUNS="${EXCISE_REFERENCE_PERF_RUNS:-3}"
dotnet build tools/Excise.RenderTools/Excise.RenderTools.csproj -c "$CONFIG" --nologo -v quiet
dotnet "tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools.dll" reference-performance \
  --fixtures tests/reference-performance/fixtures.json --output-dir "$OUT" --runs "$RUNS" \
  --oracles all --include-heavy "$@"
