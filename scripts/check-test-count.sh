#!/usr/bin/env bash
#
# #894 — every discovered test must produce a result.
#
# THE PROBLEM THIS EXISTS FOR
#
# `dotnet test` can report fewer results than it discovered. Measured on
# Excise.Core.Tests: 3932 discovered, and roughly half of all runs report 3931.
# Not the same test each time —
#
#     FlattenAcroForm_LongText_ClipsAndWrapsInsideFieldBounds
#     CffParser_Parse_MutatedValidCff_NeverThrows(seed: 11)
#     MetadataFreeParse_SkipsBoundsAnnotations
#     Write_UserPasswordOnly_OwnerEntriesDoNotValidateAgainstTheEmptyPassword
#
# — a different one on each losing run, always exactly one. It is NOT xunit
# parallelism: forcing parallelizeTestCollections=false and maxParallelThreads=1
# changes the wall clock by 4s (115s vs 111s) and still loses one. It is the
# vstest/testhost result channel.
#
# WHY IT MATTERS MORE THAN A NORMAL TEST BUG
#
# A vanished test is invisible in exactly the way a passing test is: the summary
# says all green. Worse, it defeats mutation testing — you cannot make a case
# fail by reverting the fix it covers if the case never reports. "Disable the
# fix and confirm red" would confirm nothing while appearing to confirm
# everything, which is this repo's whole method for proving an assertion
# discriminates.
#
# There is a second, DIFFERENT failure with the same signature: a test that is
# discovered and never executes at all, deterministically, on every run and
# under every filter form. That one is a real coverage hole. This script has to
# tell them apart, which is why it does not simply compare counts.
#
# WHAT IT DOES
#
#   1. Enumerate discovered tests   (`--list-tests`, discovery only, seconds)
#   2. Enumerate reported results   (trx)
#   3. For anything discovered but not reported, RE-RUN IT BY NAME:
#        - produces a result now  -> transient reporting loss. Reported, not fatal.
#        - still produces nothing -> a genuine hole. FATAL.
#        - produces a FAILING result -> FATAL, and the summary hid a red test.
#
# A plain equality check was written first and thrown away: it fails ~50% of
# runs here, and a gate that flakes is a gate people stop reading — the exact
# dynamic that let six un-allow-listed skips redden test-linux for 8+ runs
# (#854), and that #855 was closed over.
#
# Usage:
#   scripts/check-test-count.sh <csproj> [--trx PATH] [--max-recheck N]
#
#   --trx PATH       reuse an existing trx instead of running the suite again.
#                    Use this from CI so the gate costs one discovery pass, not
#                    a second full run.
#
#                    ⚠️ THE TRX MUST COME FROM AN UNFILTERED RUN. Discovery
#                    enumerates every test in the assembly, so a trx produced
#                    with `--filter` is missing everything the filter excluded
#                    and this gate would report thousands of "holes". That rules
#                    out CI's `Run Excise.Rendering.Tests (deterministic only)`
#                    step, which filters out Corpus/Differential/Benchmark/Visual
#                    by design — point the gate at unfiltered suites
#                    (Core/Cli/Avalonia) or give it no --trx and let it run one.
#   --max-recheck N  cap the re-runs (default 10). A run that loses dozens is a
#                    different problem and should not be papered over one test
#                    at a time.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

CSPROJ="${1:-}"
if [[ -z "$CSPROJ" || ! -f "$CSPROJ" ]]; then
    echo "usage: scripts/check-test-count.sh <csproj> [--trx PATH] [--max-recheck N]" >&2
    exit 2
fi
shift

TRX=""
MAX_RECHECK=10
while [[ $# -gt 0 ]]; do
    case "$1" in
        --trx) TRX="${2:-}"; shift 2 ;;
        --max-recheck) MAX_RECHECK="${2:-10}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
PROJ_NAME="$(basename "$CSPROJ" .csproj)"

assert_fresh() {
    "$ROOT/scripts/assert-fresh.sh" --configuration Debug "$CSPROJ" || exit $?
}

echo "==> enumerating discovered tests in $PROJ_NAME"
assert_fresh
dotnet test "$CSPROJ" -c Debug --no-build --list-tests 2>/dev/null \
    | sed -n 's/^    \(.*\)$/\1/p' \
    | sed 's/[[:space:]]*$//' \
    | grep -v '^$' \
    | LC_ALL=C sort -u > "$TMP/discovered.txt"

DISCOVERED=$(wc -l < "$TMP/discovered.txt" | tr -d ' ')
if [[ "$DISCOVERED" -eq 0 ]]; then
    # Discovering zero tests is a FAILURE, not a pass — the same vacuous-green
    # trap scripts/lib-runner.sh documents for `--filter` matching nothing.
    echo "FAIL: discovered 0 tests in $PROJ_NAME. Discovery is broken, or the"
    echo "      assembly was not built. This is not a clean run."
    exit 1
fi
echo "    $DISCOVERED discovered"

if [[ -z "$TRX" ]]; then
    TRX="$TMP/run.trx"
    echo "==> running $PROJ_NAME"
assert_fresh
    dotnet test "$CSPROJ" -c Debug --no-build --logger "trx;LogFileName=$TRX" >/dev/null 2>&1
fi

if [[ ! -f "$TRX" ]]; then
    echo "FAIL: no trx at $TRX — cannot tell which tests reported."
    exit 1
fi

python3 - "$TRX" > "$TMP/executed.txt" <<'PY'
import sys, xml.etree.ElementTree as ET
N = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
seen = set()
for r in ET.parse(sys.argv[1]).iter(N + 'UnitTestResult'):
    n = r.get('testName')
    if n:
        seen.add(n)
for n in sorted(seen):
    print(n)
PY

EXECUTED=$(wc -l < "$TMP/executed.txt" | tr -d ' ')
echo "    $EXECUTED reported"

LC_ALL=C comm -23 "$TMP/discovered.txt" "$TMP/executed.txt" > "$TMP/missing.txt"
MISSING=$(wc -l < "$TMP/missing.txt" | tr -d ' ')

if [[ "$MISSING" -eq 0 ]]; then
    echo "==> test count OK ($DISCOVERED discovered, all reported)"
    exit 0
fi

if [[ "$MISSING" -gt "$MAX_RECHECK" ]]; then
    echo
    echo "FAIL: $MISSING discovered tests produced no result (cap is $MAX_RECHECK)."
    echo "      Losing this many is not the known one-per-run reporting race;"
    echo "      something structural is wrong. Not re-checking them one by one."
    sed 's/^/        - /' "$TMP/missing.txt"
    exit 1
fi

echo
echo "==> $MISSING discovered test(s) produced no result. Re-running each to"
echo "    tell a transient reporting loss from a genuine coverage hole."

TRANSIENT=0
FATAL=0
while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    # Match on the method-name portion: a [Theory] case's display name carries
    # `(param: "value")`, which --filter cannot match literally.
    bare="${name%%(*}"
    assert_fresh
    out="$(dotnet test "$CSPROJ" -c Debug --no-build \
             --filter "FullyQualifiedName~$bare" 2>&1)"

    if grep -q "No test matches the given testcase filter" <<<"$out"; then
        echo "    FATAL  $name"
        echo "           discovered, but unreachable by filter — never executes."
        FATAL=$((FATAL + 1))
    elif grep -qE "^Failed!" <<<"$out"; then
        echo "    FATAL  $name"
        echo "           re-ran and FAILED — the summary hid a red test."
        FATAL=$((FATAL + 1))
    elif grep -qE "^(Passed!|Skipped!)" <<<"$out"; then
        echo "    ok     $name (transient reporting loss)"
        TRANSIENT=$((TRANSIENT + 1))
    else
        echo "    FATAL  $name"
        echo "           re-run produced no recognisable result."
        FATAL=$((FATAL + 1))
    fi
done < "$TMP/missing.txt"

echo
if [[ "$FATAL" -gt 0 ]]; then
    echo "FAIL: $FATAL discovered test(s) do not run. That is coverage loss you"
    echo "      cannot see, and it silently defeats mutation testing — reverting"
    echo "      a fix cannot redden a case that never reports."
    exit 1
fi

echo "==> test count OK after re-check ($TRANSIENT transient reporting loss(es), #894)."
echo "    Every discovered test produced a result on re-run. The loss is in the"
echo "    vstest result channel, not in coverage."
exit 0
