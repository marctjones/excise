#!/usr/bin/env bash
# Kills any excise-related process (Excise.App, Excise.Cli, any testhost/dotnet
# process hosting an Excise*.dll, Excise.RenderTools, etc.) if its RSS crosses
# a threshold, so a runaway test/render process cannot take the machine down.
#
# Diagnostic note (2026-09-10): every JetsamEvent/crash report on this machine
# over the last 5 days was checked, and no Excise process ever exceeded ~6GB —
# the actual repeat multi-GB offenders were an unrelated project's ("capabledeputy")
# Python MCP-server daemon fleet. This guard is a standing precaution, not a
# response to a confirmed excise leak — if it ever actually fires, that IS new
# evidence worth investigating as a real excise bug.
#
# Usage: scripts/watch-excise-memory.sh [threshold_gb] [poll_seconds]
set -uo pipefail

THRESHOLD_GB="${1:-20}"
POLL_S="${2:-5}"
THRESHOLD_KB=$(( THRESHOLD_GB * 1024 * 1024 ))
LOG="$(dirname "$0")/../logs/excise-memory-guard.log"
mkdir -p "$(dirname "$LOG")"

echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) watch-excise-memory: starting, threshold=${THRESHOLD_GB}GiB poll=${POLL_S}s" | tee -a "$LOG"

while true; do
    # rss is in KB. Match excise-named executables and any dotnet/testhost
    # process whose command line references an Excise project/dll, so a
    # `dotnet exec .../Excise.App.Tests.dll` process is caught too, not just
    # ones whose argv[0] literally says "Excise".
    ps -axo pid=,rss=,command= | while read -r pid rss cmd; do
        case "$cmd" in
            *Excise.App*|*Excise.Cli*|*Excise.Core*|*Excise.Rendering*|*Excise.Avalonia*|*Excise.Ocr*|*Excise.RenderTools*|*excise.app*)
                ;;
            *)
                continue
                ;;
        esac
        [ -n "$rss" ] || continue
        if [ "$rss" -gt "$THRESHOLD_KB" ] 2>/dev/null; then
            gb=$(awk -v r="$rss" 'BEGIN{printf "%.2f", r/1024/1024}')
            echo "$(date -u +%Y-%m-%dT%H:%M:%SZ) KILLING pid=$pid rss=${gb}GiB (over ${THRESHOLD_GB}GiB) cmd=$cmd" | tee -a "$LOG"
            kill -9 "$pid" 2>/dev/null
        fi
    done
    sleep "$POLL_S"
done
