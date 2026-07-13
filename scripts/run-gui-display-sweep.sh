#!/usr/bin/env bash
#
# GUI display sweep, sharded (#619).
#
# The sweep compares 144 pages of GUI display output against the renderer. As a
# single test inside a SERIAL suite it takes 5-20+ minutes depending entirely on
# machine load — which is how it produced three FALSE reds on 2026-07-13 (twice
# from concurrent test runs, once from ~900MB of accumulated logs/ + artifacts/),
# each time with zero page failures.
#
# The test already supports sharding via PDFE_GUI_DISPLAY_SHARD_COUNT /
# PDFE_GUI_DISPLAY_SHARD_INDEX. Nothing used them. This does.
#
# Each shard is a separate `dotnet test` process, so a shard that is slow or
# wedged cannot drag the others down, and each gets the full deadline budget.
#
# Usage:
#   scripts/run-gui-display-sweep.sh [shard-count]     # default 4
set -euo pipefail

SHARDS="${1:-4}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

FILTER="FullyQualifiedName~PdfViewer_RenderingQualitySuite_DisplayBitmapsMatchRenderer"
LOGDIR="$ROOT/logs/gui-display-sweep"
mkdir -p "$LOGDIR"

echo "==> building once (shards reuse it)"
dotnet build PdfEditor.Tests/PdfEditor.Tests.csproj -v q --nologo >/dev/null

FAILED=0
for ((i = 0; i < SHARDS; i++)); do
  echo "==> shard $((i + 1))/$SHARDS"
  LOG="$LOGDIR/shard-$i.log"

  # Shards run SEQUENTIALLY on purpose. Running them in parallel would recreate
  # exactly the CPU contention that makes this sweep produce false failures —
  # and PdfEditor.Tests is serial by design anyway (SkiaSharp's process-wide
  # native font manager; see #363 / CLAUDE.md).
  if PDFE_GUI_DISPLAY_SHARD_COUNT="$SHARDS" \
     PDFE_GUI_DISPLAY_SHARD_INDEX="$i" \
     dotnet test PdfEditor.Tests/PdfEditor.Tests.csproj \
       --no-build --nologo --filter "$FILTER" >"$LOG" 2>&1
  then
    echo "    PASS  ($(grep -oE 'elapsed [0-9:]+' "$LOG" | tail -1 || echo 'n/a'))"
  else
    echo "    FAIL  -> $LOG"
    grep -E "DEADLINE|failure\(s\)|Error Message" "$LOG" | head -3 | sed 's/^/          /'
    FAILED=1
  fi
done

if [[ $FAILED -eq 0 ]]; then
  echo "==> GUI display sweep OK across $SHARDS shard(s)"
else
  echo "==> GUI display sweep FAILED — see $LOGDIR"
fi
exit $FAILED
