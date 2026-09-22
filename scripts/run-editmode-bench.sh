#!/usr/bin/env bash
# Edit-mode switch bench (#1473): report-only latency/click-placement
# measurement for the continuous -> typewriter switch. Not a gate — it asserts
# nothing and normally sits NotExecuted (SkipUnless) inside Excise.App.Tests.
# See Excise.App.Tests/Benchmarks/EditModeSwitchReportTests.cs for what it
# measures and every EXCISE_EDITMODE_* knob.
#
#   scripts/run-editmode-bench.sh --pdf test-pdfs/altona-test-suite-v2/rendering-and-imaging/12_Altona_Technical_v20_x4.pdf
#   scripts/run-editmode-bench.sh --pdf some.pdf --page 3 --reps 5 --label baseline --out logs/editmode-bench.tsv
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${CONFIG:-Debug}"
PDF=""
PAGE=1
REPS=3
LABEL="unlabelled"
OUT=""
DPR=""
OFFSET=""

while [ $# -gt 0 ]; do
  case "$1" in
    --pdf) PDF="$2"; shift 2 ;;
    --page) PAGE="$2"; shift 2 ;;
    --reps) REPS="$2"; shift 2 ;;
    --label) LABEL="$2"; shift 2 ;;
    --out) OUT="$2"; shift 2 ;;
    --dpr) DPR="$2"; shift 2 ;;
    --offset) OFFSET=1; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [ -z "$PDF" ] || [ ! -f "$PDF" ]; then
  echo "usage: $0 --pdf <existing.pdf> [--page N] [--reps N] [--label L] [--out file.tsv] [--dpr N] [--offset]" >&2
  exit 2
fi

export EXCISE_EDITMODE_BENCH=1
export EXCISE_EDITMODE_PDF="$PDF"
export EXCISE_EDITMODE_PAGE="$PAGE"
export EXCISE_EDITMODE_REPS="$REPS"
export EXCISE_EDITMODE_LABEL="$LABEL"
[ -n "$OUT" ] && export EXCISE_EDITMODE_OUT="$OUT"
[ -n "$DPR" ] && export EXCISE_EDITMODE_DPR="$DPR"
[ -n "$OFFSET" ] && export EXCISE_EDITMODE_OFFSET=1

scripts/t.sh Excise.App.Tests -c "$CONFIG" \
  --filter "FullyQualifiedName~Excise.App.Tests.Benchmarks.EditModeSwitchReportTests" \
  --logger "console;verbosity=detailed"
