#!/usr/bin/env bash
# profile-render.sh — "where is the render time going?" in one command (#1351).
#
# Wraps `dotnet-trace collect` around a Release `excise render` and prints the
# top self and inclusive frames from the resulting speedscope profile.
#
# Why this and not something else:
#   - Excise.Benchmarks (BenchmarkDotNet) and the reference-performance bench
#     time whole operations/processes; neither names a hot METHOD.
#   - macOS `sample` cannot resolve JIT frames (a wall of `??? (in <unknown binary>)`).
#   - dotnet-trace's speedscope export is an EVENTED profile (open/close
#     events), not sampled; self/inclusive time needs a walk of
#     profiles[*].events, which lives in scripts/profile_render_analyze.py
#     so it is not rediscovered per investigation.
#
# Usage:
#   scripts/profile-render.sh <pdf> [page=1] [dpi=150] [-- extra analyzer args]
#     e.g. scripts/profile-render.sh test-pdfs/sample-pdfs/acc-global-compensation-report.pdf 1 150
#          scripts/profile-render.sh file.pdf 1 72 -- --top 25 --chains 'Lattice4DToRgb'
#
# Environment:
#   PROFILE_OUT_DIR   output directory (default logs/profile-render/<timestamp>)
#   PROFILE_NO_BUILD  =1 to skip the Release build of Excise.Cli
#
# Output: <out>/render.nettrace, <out>/render.speedscope.json (open it at
# https://www.speedscope.app for a flame graph), <out>/render.png,
# <out>/report.txt, <out>/stacks.collapsed.txt.
#
# Timings from this are only as quiet as the machine: the report records
# `uptime` so a reader can discount a loaded run. Proportions and call
# attribution survive load; absolute milliseconds do not.
#
# Prerequisite: `dotnet tool install -g dotnet-trace` (reported by
# scripts/check-test-prereqs.sh). Nothing is downloaded by this script.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

usage() { sed -n '2,33p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; }

if [[ $# -lt 1 || "$1" == "-h" || "$1" == "--help" ]]; then
    usage
    [[ $# -lt 1 ]] && exit 2 || exit 0
fi

PDF="$1"; shift
PAGE=1
DPI=150
if [[ $# -gt 0 && "$1" != "--" ]]; then PAGE="$1"; shift; fi
if [[ $# -gt 0 && "$1" != "--" ]]; then DPI="$1"; shift; fi
[[ $# -gt 0 && "$1" == "--" ]] && shift
ANALYZER_ARGS=("$@")

[[ -f "$PDF" ]] || { echo "profile-render: no such file: $PDF" >&2; exit 2; }
PDF="$(cd "$(dirname "$PDF")" && pwd)/$(basename "$PDF")"

# dotnet global tools install to ~/.dotnet/tools, which is often not on PATH.
TRACE="$(command -v dotnet-trace 2>/dev/null || true)"
if [[ -z "$TRACE" && -x "$HOME/.dotnet/tools/dotnet-trace" ]]; then
    TRACE="$HOME/.dotnet/tools/dotnet-trace"
fi
if [[ -z "$TRACE" ]]; then
    echo "profile-render: dotnet-trace not found (install: dotnet tool install -g dotnet-trace)" >&2
    exit 77
fi
command -v python3 >/dev/null 2>&1 || { echo "profile-render: python3 not found" >&2; exit 77; }

CLI="$ROOT/Excise.Cli/bin/Release/net10.0/excise"
if [[ "${PROFILE_NO_BUILD:-0}" != "1" ]]; then
    dotnet build "$ROOT/Excise.Cli/Excise.Cli.csproj" -c Release --nologo -v quiet >/dev/null
fi
[[ -x "$CLI" ]] || { echo "profile-render: Release CLI missing at $CLI" >&2; exit 1; }

OUT="${PROFILE_OUT_DIR:-$ROOT/logs/profile-render/$(date +%Y%m%d_%H%M%S)}"
mkdir -p "$OUT"

{
    echo "profile-render: $PDF page $PAGE @ $DPI dpi"
    echo "commit: $(git -C "$ROOT" rev-parse --short HEAD 2>/dev/null || echo unknown)$(git -C "$ROOT" diff --quiet 2>/dev/null || echo ' (dirty)')"
    echo "load:   $(uptime)"
    echo "trace:  $("$TRACE" --version 2>/dev/null | head -1)"
    echo
} | tee "$OUT/report.txt"

"$TRACE" collect --format speedscope -o "$OUT/render.nettrace" -- \
    "$CLI" render "$PDF" --page "$PAGE" --dpi "$DPI" -o "$OUT/render.png" --json \
    > "$OUT/collect.log" 2>&1 || {
        echo "profile-render: dotnet-trace collect failed; see $OUT/collect.log" >&2
        exit 1
    }

SPEEDSCOPE="$OUT/render.speedscope.json"
[[ -s "$SPEEDSCOPE" ]] || { echo "profile-render: no speedscope output; see $OUT/collect.log" >&2; exit 1; }

# The CLI's own phase timings (--json), when present in the collect log.
grep -E '"(openMs|renderMs|writeMs|runtimeMode)"' "$OUT/collect.log" | sed 's/^ */  /' | tee -a "$OUT/report.txt" || true
echo | tee -a "$OUT/report.txt"

python3 "$ROOT/scripts/profile_render_analyze.py" "$SPEEDSCOPE" \
    --dump-stacks "$OUT/stacks.collapsed.txt" \
    ${ANALYZER_ARGS[@]+"${ANALYZER_ARGS[@]}"} | tee -a "$OUT/report.txt"

echo
echo "profile-render: wrote $OUT"
