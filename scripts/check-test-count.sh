#!/usr/bin/env bash
#
# #894 — every discovered test must produce a result.
#
# THE PROBLEM THIS EXISTS FOR
#
# `dotnet test` can report fewer results than it discovered. For a year this
# repo saw Excise.Core.Tests lose exactly one result on roughly half of all
# runs, a different test each time —
#
#     FlattenAcroForm_LongText_ClipsAndWrapsInsideFieldBounds
#     CffParser_Parse_MutatedValidCff_NeverThrows(seed: 11)
#     MetadataFreeParse_SkipsBoundsAnnotations
#     Write_UserPasswordOnly_OwnerEntriesDoNotValidateAgainstTheEmptyPassword
#
# — and called it a vstest channel race (#894), which it was not.
#
# THE CAUSE IS KNOWN (#1701). The xunit v3 VSTest adapter reads one JSON
# message per line of the test process's stdout. CSJ2K's FacilityManager static
# constructor writes a UTF-8 BOM to raw descriptor 1, once per process, on the
# first JPX touch. The BOM sticks to whatever message comes next. When that is a
# test-passed/failed/skipped, the adapter logs "Received unparseable output from
# test process" (visible only with `-- xUnit.InternalDiagnosticMessages=true`)
# and that one result is gone. When it is a lifecycle message
# (test-class-dispose-starting and the like), nothing is lost. Hence "exactly
# one, a different test, about half the runs". Measured 2026-10-20 on whole
# Excise.Core.Tests runs (7742 tests) with the guard switched off: the BOM line
# appeared on both unguarded runs, once on a lifecycle message (7742 reported) and once
# on RotatedPageRedactionTests...(rotation: 90)'s test-passed (7741 reported).
# With Excise.Core.Tests/TestSupport/Csj2kStdoutBomGuard.cs in place: 7742 of
# 7742 on every run, and Avalonia, Cli, Ocr and Rendering lost nothing either.
#
# SO ANY LOSS ON A WHOLE-ASSEMBLY RUN IS NOW FATAL, immediately. The #1051
# ratchet that warned until a row was lost on 3 consecutive runs existed only
# to tolerate the chance loss. That loss had a cause and the cause is fixed. A
# loss now means something else writes raw bytes to stdout, or a test is
# discovered and never executes, and both are coverage you cannot see. The
# re-run below no longer decides pass or fail. It diagnoses which of those it
# is.
#
# ⚠️ Do not reintroduce a tolerance because one run lost one test. Find the
# writer: re-run with the diagnostics flag and grep for the line above.
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
#   3. Anything discovered but not reported is FATAL (#1701). Its CLASS is re-run
#      once, with the adapter diagnostics on, to say WHY:
#        - its own result line arrived corrupted -> something writes raw stdout.
#        - it reports on re-run -> lost only in the whole run; another test in
#          the assembly corrupts the channel. The message says how to find it.
#        - still produces nothing, siblings report -> it never executes.
#        - produces a FAILING result -> and the summary hid a red test.
#        - the FILTER matched nothing -> `~` is not a substring match (see #1008
#          and the note at the re-run), so escalate to an unfiltered run.
#
# A plain equality check was written first and thrown away when it failed ~50%
# of runs; that was the CSJ2K BOM above, and with it fixed an equality verdict
# is what this gate now gives. The re-run is for the diagnosis.
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
#   --max-recheck N  cap the diagnostic re-runs (default 10). Past the cap the
#                    gate fails without diagnosing each test one by one.
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
TRX_FILES=()
MAX_RECHECK=10
while [[ $# -gt 0 ]]; do
    case "$1" in
        --trx)
            # Repeatable (#894). Chunked projects (Core/Rendering/App in
            # run-full-suite.sh) never produce a single unfiltered trx, but
            # their chunks PARTITION the project by test class, so the union of
            # the chunk trx files is exactly the unfiltered set — and the
            # runner already verifies that partition covers every class
            # ("chunk coverage verified: … 0 uncovered"). Passing them all is
            # therefore sound where passing any ONE would report every other
            # chunk's tests as holes.
            TRX_FILES+=("${2:-}"); shift 2 ;;
        --max-recheck) MAX_RECHECK="${2:-10}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

# The #1051 loss-streak ledger is gone with the ratchet (#1701). An old
# checkout may still hold one; it means nothing now.
rm -f "$ROOT/tests/test-count-loss-ledger.tsv"

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

if [[ "${#TRX_FILES[@]}" -eq 0 ]]; then
    TRX="$TMP/run.trx"
    echo "==> running $PROJ_NAME"
assert_fresh
    dotnet test "$CSPROJ" -c Debug --no-build --logger "trx;LogFileName=$TRX" >/dev/null 2>&1
    TRX_FILES=("$TRX")
fi

for _trx in "${TRX_FILES[@]}"; do
    if [[ ! -f "$_trx" ]]; then
        echo "FAIL: no trx at $_trx — cannot tell which tests reported."
        exit 1
    fi
done
echo "    reading $(printf '%s' "${#TRX_FILES[@]}") trx file(s)"

python3 - "${TRX_FILES[@]}" > "$TMP/executed.txt" <<'PY'
import re, sys, xml.etree.ElementTree as ET
N = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
# The trx spells a character outside the BMP in a theory argument as the
# literal text `𐐀`, while --list-tests prints the character itself.
# Without this, MatchingKeyInvariantTests' "le 𐐀x noir" row reads as missing on
# every run (7742 discovered, 7742 reported, one name unmatched; 2026-10-20).
# Only a well-formed escaped surrogate PAIR is decoded, so no other backslash
# in a name is touched.
PAIR = re.compile(r'\\u(d[89ab][0-9a-f]{2})\\u(d[c-f][0-9a-f]{2})', re.I)
def unescape_pairs(s):
    return PAIR.sub(lambda m: (chr(int(m.group(1), 16)) + chr(int(m.group(2), 16)))
                    .encode('utf-16', 'surrogatepass').decode('utf-16'), s)
seen = set()
for path in sys.argv[1:]:
    for r in ET.parse(path).iter(N + 'UnitTestResult'):
        n = r.get('testName')
        if n:
            seen.add(unescape_pairs(n))
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
    echo "      Something structural is wrong. Not diagnosing them one by one."
    sed 's/^/        - /' "$TMP/missing.txt"
    exit 1
fi

echo
echo "==> $MISSING discovered test(s) produced no result. Every one is FATAL (#1701);"
echo "    re-running each class once, with adapter diagnostics, to say why."

FATAL=0
# At most ONE unfiltered escalation run per invocation, shared by every test that
# needs it (MAX_RECHECK caps the list at 10, so this is bounded by one full run).
UNFILTERED_OUT_FILE=""
while IFS= read -r name; do
    [[ -z "$name" ]] && continue
    FATAL=$((FATAL + 1))
    # Match on the method-name portion: a [Theory] case's display name carries
    # `(param: "value")`, which --filter cannot match literally.
    bare="${name%%(*}"
    # Re-run on the CLASS simple name, not the method FQN (#1008).
    #
    # `FullyQualifiedName~X` is NOT a substring match under xunit v3 on
    # Microsoft.Testing.Platform — it matches only where X aligns to a token
    # boundary. Measured on
    # Excise.Core.Tests.Filters.Jpx.CodestreamParserTests.TryDecodeManaged_AltonaIndexedJpxDecodesSingleIndexPlane:
    # `~Altona` matched 3 tests and `~Indexed` 10, while `~AltonaIndexed`,
    # `~IndexedJpx` and even `~<the full FQN>` matched NOTHING, deterministically,
    # on plain-ASCII names and after a --no-incremental rebuild. A dotted segment
    # is always a whole token, so the class name is a filter whose alignment is
    # guaranteed where the method FQN's is not.
    cls="${bare%.*}"; cls="${cls##*.}"
    [[ -z "$cls" ]] && cls="$bare"
    assert_fresh
    # verbosity=detailed so the re-run lists PER-CASE results. The summary line
    # alone is not enough for a [Theory]: re-running the class runs EVERY row of
    # every method in it, so "Passed!" can mean "some other row ran" while the
    # row that went missing is still missing (Cff2RefusalTests, #894).
    #
    # #1701: the xunit adapter's internal diagnostics are the only place a
    # CORRUPTED result line is visible. The xunit v3 VSTest adapter reads one
    # JSON message per line of the test process's stdout; anything that writes
    # raw bytes to descriptor 1 (CSJ2K wrote a UTF-8 BOM there) glues itself to
    # the next message, the adapter logs "Received unparseable output from test
    # process" and that result is gone.
    out="$(dotnet test "$CSPROJ" -c Debug --no-build \
             --filter "FullyQualifiedName~$cls" \
             --logger "console;verbosity=detailed" \
             -- xUnit.InternalDiagnosticMessages=true 2>&1)"
    # Kept for the corrupted-line lookup below. A test that drops its OWN
    # result leaves its class run with nothing parseable, so vstest says "No
    # test matches", and the unfiltered escalation may put the stray bytes on a
    # different message. The evidence for this test is in THIS run.
    filtered_out="$out"

    if grep -q "No test matches the given testcase filter" <<<"$out"; then
        # A NON-MATCH IS NOT A DIAGNOSIS (#1008). It says the filter could not
        # select the test, which — see the token-boundary note above — can happen
        # for reasons that have nothing to do with whether the test executes.
        # Run the assembly UNFILTERED and read the per-case output instead.
        echo "    note   $name"
        echo "           filter ~$cls selected nothing — that is a statement about the"
        echo "           filter, not about the test. Escalating to an unfiltered run."
        if [[ -z "$UNFILTERED_OUT_FILE" ]]; then
            UNFILTERED_OUT_FILE="$TMP/unfiltered.txt"
            assert_fresh
            dotnet test "$CSPROJ" -c Debug --no-build \
                --logger "console;verbosity=detailed" \
                -- xUnit.InternalDiagnosticMessages=true > "$UNFILTERED_OUT_FILE" 2>&1
        fi
        out="$(cat "$UNFILTERED_OUT_FILE")"
        if grep -q "No test matches the given testcase filter" <<<"$out"; then
            echo "    FATAL  $name"
            echo "           an UNFILTERED run of the assembly also selected nothing."
            echo "           The assembly itself is unrunnable — this is not a filter"
            echo "           problem and not a per-test one."
            continue
        fi
    fi

    diag_out="$filtered_out"$'\n'"$out"
    corrupted_ids="$(grep -F 'Received unparseable output from test process' <<<"$diag_out" \
        | grep -E '"\$type":"test-(passed|failed|skipped|not-run|finished)"' \
        | sed -n 's/.*"TestCaseUniqueID":"\([0-9a-f]*\)".*/\1/p' | sort -u)"
    corrupted_name=""
    others=""
    for id in $corrupted_ids; do
        n="$(sed -n "s/.*Discovered \[execution\] test case '\(.*\)' (ID = '$id').*/\1/p" <<<"$diag_out" | head -1)"
        if [[ "$n" == "$name" || "$n" == "$bare" ]]; then corrupted_name="$n"; else others+="${n:-$id}"$'\n'; fi
    done
    # Diagnostics print every discovered name, so match result lines only.
    results="$(grep -v '^\[xUnit.net' <<<"$out")"

    if [[ -n "$corrupted_name" ]]; then
        echo "    FATAL  $name"
        echo "           its result line reached the adapter corrupted and was dropped"
        echo "           (\"Received unparseable output from test process\", #1701)."
        echo "           Something this test runs writes raw bytes to stdout. That"
        echo "           drop is deterministic: the test never reports, on any run."
    elif grep -qE "^Failed!" <<<"$results"; then
        echo "    FATAL  $name"
        echo "           re-ran and FAILED — the summary hid a red test."
    elif grep -qF "$name" <<<"$results"; then
        echo "    FATAL  $name"
        echo "           reported on the diagnostic re-run, so the gated run lost it on"
        echo "           the way. Until #1701 that was CSJ2K's stdout BOM; with the guard"
        echo "           in place, something in this assembly writes raw bytes to stdout"
        echo "           and corrupts whichever result line comes next. Find it: re-run"
        echo "           with  -- xUnit.InternalDiagnosticMessages=true  and grep for"
        echo "           'Received unparseable output from test process'."
        if [[ -n "$others" ]]; then
            echo "           The re-run itself lost these results the same way:"
            printf '%s' "$others" | sed 's/^/             - /'
        fi
    elif grep -qE "^ +(Passed|Failed|Skipped) " <<<"$results"; then
        echo "    FATAL  $name"
        echo "           its class re-ran and siblings reported, this one did not."
        echo "           Discovered and never executed: a coverage hole."
    else
        echo "    FATAL  $name"
        echo "           re-run produced no recognisable result."
    fi
done < "$TMP/missing.txt"

echo
echo "FAIL: $FATAL discovered test(s) produced no result. That is coverage loss you"
echo "      cannot see, and it silently defeats mutation testing — reverting"
echo "      a fix cannot redden a case that never reports."
exit 1
