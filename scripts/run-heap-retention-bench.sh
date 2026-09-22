#!/usr/bin/env bash
# Heap retention bench (#1481 / #1469): report-only committed/live/RSS
# measurement across open -> index -> pre-warm -> page -> close cycles, so GC
# options can be A/B'd without a GUI. Not a gate — it asserts nothing and
# normally sits NotExecuted (SkipUnless) inside Excise.App.Tests. See
# Excise.App.Tests/Benchmarks/HeapRetentionReportTests.cs for every
# EXCISE_HEAP_REPORT_* knob.
#
#   scripts/run-heap-retention-bench.sh
#   scripts/run-heap-retention-bench.sh --mode forced-compacting --out logs/heap-report.tsv
#   DOTNET_GCConserveMemory=9 scripts/run-heap-retention-bench.sh
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-Debug}"
OUT=""

while [ $# -gt 0 ]; do
  case "$1" in
    --mode) export EXCISE_HEAP_REPORT_MODE="$2"; shift 2 ;;
    --stage-gc) export EXCISE_HEAP_REPORT_STAGE_GC=1; shift ;;
    --pause-seconds) export EXCISE_HEAP_REPORT_PAUSE_SECONDS="$2"; shift 2 ;;
    --pause-at) export EXCISE_HEAP_REPORT_PAUSE_AT="$2"; shift 2 ;;
    --pause-marker) export EXCISE_HEAP_REPORT_PAUSE_MARKER="$2"; shift 2 ;;
    --survivor-mb) export EXCISE_HEAP_REPORT_SURVIVOR_MB="$2"; shift 2 ;;
    --pdf) export EXCISE_HEAP_REPORT_PDF="$2"; shift 2 ;;
    --cycles) export EXCISE_HEAP_REPORT_CYCLES="$2"; shift 2 ;;
    --pages) export EXCISE_HEAP_REPORT_PAGES="$2"; shift 2 ;;
    --dpi) export EXCISE_HEAP_REPORT_DPI="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

export EXCISE_HEAP_REPORT=1
[ -n "$OUT" ] && export EXCISE_HEAP_REPORT_OUT="$OUT"

scripts/t.sh Excise.App.Tests -c "$CONFIG" \
  --filter "FullyQualifiedName~Excise.App.Tests.Benchmarks.HeapRetentionReportTests" \
  --logger "console;verbosity=detailed"
