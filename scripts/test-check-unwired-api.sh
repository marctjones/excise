#!/usr/bin/env bash
#
# Selftest for scripts/check-unwired-api.sh (#908/#931, selftested per #1012).
#
# The gate is a RATCHET over tests/unwired-api-baseline.tsv: public API that
# nothing calls, or that only tests call, must be either wired up or accepted
# with a triage note. "tests-only" is the shape that shipped #896 (a redaction
# leak, because the safe API existed and no production code called it) and #908.
#
# Driven inside a hermetic temp root with a three-file synthetic assembly, so
# every verdict is decided by known input rather than by the real 100+ entry
# baseline — against which a broken checker would still print a plausible
# "no NEW unwired API".
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

R="$TMP/root"
mkdir -p "$R/scripts" "$R/Fake.Core/PublicApi" "$R/Fake.App" "$R/Fake.Core.Tests" "$R/tests"
cp "$ROOT/scripts/check-unwired-api.sh" "$R/scripts/"
cp "$ROOT/scripts/check_unwired_api.py" "$R/scripts/"

# The approved public surface: three members, one of each fate.
cat > "$R/Fake.Core/PublicApi/Fake.Core.approved.txt" <<'API'
namespace Fake.Core
{
    public class WidgetFactory
    {
        public void CreateWidgetSafely();
        public void DanglingHelper();
        public void TestedOnlyHelper();
    }
}
API
# Production: declares all three, and calls only CreateWidgetSafely (a second
# production file, which is what "wired" means here).
cat > "$R/Fake.Core/WidgetFactory.cs" <<'CS'
public class WidgetFactory
{
    public void CreateWidgetSafely() { }
    public void DanglingHelper() { }
    public void TestedOnlyHelper() { }
}
CS
cat > "$R/Fake.App/Caller.cs" <<'CS'
public class Caller
{
    void Go() { new WidgetFactory().CreateWidgetSafely(); }
}
CS
# Tests reference TestedOnlyHelper and nothing else — implemented, tested,
# never called in production.
cat > "$R/Fake.Core.Tests/WidgetFactoryTests.cs" <<'CS'
public class WidgetFactoryTests
{
    void T() { new WidgetFactory().TestedOnlyHelper(); }
}
CS

BASELINE="tests/unwired-api-baseline.tsv"
write_baseline() {   # write_baseline <note-for-DanglingHelper> [omit-tests-only]
    {
        echo "# selftest baseline"
        printf 'Fake.Core\tnowhere\tDanglingHelper\t%s\n' "$1"
        [[ "${2:-}" == "omit" ]] || printf 'Fake.Core\ttests-only\tTestedOnlyHelper\taccepted: selftest fixture\n'
    } > "$R/$BASELINE"
}

run() {
    set +e
    OUT="$("$R/scripts/check-unwired-api.sh" --quiet --baseline "$BASELINE" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-unwired-api.sh"

# ── 1. No baseline at all -> FAIL (not an empty, permissive ratchet) ─────────
rm -f "$R/$BASELINE"
run
[[ "$RC" -ne 0 ]] || fail "a missing baseline must fail, not be treated as 'nothing accepted, nothing new'
$OUT"
echo "    no baseline                          exit $RC"

# ── 2. Everything accepted and triaged -> pass ──────────────────────────────
write_baseline "accepted: selftest fixture"
run
[[ "$RC" -eq 0 ]] || fail "a fully triaged baseline must pass (exit $RC)
$OUT"
grep -q "no NEW unwired API" <<<"$OUT" || fail "unexpected verdict:
$OUT"
echo "    all entries baselined + triaged      exit 0"

# ── 3. THE GUARDED PROPERTY: a tests-only member not in the baseline -> FAIL ─
# Implemented, tested, called by no production code, and never accepted. This is
# the #896/#908 shape.
write_baseline "accepted: selftest fixture" omit
run
[[ "$RC" -ne 0 ]] || fail "a NEW tests-only public member must fail the ratchet
$OUT"
grep -q "TestedOnlyHelper" <<<"$OUT" || fail "the failure must name the unwired member:
$OUT"
echo "    new tests-only member                exit $RC"

# ── 4. An accepted row with no triage note -> FAIL ──────────────────────────
write_baseline "UNTRIAGED"
run
[[ "$RC" -ne 0 ]] || fail "an UNTRIAGED accepted row must fail (#931)
$OUT"
grep -q "lack triage notes" <<<"$OUT" || fail "expected the untriaged verdict:
$OUT"
echo "    baselined but untriaged              exit $RC"

# ── 5. No approved API inventory at all -> FAIL, not a vacuous pass ─────────
write_baseline "accepted: selftest fixture"
rm -rf "$R/Fake.Core/PublicApi"
run
[[ "$RC" -ne 0 ]] || fail "with no PublicApi/*.approved.txt the gate measured nothing — not a pass
$OUT"
echo "    no approved-API inventory            exit $RC"

echo "==> check-unwired-api.sh selftest OK"
