#!/usr/bin/env bash
#
# Reduce one runner log directory (plan.tsv + ledger.jsonl) to the gate report
# and exit with its verdict: 0 PASS, 1 NEW/STALE, 3 NOT RUN, 2 unreadable.
# Runs nothing; the only process it may spawn is `gh issue view`.
#
# Usage:
#   scripts/report-gates.sh [LOG_DIR|--latest] [--full] [--no-gh]
#
# Body lives in scripts/report_gates.py (same split as check-doc-claim-freshness.sh).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
exec python3 "$ROOT/scripts/report_gates.py" "$@"
