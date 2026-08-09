#!/usr/bin/env bash
#
# Enforce a per-assembly coverage floor for a named PROFILE.
#
# Thin wrapper over check-coverage.sh, which already does the Cobertura parsing
# and the threshold comparison. All this adds is the profile lookup — and the
# profile is the whole point.
#
# WHY A PROFILE
#
# CI and a developer machine run different test populations. Measured on
# Excise.Rendering: 78.4% on the corpus-less runner versus 87.0% locally with
# the corpora and reference renderers present. The 8.6-point gap is Differential
# and Corpus tests. One floor cannot serve both — see tests/coverage-floors.tsv
# for the full reasoning.
#
# Usage:
#   scripts/check-coverage-floor.sh <cobertura.xml> <profile> <assembly>
#   scripts/check-coverage-floor.sh coverage/x.xml ci Excise.Rendering
#
# Profiles: ci | full
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLOORS="$ROOT/tests/coverage-floors.tsv"

if [[ $# -lt 3 ]]; then
    echo "usage: $0 <cobertura.xml> <profile> <assembly>" >&2
    exit 2
fi
REPORT="$1"; PROFILE="$2"; ASSEMBLY="$3"

[[ -f "$FLOORS" ]] || { echo "FAIL: floors file missing: $FLOORS" >&2; exit 1; }
[[ -f "$REPORT" ]] || { echo "FAIL: coverage report missing: $REPORT" >&2; exit 1; }

FLOOR="$(awk -F'\t' -v p="$PROFILE" -v a="$ASSEMBLY" \
    '$0 !~ /^#/ && $1 == p && $2 == a { print $3; exit }' "$FLOORS")"

if [[ -z "$FLOOR" ]]; then
    # An unknown pair is a FAILURE, not a pass. A typo'd assembly name that
    # silently skipped the check would be a gate that reports green while
    # measuring nothing — the same vacuous-pass shape #894 and the skip budget
    # exist to catch.
    echo "FAIL: no floor declared for profile='$PROFILE' assembly='$ASSEMBLY'."
    echo "      Add one to tests/coverage-floors.tsv, or fix the arguments."
    echo "      Declared pairs:"
    awk -F'\t' '$0 !~ /^#/ && NF >= 3 { printf "        %-6s %s (%s)\n", $1, $2, $3 }' "$FLOORS"
    exit 1
fi

echo "==> coverage floor: $ASSEMBLY @ profile '$PROFILE' >= $FLOOR"
exec "$ROOT/scripts/check-coverage.sh" "$REPORT" "$FLOOR" "$ASSEMBLY"
