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
# Excise.Rendering: 54.36% on the corpus-less CI runner versus 87.49% locally
# with the corpora and reference renderers present. One floor cannot serve both.
#
# ⚠️ A `ci` NUMBER MUST BE READ OFF A CI RUN. Applying CI's test filter on a
# dev machine measures 78.41% for the same ~467 tests, because 86 of them are
# corpus-gated and SKIP on CI without announcing themselves as filtered out.
# This file's first version carried that 78.41% as the ci floor and turned CI
# red for four commits. See tests/coverage-floors.tsv for the full reasoning.
#
# Usage:
#   scripts/check-coverage-floor.sh <cobertura.xml> <profile> <assembly>
#   scripts/check-coverage-floor.sh coverage/x.xml ci Excise.Rendering
#
# Profiles: ci | full
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
FLOORS="$ROOT/tests/coverage-floors.tsv"

UPDATE=0
ARGS=()
for a in "$@"; do
    case "$a" in
        --update) UPDATE=1 ;;
        *) ARGS+=("$a") ;;
    esac
done

if [[ ${#ARGS[@]} -lt 3 ]]; then
    echo "usage: $0 <cobertura.xml> <profile> <assembly> [--update]" >&2
    echo "  --update  raise the floor when coverage IMPROVED; never lowers it." >&2
    exit 2
fi
REPORT="${ARGS[0]}"; PROFILE="${ARGS[1]}"; ASSEMBLY="${ARGS[2]}"

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

if [[ "$UPDATE" != "1" ]]; then
    exec "$ROOT/scripts/check-coverage.sh" "$REPORT" "$FLOOR" "$ASSEMBLY"
fi

# ── --update: the RATCHET half of #909 ───────────────────────────────────────
#
# Without this a floor only ever protects against decline. Coverage improves,
# nothing records it, and the gap between measured and floor widens until the
# gate is guarding a number nobody has met in months — which is the same
# "declared, plausible, inert" shape #909 was filed about, one step later.
#
# Rules, and each exists because the opposite is a way to lose the gate:
#
#   * NEVER LOWERS. A drop is a regression and must fail the check, not quietly
#     rewrite the floor to match. `--update` after a regression is how a ratchet
#     becomes a rubber stamp.
#   * Raises only on a MATERIAL gain (>= 1 point above the current floor's
#     headroom), so ordinary run-to-run wobble does not tighten the floor until
#     it flaps.
#   * Keeps ~2 points of headroom below measured, matching how floors were
#     chosen by hand.
#   * Rewrites the `measured` column either way, so the file records what was
#     last observed even when the floor does not move.
"$ROOT/scripts/check-coverage.sh" "$REPORT" "$FLOOR" "$ASSEMBLY" || exit 1

MEASURED="$(python3 - "$REPORT" "$ASSEMBLY" <<'PY'
import re, sys
xml = open(sys.argv[1], encoding="utf-8", errors="ignore").read()
m = re.search(r'package name="%s"[^>]*line-rate="([^"]+)"' % re.escape(sys.argv[2]), xml)
print(m.group(1) if m else "")
PY
)"
if [[ -z "$MEASURED" ]]; then
    echo "FAIL: --update could not read a line-rate for '$ASSEMBLY' from $REPORT" >&2
    exit 1
fi

python3 - "$FLOORS" "$PROFILE" "$ASSEMBLY" "$MEASURED" <<'PY'
import sys
floors, profile, assembly, measured = sys.argv[1], sys.argv[2], sys.argv[3], float(sys.argv[4])

HEADROOM = 0.02          # keep ~2 points below measured, as hand-chosen floors do
MIN_GAIN = 0.01          # only ratchet on a material gain, so noise cannot flap it

lines = open(floors, encoding="utf-8").read().split("\n")
out, changed, note = [], False, ""
for line in lines:
    parts = line.split("\t")
    if line.startswith("#") or len(parts) < 4 or parts[0] != profile or parts[1] != assembly:
        out.append(line)
        continue
    old_floor = float(parts[2])
    candidate = round(measured - HEADROOM, 4)
    if candidate >= old_floor + MIN_GAIN:
        parts[2] = f"{candidate:.4f}".rstrip("0").rstrip(".")
        note = f"floor {old_floor} -> {parts[2]} (measured {measured:.4f})"
        changed = True
    else:
        note = (f"floor stays {old_floor} (measured {measured:.4f}; "
                f"a raise needs measured >= {old_floor + MIN_GAIN + HEADROOM:.4f})")
    parts[3] = f"{measured:.4f}".rstrip("0").rstrip(".")
    out.append("\t".join(parts))

open(floors, "w", encoding="utf-8").write("\n".join(out))
print(f"    {note}")
print("    measured column updated" + ("; FLOOR RAISED" if changed else ""))
PY
