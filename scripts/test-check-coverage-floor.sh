#!/usr/bin/env bash
#
# Selftest for scripts/check-coverage-floor.sh --update (the #909 ratchet).
#
# A ratchet that can be talked into lowering a floor is worse than no ratchet:
# it looks like a guarantee and provides none. The rules are only worth having
# if they are enforced, so this exercises all three branches against synthetic
# Cobertura reports rather than trusting the code to mean what it says.
#
# Runs in t0 — it is sub-second and needs no coverage run.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLOORS="$ROOT/tests/coverage-floors.tsv"
SCRIPT="$ROOT/scripts/check-coverage-floor.sh"

TMP="$(mktemp -d)"
BACKUP="$TMP/floors.orig"
cp "$FLOORS" "$BACKUP"
# Always restore the real file, including on failure — this test mutates it.
trap 'cp "$BACKUP" "$FLOORS"; rm -rf "$TMP"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

mkreport() {   # mkreport <line-rate> <path>
    printf '<?xml version="1.0"?>\n<coverage line-rate="%s">\n<packages>\n<package name="Excise.Rendering" line-rate="%s"/>\n</packages>\n</coverage>\n' \
        "$1" "$1" > "$2"
}

floor_now() {
    awk -F'\t' '$1 == "full" && $2 == "Excise.Rendering" { print $3; exit }' "$FLOORS"
}

BASE="$(floor_now)"
[[ -n "$BASE" ]] || fail "fixture row 'full Excise.Rendering' missing from $FLOORS"

echo "==> selftest: check-coverage-floor.sh --update (base floor $BASE)"

# ── 1. A material gain RAISES the floor and keeps headroom ───────────────────
mkreport 0.9100 "$TMP/high.xml"
cp "$BACKUP" "$FLOORS"
"$SCRIPT" "$TMP/high.xml" full Excise.Rendering --update >/dev/null 2>&1 \
    || fail "a passing run with a material gain should exit 0"
RAISED="$(floor_now)"
awk -v a="$RAISED" -v b="$BASE" 'BEGIN { exit !(a > b) }' \
    || fail "floor should have risen above $BASE, got $RAISED"
awk -v f="$RAISED" 'BEGIN { exit !(f <= 0.89 + 1e-9) }' \
    || fail "floor $RAISED left less than the ~2 points of headroom below 0.91"
echo "    raises on a material gain            $BASE -> $RAISED"

# ── 2. A marginal gain does NOT raise (noise must not tighten the floor) ─────
mkreport 0.8600 "$TMP/near.xml"
cp "$BACKUP" "$FLOORS"
"$SCRIPT" "$TMP/near.xml" full Excise.Rendering --update >/dev/null 2>&1 \
    || fail "a passing run with a marginal gain should still exit 0"
[[ "$(floor_now)" == "$BASE" ]] \
    || fail "a marginal gain must not move the floor (got $(floor_now), expected $BASE)"
echo "    holds on a marginal gain             $BASE"

# ── 3. A REGRESSION fails and must not rewrite anything ──────────────────────
# This is the one that matters. If --update lowered the floor to match a drop,
# the ratchet would launder every regression it was built to catch.
mkreport 0.8000 "$TMP/low.xml"
cp "$BACKUP" "$FLOORS"
set +e
"$SCRIPT" "$TMP/low.xml" full Excise.Rendering --update >/dev/null 2>&1
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "a coverage REGRESSION must exit non-zero even with --update"
diff -q "$BACKUP" "$FLOORS" >/dev/null \
    || fail "a regression must leave the floors file untouched — --update must never lower"
echo "    regression fails, file untouched      exit $RC"

echo "==> check-coverage-floor.sh --update selftest OK"
