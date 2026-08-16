#!/usr/bin/env bash
#
# Selftest for scripts/check-test-count.sh (#894, classification fixed in #1008).
#
# The gate's JOB is to tell a transient reporting loss from a genuine coverage
# hole. Until #1008 it decided that from whether a `--filter` matched — and
# `FullyQualifiedName~X` is not a substring match under xunit v3 on
# Microsoft.Testing.Platform, so a non-match happens for reasons unrelated to
# whether the test executes. A transient loss was reported as FATAL "never
# executes", which sent the next person hunting a crash that did not exist.
#
# This drives the REAL script inside a hermetic temp root with a fake `dotnet`
# on PATH, so every branch is exercised against known input in milliseconds and
# no test project is built or run.
#
# Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-test-count.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

ALPHA="Ns.FakeTests.AlphaTest"
BETA="Ns.FakeTests.BetaTest"     # discovered, absent from the trx — the subject

# ── hermetic root ────────────────────────────────────────────────────────────
R="$TMP/root"
mkdir -p "$R/scripts" "$R/Fake.Tests" "$R/bin"
cp "$SCRIPT" "$R/scripts/check-test-count.sh"
: > "$R/Fake.Tests/Fake.Tests.csproj"
# assert-fresh compares source timestamps against a built DLL; there is no build
# here, so stub it. It has its own selftest (scripts/test-assert-fresh.sh).
printf '#!/usr/bin/env bash\nexit 0\n' > "$R/scripts/assert-fresh.sh"
chmod +x "$R/scripts/assert-fresh.sh"

# A trx that reports ALPHA and not BETA: exactly the "discovered but not
# reported" input this gate exists to classify.
cat > "$TMP/run.trx" <<TRX
<?xml version="1.0" encoding="UTF-8"?>
<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Results>
    <UnitTestResult testName="$ALPHA" outcome="Passed" />
  </Results>
</TestRun>
TRX

# ── fake dotnet ──────────────────────────────────────────────────────────────
# --list-tests always discovers BOTH tests (4-space indent, as `dotnet test`
# prints them). Everything else is driven by FAKE_MODE, which is what makes each
# case below a KNOWN input rather than a measurement of the real toolchain.
cat > "$R/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
args="$*"
if [[ "$args" == *"--list-tests"* ]]; then
    echo "The following Tests are available:"
    echo "    Ns.FakeTests.AlphaTest"
    echo "    Ns.FakeTests.BetaTest"
    exit 0
fi
filtered=0
[[ "$args" == *"--filter"* ]] && filtered=1
case "${FAKE_MODE:?FAKE_MODE not set}" in
  transient)      # the class filter selects it and it reports this time
      echo "  Passed Ns.FakeTests.AlphaTest [1 ms]"
      echo "  Passed Ns.FakeTests.BetaTest [1 ms]" ;;
  filter_blind)   # the FILTER cannot select it; an unfiltered run can
      if [[ $filtered -eq 1 ]]; then
          echo "No test matches the given testcase filter \`FullyQualifiedName~FakeTests\` in suite"
      else
          echo "  Passed Ns.FakeTests.AlphaTest [1 ms]"
          echo "  Passed Ns.FakeTests.BetaTest [1 ms]"
      fi ;;
  genuine_hole)   # filter blind AND the unfiltered run still never reports it
      if [[ $filtered -eq 1 ]]; then
          echo "No test matches the given testcase filter \`FullyQualifiedName~FakeTests\` in suite"
      else
          echo "  Passed Ns.FakeTests.AlphaTest [1 ms]"
      fi ;;
  red)            # it reports, and it is RED — the summary hid a failing test
      echo "  Passed Ns.FakeTests.AlphaTest [1 ms]"
      echo "  Failed Ns.FakeTests.BetaTest [1 ms]"
      echo "Failed!  - Failed:     1, Passed:     1" ;;
  no_discovery)   # handled above for --list-tests; unreachable here
      : ;;
esac
exit 0
FAKE
chmod +x "$R/bin/dotnet"

run_gate() {   # run_gate <FAKE_MODE> -> prints output, sets RC
    set +e
    OUT="$(FAKE_MODE="$1" PATH="$R/bin:$PATH" \
             "$R/scripts/check-test-count.sh" Fake.Tests/Fake.Tests.csproj \
             --trx "$TMP/run.trx" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-test-count.sh classification"

# ── 1. Transient reporting loss -> reported, NOT fatal ───────────────────────
run_gate transient
[[ "$RC" -eq 0 ]] || fail "a test that reports on re-run is a transient loss, not a failure (exit $RC)
$OUT"
grep -q "transient reporting loss" <<<"$OUT" || fail "expected the transient verdict:
$OUT"
echo "    transient loss                       exit 0"

# ── 2. #1008: a FILTER NON-MATCH IS NOT A VERDICT ────────────────────────────
# The whole point. Old behaviour on this input: FATAL, "discovered, but
# unreachable by filter — never executes." New behaviour: escalate to an
# unfiltered run, find it, call it what it is.
run_gate filter_blind
[[ "$RC" -eq 0 ]] || fail "a filter non-match must escalate, not be reported as a coverage hole (exit $RC)
$OUT"
grep -q "Escalating to an unfiltered run" <<<"$OUT" \
    || fail "expected an escalation, not a verdict from the filter:
$OUT"
grep -q "never executes" <<<"$OUT" \
    && fail "the gate still infers 'never executes' from a filter non-match (#1008)"
grep -q "transient reporting loss" <<<"$OUT" \
    || fail "after escalating and finding it, the verdict must be the transient one:
$OUT"
echo "    filter non-match -> escalates        exit 0"

# ── 3. A GENUINE hole is still fatal ─────────────────────────────────────────
# Same filter non-match as case 2, but the unfiltered run never reports it
# either. The fix must not have turned the gate into a rubber stamp.
run_gate genuine_hole
[[ "$RC" -ne 0 ]] || fail "a test absent from an UNFILTERED run is a real coverage hole and must be fatal
$OUT"
grep -q "FATAL  $BETA" <<<"$OUT" || fail "expected a FATAL verdict for $BETA:
$OUT"
echo "    genuine hole                         exit $RC"

# ── 4. A test that re-runs RED is fatal ──────────────────────────────────────
run_gate red
[[ "$RC" -ne 0 ]] || fail "a re-run that FAILS must be fatal — the summary hid a red test
$OUT"
grep -q "hid a red test" <<<"$OUT" || fail "expected the red-test verdict:
$OUT"
echo "    re-ran and failed                    exit $RC"

# ── 5. Discovering nothing is a FAILURE, not a vacuous pass ──────────────────
cat > "$R/bin/dotnet" <<'NONE'
#!/usr/bin/env bash
[[ "$*" == *"--list-tests"* ]] && { echo "The following Tests are available:"; exit 0; }
exit 0
NONE
chmod +x "$R/bin/dotnet"
set +e
OUT="$(FAKE_MODE=no_discovery PATH="$R/bin:$PATH" \
         "$R/scripts/check-test-count.sh" Fake.Tests/Fake.Tests.csproj \
         --trx "$TMP/run.trx" 2>&1)"
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "discovering 0 tests must fail — it is a gate that measured nothing
$OUT"
echo "    zero discovered                      exit $RC"

echo "==> check-test-count.sh selftest OK"
