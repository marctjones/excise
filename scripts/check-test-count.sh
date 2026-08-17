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
#   3. For anything discovered but not reported, RE-RUN ITS CLASS:
#        - produces a result now  -> transient reporting loss. Reported, not fatal.
#        - still produces nothing -> a genuine hole. FATAL.
#        - produces a FAILING result -> FATAL, and the summary hid a red test.
#        - the FILTER matched nothing -> no verdict at all. `~` is not a substring
#          match (see #1008 and the note at the re-run), so a non-match is a fact
#          about the filter. Escalate to an unfiltered run and decide from that.
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
import sys, xml.etree.ElementTree as ET
N = '{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}'
seen = set()
for path in sys.argv[1:]:
    for r in ET.parse(path).iter(N + 'UnitTestResult'):
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
# At most ONE unfiltered escalation run per invocation, shared by every test that
# needs it (MAX_RECHECK caps the list at 10, so this is bounded by one full run).
UNFILTERED_OUT_FILE=""
while IFS= read -r name; do
    [[ -z "$name" ]] && continue
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
    # row that went missing is still missing. Measured on
    # Cff2RefusalTests.CorpusFilesCarryingCff2_RenderAtParityWithMutool, where a
    # narrow run reports 1 of 2 rows every time — the old check called that a
    # transient loss and moved on, which is this gate's own failure mode one
    # level down (#894).
    out="$(dotnet test "$CSPROJ" -c Debug --no-build \
             --filter "FullyQualifiedName~$cls" \
             --logger "console;verbosity=detailed" 2>&1)"

    if grep -q "No test matches the given testcase filter" <<<"$out"; then
        # A NON-MATCH IS NOT A VERDICT (#1008). It says the filter could not
        # select the test, which — see the token-boundary note above — can happen
        # for reasons that have nothing to do with whether the test executes.
        # This branch used to print "discovered, but unreachable by filter —
        # never executes" and exit FATAL, so a transient reporting loss was
        # reported as a genuine coverage hole and the message sent the next
        # person hunting a crash that did not exist. Escalate instead: run the
        # assembly UNFILTERED and decide from the per-case output, which is the
        # only evidence that can distinguish the two.
        echo "    note   $name"
        echo "           filter ~$cls selected nothing — that is a statement about the"
        echo "           filter, not about the test. Escalating to an unfiltered run."
        if [[ -z "$UNFILTERED_OUT_FILE" ]]; then
            UNFILTERED_OUT_FILE="$TMP/unfiltered.txt"
            assert_fresh
            dotnet test "$CSPROJ" -c Debug --no-build \
                --logger "console;verbosity=detailed" > "$UNFILTERED_OUT_FILE" 2>&1
        fi
        out="$(cat "$UNFILTERED_OUT_FILE")"
        if grep -q "No test matches the given testcase filter" <<<"$out"; then
            echo "    FATAL  $name"
            echo "           an UNFILTERED run of the assembly also selected nothing."
            echo "           The assembly itself is unrunnable — this is not a filter"
            echo "           problem and not a per-test one."
            FATAL=$((FATAL + 1))
            continue
        fi
    fi

    if grep -qE "^Failed!" <<<"$out"; then
        echo "    FATAL  $name"
        echo "           re-ran and FAILED — the summary hid a red test."
        FATAL=$((FATAL + 1))
    elif grep -qF "$name" <<<"$out"; then
        echo "    ok     $name (transient reporting loss)"
        TRANSIENT=$((TRANSIENT + 1))
    elif grep -qE "^ +(Passed|Failed|Skipped) " <<<"$out"; then
        # The method re-ran and OTHER cases reported, but this one did not.
        # Detected from the per-case lines, not a summary line: under
        # `verbosity=detailed` dotnet test prints per-case results and no
        # `Passed!` summary at all, so keying on the summary made this branch
        # unreachable and every row-level loss fell to the vaguer message below.
        #
        # #1035: RE-CHECK ONCE before convicting. The #894 loss is per-run and
        # independent — a different test each time — so it can strike the same
        # test in the run AND in the re-check, and then this branch blocked a
        # push over a test that runs perfectly well. That happened twice on
        # 2026-08-17, both cleared by typing the same command again. A gate
        # cleared by retrying teaches people to retry instead of read, which is
        # how a gate stops being read at all (#854).
        #
        # Two consecutive losses of the SAME test are far rarer than one, so a
        # second look costs one class-filtered run and removes almost all the
        # false blocks. A test that is genuinely never executed fails all three
        # looks, so the capability that found #985 and the Altona hole is intact.
        echo "    ...    $name absent on re-run; looking once more (#1035)"
        assert_fresh
        out2="$(dotnet test "$CSPROJ" -c Debug --no-build \
                  --filter "FullyQualifiedName~$cls" \
                  --logger "console;verbosity=detailed" 2>&1)"
        if grep -qF "$name" <<<"$out2"; then
            echo "    ok     $name (transient reporting loss, seen twice then reported)"
            TRANSIENT=$((TRANSIENT + 1))
        else
            echo "    FATAL  $name"
            echo "           its class re-ran TWICE and other cases reported both"
            echo "           times, but THIS case produced no result either time —"
            echo "           a row-level loss no summary can see."
            FATAL=$((FATAL + 1))
        fi
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
