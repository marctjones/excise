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
    current_slice_dir=""
    current_report=""
    if [[ -f "$LOG_DIR/run.log" ]]; then
        current_slice_dir="$(awk -F'slice dir: ' '/slice dir:/ { value=$2 } END { print value }' "$LOG_DIR/run.log")"
        current_report="$(awk '/^  wrote .*\.json$/ { sub(/^  wrote /, ""); value=$0 } END { print value }' "$LOG_DIR/run.log")"
    fi
    if [[ -z "$current_report" && -f "$LOG_DIR/args.txt" ]]; then
        report_name="$(awk 'previous { print; exit } $0 == "--report-name" { previous=1 }' "$LOG_DIR/args.txt")"
        if [[ -n "$report_name" ]]; then
            current_report="Pdfe.Rendering.Tests/bin/Debug/net10.0/$report_name"
        fi
    fi

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
    if [[ -n "$current_slice_dir" && -d "$current_slice_dir" ]]; then
        find "$current_slice_dir" -maxdepth 1 -name 'exploratory-chunk-*.json' -print 2>/dev/null |
            sort |
            sed 's#^#  #' |
            tail -20
    elif [[ -n "$current_slice_dir" ]]; then
        echo "  current slice dir not created yet: $current_slice_dir"
    else
        echo "  slice dir not reported yet"
    fi
    echo

    report=""
    if [[ -n "$current_report" && -f "$current_report" ]]; then
        report="$current_report"
    fi
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
    print(
        f"  slow {elapsed(e):7d}ms {get(e,'status','UNKNOWN'):18s} "
        f"{get(e,'path','')}#p{get(e,'pageNumber',0)} "
        f"mutool={get(e,'mutoolStatus','-') or '-'} "
        f"cairo={get(e,'cairoStatus','-') or '-'} "
        f"gs={get(e,'ghostscriptStatus','-') or '-'} "
        f"pdfbox={get(e,'pdfboxStatus','-') or '-'} "
        f"pdfium={get(e,'pdfiumStatus','-') or '-'}"
    )
fail_status = {
    "TIMEOUT", "MALFORMED_PDF", "UNSUPPORTED_ENCRYPTED",
    "UNSUPPORTED_COMPRESSION", "DECODE_ERROR", "RESOURCE_LIMIT",
    "INVALID_PAGE_GEOMETRY", "INVALID_PAGE_NUMBER", "PARSE_ERROR", "RENDER_ERROR",
    "COMPARE_ERROR", "ALL_ORACLES_REFUSED", "EMPTY_DOC", "RENDER_NULL"
}
shown = 0
for e in entries:
    if get(e, "status") in fail_status:
        msg = get(e, "diagnostic") or get(e, "errorMessage") or ""
        if not msg and get(e, "status") == "ALL_ORACLES_REFUSED":
            msg = (
                f"mutool={get(e, 'mutoolStatus', '-') or '-'}"
                f" ({get(e, 'mutoolError', '') or ''}); "
                f"pdftocairo={get(e, 'cairoStatus', '-') or '-'}"
                f" ({get(e, 'cairoError', '') or ''}); "
                f"ghostscript={get(e, 'ghostscriptStatus', '-') or '-'}"
                f" ({get(e, 'ghostscriptError', '') or ''}); "
                f"pdfbox={get(e, 'pdfboxStatus', '-') or '-'}"
                f" ({get(e, 'pdfboxError', '') or ''}); "
                f"pdfium={get(e, 'pdfiumStatus', '-') or '-'}"
                f" ({get(e, 'pdfiumError', '') or ''})"
            )
        if len(msg) > 100: msg = msg[:97] + "..."
        print(f"  fail {get(e,'status','UNKNOWN'):18s} phase={get(e,'errorPhase','-') or '-':10s} {get(e,'path','')}#p{get(e,'pageNumber',0)} {msg}")
        shown += 1
        if shown >= 8:
            break
PY
        echo
    elif [[ -n "$current_report" ]]; then
        echo "merged report diagnostics: waiting for $current_report"
        echo
    fi

    [[ -f "$LOG_DIR/done" ]] && break
    sleep "$interval"
done
