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

    report=""
    for candidate in \
        Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-all.json \
        Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-sample.json \
        Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report-first.json \
        Pdfe.Rendering.Tests/bin/Debug/net10.0/exploratory-report.json
    do
        if [[ -f "$candidate" ]]; then
            report="$candidate"
            break
        fi
    done
    if [[ -n "$report" ]]; then
        echo "merged report diagnostics: $report"
        python3 - "$report" <<'PY' 2>/dev/null || true
import json, sys
d = json.load(open(sys.argv[1]))
entries = d.get("entries", [])
def get(e, key, default=None):
    return e.get(key, e.get(key[:1].upper() + key[1:], default))
def elapsed(e):
    try: return int(get(e, "elapsedMs") or 0)
    except Exception: return 0
slow = [e for e in entries if elapsed(e) > 0]
slow.sort(key=elapsed, reverse=True)
for e in slow[:5]:
    print(f"  slow {elapsed(e):7d}ms {get(e,'status','UNKNOWN'):18s} {get(e,'path','')}#p{get(e,'pageNumber',0)}")
fail_status = {"TIMEOUT","PARSE_ERROR","DECODE_ERROR","RENDER_ERROR","COMPARE_ERROR","ALL_ORACLES_REFUSED"}
shown = 0
for e in entries:
    if get(e, "status") in fail_status:
        msg = get(e, "diagnostic") or get(e, "errorMessage") or ""
        if len(msg) > 100: msg = msg[:97] + "..."
        print(f"  fail {get(e,'status','UNKNOWN'):18s} phase={get(e,'errorPhase','-') or '-':10s} {get(e,'path','')}#p{get(e,'pageNumber',0)} {msg}")
        shown += 1
        if shown >= 8:
            break
PY
        echo
    fi

    [[ -f "$LOG_DIR/done" ]] && break
    sleep "$interval"
done
