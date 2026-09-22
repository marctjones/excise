#!/usr/bin/env bash
#
# test-check-redaction-suite-floor.sh — falsifiability for the redaction floor
# and the trx-union proof (#1767, #1012).
#
# A floor that has never been seen to trip is not a floor, and a union check
# that has never refused stale evidence is a green nobody earned. Every check
# below PLANTS the defect it names against a synthetic trx and asserts the gate
# goes red, then restores it and asserts green. Hermetic: a temp dir, no
# dotnet, no corpus, no reference tool (--discover is exercised through a
# pre-supplied discovery list, not by running discovery).
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
GATE="$ROOT/scripts/check-redaction-suite-floor.sh"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

PASS=0
FAIL=0
check() {   # check <description> <expected-rc> <command...>
    local what="$1" want="$2"; shift 2
    local out rc
    out="$("$@" 2>&1)"; rc=$?
    if [ "$rc" = "$want" ]; then
        PASS=$((PASS + 1)); echo "PASS  $what"
    else
        FAIL=$((FAIL + 1)); echo "FAIL  $what (rc=$rc, wanted $want)"
        printf '%s\n' "$out" | sed 's/^/        /'
    fi
}
saw() {     # saw <description> <needle> <command...>
    local what="$1" needle="$2"; shift 2
    local out
    out="$("$@" 2>&1)"
    if printf '%s' "$out" | grep -qF "$needle"; then
        PASS=$((PASS + 1)); echo "PASS  $what"
    else
        FAIL=$((FAIL + 1)); echo "FAIL  $what (output does not mention '$needle')"
        printf '%s\n' "$out" | sed 's/^/        /'
    fi
}

# write_trx <path> <assembly> <count> <outcome> <class> <method-prefix>
write_trx() {
    local path="$1" asm="$2" count="$3" outcome="$4" cls="$5" prefix="$6" i
    {
        echo '<?xml version="1.0" encoding="UTF-8"?>'
        echo '<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
        echo '  <Results>'
        for ((i = 1; i <= count; i++)); do
            printf '    <UnitTestResult testId="%s-%d" testName="%s.%s%d" outcome="%s" />\n' \
                "$asm" "$i" "$cls" "$prefix" "$i" "$outcome"
        done
        echo '  </Results>'
        echo '  <TestDefinitions>'
        for ((i = 1; i <= count; i++)); do
            printf '    <UnitTest id="%s-%d" name="%s%d"><TestMethod codeBase="/b/%s.dll" className="%s" name="%s%d" /></UnitTest>\n' \
                "$asm" "$i" "$prefix" "$i" "$asm" "$cls" "$prefix" "$i"
        done
        echo '  </TestDefinitions>'
        echo '</TestRun>'
    } > "$path"
}

RUN="$TMP/run"; mkdir -p "$RUN"
write_trx "$RUN/core.trx" Excise.Core.Tests 100 Passed Excise.Core.Tests.RedactionFooTests Removes
write_trx "$RUN/cli.trx"  Excise.Cli.Tests   16 Passed Excise.Cli.Tests.RedactCommandRedactionTests Runs
write_trx "$RUN/ava.trx"  Excise.Avalonia.Tests 5 Passed Excise.Avalonia.Tests.ViewerTests Scrolls

echo "== the floor itself"
check "a healthy run passes its floors" 0 \
    "$GATE" --label selftest --trx "$RUN/core.trx" --trx "$RUN/cli.trx" \
    --floor Excise.Core.Tests=90:90 --floor Excise.Cli.Tests=14:14 --total-floor 100:100

# THE PLANT the issue names: the row collected 40 instead of its usual load and
# `Passed! Failed: 0` is byte-identical to a full run.
write_trx "$TMP/truncated.trx" Excise.Core.Tests 40 Passed Excise.Core.Tests.RedactionFooTests Removes
check "a truncated 40-result trx trips the results floor" 1 \
    "$GATE" --label selftest --trx "$TMP/truncated.trx" --floor Excise.Core.Tests=90:90
saw "and it says which floor and by how much" "reported 40 result(s); the floor is 90" \
    "$GATE" --label selftest --trx "$TMP/truncated.trx" --floor Excise.Core.Tests=90:90
check "restored, the same floor passes" 0 \
    "$GATE" --label selftest --trx "$RUN/core.trx" --floor Excise.Core.Tests=90:90

write_trx "$TMP/allskipped.trx" Excise.Core.Tests 100 NotExecuted Excise.Core.Tests.RedactionFooTests Removes
check "a mass SKIP keeps the result count and trips the PASSED floor" 1 \
    "$GATE" --label selftest --trx "$TMP/allskipped.trx" --floor Excise.Core.Tests=90:90
saw "and it says a mass skip verifies nothing" "verifies nothing" \
    "$GATE" --label selftest --trx "$TMP/allskipped.trx" --floor Excise.Core.Tests=90:90

write_trx "$TMP/red.trx" Excise.Core.Tests 100 Failed Excise.Core.Tests.RedactionFooTests Removes
check "a FAILED redaction result fails the gate" 1 \
    "$GATE" --label selftest --select --trx "$TMP/red.trx" --floor Excise.Core.Tests=90:0

echo "== evidence freshness"
check "a trx outside --run-dir is refused" 1 \
    "$GATE" --label selftest --run-dir "$RUN" --trx "$TMP/truncated.trx"
saw "and it says the producer was checkpointed away" "checkpointed away" \
    "$GATE" --label selftest --run-dir "$RUN" --trx "$TMP/truncated.trx"
check "the same content INSIDE --run-dir is accepted" 0 \
    "$GATE" --label selftest --run-dir "$RUN" --trx "$RUN/core.trx"
check "a named trx that does not exist fails" 1 \
    "$GATE" --label selftest --trx "$TMP/nope.trx"
head -c 120 "$RUN/core.trx" > "$TMP/torn.trx"
check "a torn trx fails rather than reading as empty" 1 \
    "$GATE" --label selftest --trx "$TMP/torn.trx"
check "an empty --trx-dir fails (no evidence is not a pass)" 1 \
    "$GATE" --label selftest --trx-dir "$TMP/empty-dir"
mkdir -p "$TMP/dir"; cp "$RUN/core.trx" "$TMP/dir/"
check "--trx-dir reads every trx in the directory" 0 \
    "$GATE" --label selftest --trx-dir "$TMP/dir" --floor Excise.Core.Tests=90:90

echo "== containment: every test project must be represented"
SLN="$TMP/fake.sln"
cat > "$SLN" <<'EOF'
Project("{X}") = "Excise.Core.Tests", "Excise.Core.Tests\Excise.Core.Tests.csproj", "{1}"
Project("{X}") = "Excise.Cli.Tests", "Excise.Cli.Tests\Excise.Cli.Tests.csproj", "{2}"
Project("{X}") = "Excise.Avalonia.Tests", "Excise.Avalonia.Tests\Excise.Avalonia.Tests.csproj", "{3}"
EOF
check "all three projects represented -> pass" 0 \
    "$GATE" --label selftest --select --sln "$SLN" \
    --trx "$RUN/core.trx" --trx "$RUN/cli.trx" --trx "$RUN/ava.trx"
check "drop one producer and containment fails" 1 \
    "$GATE" --label selftest --select --sln "$SLN" --trx "$RUN/core.trx" --trx "$RUN/cli.trx"
saw "and it names the unrepresented project" "Excise.Avalonia.Tests is a test project" \
    "$GATE" --label selftest --select --sln "$SLN" --trx "$RUN/core.trx" --trx "$RUN/cli.trx"
# Excise.Avalonia.Tests holds no redaction test at all; representation is by ANY
# result, never by a selected one, or a project with none would be unprovable.
saw "representation is by any result, not a selected one" "all represented: yes" \
    "$GATE" --label selftest --select --sln "$SLN" \
    --trx "$RUN/core.trx" --trx "$RUN/cli.trx" --trx "$RUN/ava.trx"

echo "== the selection rule, at both edges"
write_trx "$TMP/unredaction.trx" Excise.Rendering.Tests 8 Passed \
    Excise.Rendering.Tests.Differential.UnredactionBenchManifestTests Row
saw "Unredaction* IS selected (the filter's ~ is case-insensitive)" "results=8" \
    "$GATE" --label selftest --select --trx "$TMP/unredaction.trx"
# The real shape is a display name `...(mode: Redaction)` whose FQN has no
# 'redaction' in it; the filter does not collect it and neither may we.
write_trx "$TMP/displayonly.trx" Excise.App.Tests 8 Passed \
    Excise.App.Tests.Unit.MainWindowViewModelTests ExitingEditingMode
saw "a display-name-only match is NOT selected" "results=0" \
    "$GATE" --label selftest --select --trx "$TMP/displayonly.trx"

echo "== discovery cross-check (class level)"
DISC="$TMP/discovered.txt"
# --discover runs `dotnet test --list-tests`; --discovery-file feeds the SAME
# comparison a list produced elsewhere, so the selftest needs no build.
printf 'Excise.Core.Tests.RedactionFooTests.Removes1\nExcise.Core.Tests.RedactionFooTests.Removes2(mode: A)\n' > "$DISC"
check "every discovered class has a result -> pass" 0 \
    "$GATE" --label selftest --select --discovery-file "$DISC" --trx "$RUN/core.trx"
printf 'Excise.Core.Tests.RedactionGoneTests.Vanished\n' >> "$DISC"
check "a discovered class with no result fails" 1 \
    "$GATE" --label selftest --select --discovery-file "$DISC" --trx "$RUN/core.trx"
saw "and it names the class the filter collects" "RedactionGoneTests" \
    "$GATE" --label selftest --select --discovery-file "$DISC" --trx "$RUN/core.trx"
: > "$DISC"
check "an EMPTY discovery list fails (a vacuous proof is not a proof)" 1 \
    "$GATE" --label selftest --select --discovery-file "$DISC" --trx "$RUN/core.trx"

echo
if [ "$FAIL" -gt 0 ]; then
    echo "redaction-suite-floor selftest: FAILED ($FAIL of $((PASS + FAIL)) checks)"
    exit 1
fi
echo "redaction-suite-floor selftest: all $PASS checks passed"
