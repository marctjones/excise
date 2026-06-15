#!/usr/bin/env bash
# Monitor a long-running exploratory corpus scan. Intended for the second
# pane of scripts/run-exploratory-corpus-tmux.sh.

set -euo pipefail

LOG_DIR="${1:-}"
if [[ -z "$LOG_DIR" || ! -d "$LOG_DIR" ]]; then
    echo "usage: scripts/monitor-exploratory-corpus.sh <log-dir>" >&2
    exit 2
fi

interval="${PDFE_MONITOR_INTERVAL:-15}"

while true; do
    clear 2>/dev/null || true
    echo "pdfe exploratory corpus monitor"
    echo "time: $(date)"
    echo "logs: $LOG_DIR"
    echo

    if [[ -f "$LOG_DIR/done" ]]; then
        echo "run marker: done"
        [[ -f "$LOG_DIR/exit.log" ]] && cat "$LOG_DIR/exit.log"
        echo
    fi

    echo "recent runner output:"
    if [[ -f "$LOG_DIR/run.log" ]]; then
        tail -20 "$LOG_DIR/run.log"
    else
        echo "  run.log not created yet"
    fi
    echo

    echo "active render/reference processes:"
    if ps_output=$(ps -axo pid,ppid,%cpu,rss,etime,command 2>&1); then
        printf '%s\n' "$ps_output" |
            awk 'NR == 1 || /Pdfe\.Cli|\/pdfe( |$)|mutool|pdftocairo|dotnet build/ { print }' |
            head -30
    else
        echo "  unavailable: $ps_output"
    fi
    echo

    echo "chunk log freshness:"
    for log in "$LOG_DIR"/exploratory-chunk-*.log; do
        [[ -e "$log" ]] || continue
        bytes=$(wc -c < "$log" | tr -d ' ')
        lines=$(wc -l < "$log" | tr -d ' ')
        if stat -f "%m" "$log" >/dev/null 2>&1; then
            mtime=$(stat -f "%m" "$log")
        else
            mtime=$(stat -c "%Y" "$log")
        fi
        age=$(( $(date +%s) - mtime ))
        printf '  %-34s %7s bytes %5s lines age %4ss\n' "$(basename "$log")" "$bytes" "$lines" "$age"
    done
    echo

    echo "partial chunk result files:"
    find Pdfe.Rendering.Tests/bin/Debug/net10.0 -maxdepth 1 -name 'exploratory-chunk-*.json' -print 2>/dev/null |
        sort |
        sed 's#^#  #' |
        tail -20
    echo

    [[ -f "$LOG_DIR/done" ]] && break
    sleep "$interval"
done
