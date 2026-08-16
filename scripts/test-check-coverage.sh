#!/usr/bin/env bash
#
# Selftest for scripts/check-coverage.sh (#1012).
#
# A coverage threshold that cannot fail is a number printed next to a tick. This
# feeds the checker synthetic Cobertura reports on both sides of the threshold
# and requires it to discriminate — including the two shapes that would otherwise
# report success having measured nothing: a package filter that matches no
# package, and a report with no parseable line-rate.
#
# Sub-second, no coverage run. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-coverage.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

mkreport() {   # mkreport <line-rate> <package> <path>
    printf '<coverage line-rate="%s">\n<packages>\n<package name="%s" line-rate="%s"/>\n</packages>\n</coverage>\n' \
        "$1" "$2" "$1" > "$3"
}

run() {   # run <args...>
    set +e
    OUT="$("$SCRIPT" "$@" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-coverage.sh"

mkreport 0.9500 Excise.Core "$TMP/high.xml"
mkreport 0.8000 Excise.Core "$TMP/low.xml"

# ── 1. At or above the threshold passes ──────────────────────────────────────
run "$TMP/high.xml" 0.94
[[ "$RC" -eq 0 ]] || fail "95% must clear a 94% floor (exit $RC)
$OUT"
echo "    95% vs 94% floor                     exit 0"

# ── 2. Below the threshold FAILS. The one that matters. ──────────────────────
run "$TMP/low.xml" 0.94
[[ "$RC" -ne 0 ]] || fail "80% must NOT clear a 94% floor — the gate does not discriminate
$OUT"
grep -q "FAILED" <<<"$OUT" || fail "expected a FAILED verdict:
$OUT"
echo "    80% vs 94% floor                     exit $RC"

# ── 3. Same, scoped to a package ─────────────────────────────────────────────
run "$TMP/low.xml" 0.94 Excise.Core
[[ "$RC" -ne 0 ]] || fail "a package-scoped shortfall must fail too
$OUT"
echo "    80% vs 94% floor (package-scoped)    exit $RC"

# ── 4. A filter that matches NO package must fail, not pass vacuously ────────
run "$TMP/high.xml" 0.94 Excise.Nonexistent
[[ "$RC" -ne 0 ]] || fail "a package filter matching nothing measured nothing — that is not a pass
$OUT"
echo "    package filter matches nothing       exit $RC"

# ── 5. An unparseable report must fail, not pass vacuously ───────────────────
printf '<coverage>\n</coverage>\n' > "$TMP/norate.xml"
run "$TMP/norate.xml" 0.94
[[ "$RC" -ne 0 ]] || fail "a report with no line-rate measured nothing — that is not a pass
$OUT"
echo "    no line-rate in report               exit $RC"

# ── 6. A missing report must fail ────────────────────────────────────────────
run "$TMP/does-not-exist.xml" 0.94
[[ "$RC" -ne 0 ]] || fail "a missing coverage file must fail
$OUT"
echo "    missing report                       exit $RC"

echo "==> check-coverage.sh selftest OK"
