#!/usr/bin/env bash
# Launch a long-running exploratory corpus scan in tmux with durable logs
# and a live monitor pane.
#
# Default workload is the release-quality all-pages scan with a larger
# per-PDF timeout than the fast local runner:
#
#   scripts/run-exploratory-corpus-tmux.sh
#
# Pass any run-exploratory-corpus.sh arguments after --:
#
#   scripts/run-exploratory-corpus-tmux.sh -- --page-mode all --pdf-timeout-ms 300000
#
# Attach later with:
#
#   tmux attach -t pdfe-corpus

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$ROOT"

SESSION="pdfe-corpus"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --session) SESSION="$2"; shift 2 ;;
        --) shift; break ;;
        --help|-h)
            sed -n '2,26p' "$0"; exit 0 ;;
        *)
            break ;;
    esac
done

RUN_ARGS=("$@")
if [[ ${#RUN_ARGS[@]} -eq 0 ]]; then
    RUN_ARGS=(--page-mode all --pdf-timeout-ms 120000 --chunk-parallel 2 --per-chunk-parallel 1)
fi

if ! command -v tmux >/dev/null 2>&1; then
    echo "✗ tmux not found on PATH" >&2
    exit 1
fi

if tmux has-session -t "$SESSION" 2>/dev/null; then
    echo "✗ tmux session '$SESSION' already exists" >&2
    echo "  attach: tmux attach -t $SESSION" >&2
    exit 1
fi

STAMP="$(date +%Y%m%d_%H%M%S)"
LOG_DIR="$ROOT/logs/exploratory-corpus_${STAMP}"
mkdir -p "$LOG_DIR"

printf '%s\n' "${RUN_ARGS[@]}" > "$LOG_DIR/args.txt"

RUN_CMD=$(printf '%q ' "$ROOT/scripts/run-exploratory-corpus.sh" "${RUN_ARGS[@]}" --log-dir "$LOG_DIR")
MONITOR_CMD=$(printf '%q ' "$ROOT/scripts/monitor-exploratory-corpus.sh" "$LOG_DIR")

tmux new-session -d -s "$SESSION" -n run -c "$ROOT" \
    "set -o pipefail; $RUN_CMD 2>&1 | tee '$LOG_DIR/run.log'; printf '\nexit=%s\n' \"\$?\" | tee '$LOG_DIR/exit.log'; touch '$LOG_DIR/done'; exec \$SHELL"

tmux split-window -t "$SESSION:run" -h -c "$ROOT" \
    "$MONITOR_CMD 2>&1 | tee '$LOG_DIR/monitor.log'; exec \$SHELL"

tmux select-pane -t "$SESSION:run.0"

cat <<EOF
Started tmux session '$SESSION'.

Run log:     $LOG_DIR/run.log
Monitor log: $LOG_DIR/monitor.log
Chunk logs:  $LOG_DIR/exploratory-chunk-N.log

Attach:
  tmux attach -t $SESSION

Watch logs without attaching:
  tail -f $LOG_DIR/run.log
  tail -f $LOG_DIR/monitor.log
EOF
