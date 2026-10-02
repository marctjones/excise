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
# The binary this gate MEASURES is excise-cli, which RenderTools launches as a subprocess and does not
# reference. Build it in the SAME configuration here: the full tier builds only the tier's configuration
# (Debug), so a Release run measured whatever Release CLI was last built by hand, and reported its number.
# The 3.15.0 full run read nested-forms-dct at 1.893x from a CLI built on 2026-09-27, five days before the
# fix that brings it to 0.918x.
dotnet build Excise.Cli/Excise.Cli.csproj -c "$CONFIG" --nologo -v quiet
# #1386/#1387 — compare against the committed baseline when one exists, so a run answers
# "is this faster than the last recorded state" rather than only "how fast is it".
# By default the gate REPORTS; pass --fail-on-regression to enforce it. tests/gates.tsv
# enforces it in tier full (2026-09-13): render ms relative to the same-run oracles, and
# peak RSS relative to the baseline. Absolute ms and wall stay report-only (load-specific).
have() { for arg in "$@"; do [ "$arg" = "$WANT" ] && return 0; done; return 1; }

BASELINE_ARGS=()
WANT="--baseline"
if [ -f tests/reference-performance/baseline.json ] && ! have "$@"; then
    BASELINE_ARGS=(--baseline tests/reference-performance/baseline.json)
fi

# Only supply a default the caller did not. System.CommandLine rejects a repeated option,
# so unconditionally appending --runs/--oracles made every override ("--oracles mutool" for
# a fast dev loop) fail to parse and silently print help instead of running anything.
DEFAULTS=()
WANT="--runs";    have "$@" || DEFAULTS+=(--runs "$RUNS")
WANT="--oracles"; have "$@" || DEFAULTS+=(--oracles all)

dotnet "tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools.dll" reference-performance \
  --fixtures tests/reference-performance/fixtures.json --output-dir "$OUT" \
  ${DEFAULTS[@]+"${DEFAULTS[@]}"} --include-heavy ${BASELINE_ARGS[@]+"${BASELINE_ARGS[@]}"} "$@"
