#!/usr/bin/env bash
#
# Reduce one runner log directory (plan.tsv + ledger.jsonl) to the gate report
# and exit with its verdict: 0 PASS, 1 NEW/STALE/INVALID, 3 NOT RUN, 2 unreadable.
# Runs nothing; the only process it may spawn is `gh issue view`.
#
# Usage:
#   scripts/report-gates.sh [LOG_DIR|--latest] [--full] [--no-gh]
#
# LOG_DIR is resolved against the CALLER's cwd (no cd here, unlike the other
# wrappers: they take no path arguments). The body, scripts/report_gates.py,
# anchors every repo path on its own location.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec python3 "$ROOT/scripts/report_gates.py" "$@"
