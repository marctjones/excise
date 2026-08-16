#!/usr/bin/env bash
#
# Derive-don't-trust gate for numeric/inventory claims in CLAUDE.md (#936).
#
# See scripts/check_doc_claim_freshness.py for the full rationale and the
# explicit "what this deliberately does not check" list. Short version: three
# claims about the test suite were each true when written and wrong by the
# time someone acted on them, all in the direction of doing LESS
# verification. This mechanically re-derives what can be re-derived
# (reference-oracle usage counts, milestone existence) and requires a dated
# measurement marker on numbers that can't be cheaply re-derived in t0.
#
# Implemented in Python (bash 3.2 here has no lookaround/named-group regex);
# this script is just the thin, t0-callable entry point — same split as
# scripts/check-unwired-api.sh / scripts/check_unwired_api.py.
#
# Usage:
#   scripts/check-doc-claim-freshness.sh                  # check (fast, no network)
#   scripts/check-doc-claim-freshness.sh --update          # rewrite oracle usage counts
#   scripts/check-doc-claim-freshness.sh --update-milestones  # refresh milestone baseline (network + gh auth)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
exec python3 "$ROOT/scripts/check_doc_claim_freshness.py" "$@"
