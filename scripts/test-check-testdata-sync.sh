#!/usr/bin/env bash
#
# Selftest for scripts/check-testdata-sync.sh (#678, selftested per #1012).
#
# The gate resolves every "file" reference in test-pdfs/manifests/*.json against
# the working tree. It is driven here inside a hermetic temp root — the script
# derives its ROOT from its own location, so a copy of it in $TMP/scripts sees
# $TMP as the repo — with hand-written manifests instead of the real ones. That
# keeps the real test-pdfs/ untouched and makes every branch a known input.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-testdata-sync.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

R="$TMP/root"
mkdir -p "$R/scripts" "$R/test-pdfs/manifests" "$R/Fake.Core.Tests"
cp "$SCRIPT" "$R/scripts/check-testdata-sync.sh"
: > "$R/Fake.Core.Tests/RealEvidence.cs"

run() {
    set +e
    OUT="$("$R/scripts/check-testdata-sync.sh" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-testdata-sync.sh"

# ── 1. Every reference resolves -> pass ──────────────────────────────────────
cat > "$R/test-pdfs/manifests/evidence.json" <<'JSON'
{ "requirements": [ { "id": "r1", "evidence": [ { "file": "Fake.Core.Tests/RealEvidence.cs" } ] } ] }
JSON
run
[[ "$RC" -eq 0 ]] || fail "a manifest whose references all resolve must pass (exit $RC)
$OUT"
grep -q "1 'file' references" <<<"$OUT" \
    || fail "the gate must report how many references it actually checked:
$OUT"
echo "    all references resolve               exit 0"

# ── 2. A DANGLING reference fails. The guarded property. ─────────────────────
# This is the v3.0 pdfe->Excise rename shape: the manifest still points at a
# path that no longer exists, and nothing compile-checks it.
cat > "$R/test-pdfs/manifests/evidence.json" <<'JSON'
{ "requirements": [ { "id": "r1", "evidence": [ { "file": "Pdfe.Core.Tests/RenamedAway.cs" } ] } ] }
JSON
run
[[ "$RC" -ne 0 ]] || fail "a dangling 'file' reference must fail — that is the whole gate
$OUT"
grep -q "Pdfe.Core.Tests/RenamedAway.cs" <<<"$OUT" \
    || fail "the failure must name the offending reference:
$OUT"
echo "    dangling reference                   exit $RC"

# ── 3. Invalid JSON fails rather than being skipped ──────────────────────────
printf '{ "requirements": [ oops\n' > "$R/test-pdfs/manifests/evidence.json"
run
[[ "$RC" -ne 0 ]] || fail "an unparseable manifest must fail, not be silently skipped
$OUT"
echo "    invalid JSON                         exit $RC"

# ── 4. ZERO manifests is a failure, not a vacuous pass ───────────────────────
rm -f "$R"/test-pdfs/manifests/*.json
run
[[ "$RC" -ne 0 ]] || fail "finding no manifests means the gate measured nothing — not a pass
$OUT"
grep -q "vacuous pass" <<<"$OUT" || fail "expected the vacuous-pass refusal:
$OUT"
echo "    no manifests at all                  exit $RC"

echo "==> check-testdata-sync.sh selftest OK"
