#!/usr/bin/env bash
#
# check-redaction-suite-floor.sh — the redaction suites' FLOOR, and (with
# --select/--sln/--discover) the proof that a trx UNION stands in for them.
#
# WHY THIS EXISTS (#1767)
#
# `redaction-suites` is the one gate CLAUDE.md calls non-negotiable, and until
# now it had no floor. It collected 1898 results on 2026-09-21; if a corpus
# became unreachable and it collected 40, `Passed! Failed: 0` is byte-identical
# — the #1527 shape, where 1,071 corpus-gated rows were never COLLECTED and
# nothing could see it. The runner's zero-tests guard only catches zero.
# `CorpusRowFloorGateTests` is a per-corpus zero check; 40 of 1194 passes it.
# Every other oracle family in t1 has a floor (rendering 3000, App 13, Core 14).
#
# A floor is not a target. It fails only when the count DROPS, so adding tests
# never needs a bump; losing a class, a corpus, or a whole assembly does.
#
# TWO NUMBERS PER ASSEMBLY, NOT ONE
#
#   results  every <UnitTestResult> element — catches rows that were never
#            COLLECTED (discovery-time MemberData enumeration, #1527).
#   passed   outcome="Passed" — catches a mass SKIP, which leaves the result
#            count intact and verifies nothing (check-oracle-floor.sh's case).
#
# Count ELEMENTS, never distinct names: MemberData theory rows share a display
# name, so a set of names reads 1888 where the run reported 1898.
#
# THE UNION MODE, AND WHY IT IS NOT A SHORTCUT
#
# The solution-wide `FullyQualifiedName~Redaction` pass costs ~435 s and
# executes no unique test: its five assemblies each run unfiltered (or under
# filters that partition them) elsewhere in the same tier. --select reads those
# runs instead. The containment argument has to be airtight or the redesign
# reproduces the blindness it replaces, so it is checked three ways:
#
#   --run-dir  every trx must live in THIS run's log directory. A producer that
#              --resume took from a checkpoint hands back a path in an older run
#              directory; that is stale evidence and it FAILS. The redaction gate
#              accepts only what it just watched happen.
#   --sln      every *.Tests.csproj in the solution must be represented by at
#              least one result. This is the containment proof and it needs no
#              filter semantics at all: producers that run every test of every
#              test project are a superset of any filter over them.
#   --discover `dotnet test <sln> --list-tests --filter FullyQualifiedName~Redaction`
#              is the AUTHORITATIVE population (3.3 s, discovery only), and every
#              class it names must have a result here.
#
# --discover is checked per CLASS, not per test, deliberately. The #894 vstest
# channel loss drops about one result per run: on 2026-09-21 the union held 735
# of the 736 discovered method FQNs, the loser being one method of a class whose
# other five reported. A per-test check would have reddened a healthy run.
# Per-test completeness is `check-test-count.sh`'s job (#894/#1051), which owns
# the re-run and the loss ratchet; this gate must not duplicate that verdict.
#
# THE SELECTION RULE
#
# A result is a redaction result when 'redaction' appears, case-insensitively,
# in className + '.' + methodName. Measured against the real filtered run of
# 2026-09-21 this is EXACT — 0 results selected that the filter did not collect
# — and its two edges are both live in the repo:
#   * Unredaction* IS selected (the filter's ~ is case-insensitive 'contains')
#   * a [Theory] display name like `...(mode: Redaction)` is NOT selected, and
#     the filter does not collect it either: `~` matches FullyQualifiedName,
#     which carries no argument list.
#
# Usage:
#   scripts/check-redaction-suite-floor.sh --label L [--trx P]... [--trx-dir D]...
#       [--select] [--run-dir D] [--sln F] [--discover]
#       [--floor ASM=RESULTS:PASSED]... [--total-floor RESULTS:PASSED]
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

LABEL="redaction suites"
SELECT=0
DISCOVER=0
DISCOVERY_FILE=""
RUN_DIR=""
SLN=""
TOTAL_FLOOR=""
TRX_FILES=()
FLOORS=()

while [ "$#" -gt 0 ]; do
    case "$1" in
        --label)       LABEL="${2:-}"; shift 2 ;;
        --trx)         TRX_FILES+=("${2:-}"); shift 2 ;;
        --trx-dir)
            # A solution-wide row writes a DIRECTORY of auto-named trx (#1368),
            # one per project, so the dir is the unit of evidence, not a file.
            if [ -d "${2:-}" ]; then
                while IFS= read -r f; do TRX_FILES+=("$f"); done \
                    < <(find "${2:-}" -maxdepth 1 -name '*.trx' | LC_ALL=C sort)
            else
                echo "FAIL: $LABEL produced no trx directory at ${2:-}" >&2
                echo "      The test run did not happen, so its green means nothing." >&2
                exit 1
            fi
            shift 2 ;;
        --select)      SELECT=1; shift ;;
        --discover)    DISCOVER=1; shift ;;
        # The same comparison against a list produced elsewhere — how the
        # selftest exercises it without a build, and how you re-check a red
        # without paying for discovery twice.
        --discovery-file) DISCOVERY_FILE="${2:-}"; shift 2 ;;
        --run-dir)     RUN_DIR="${2:-}"; shift 2 ;;
        --sln)         SLN="${2:-}"; shift 2 ;;
        --floor)       FLOORS+=("${2:-}"); shift 2 ;;
        --total-floor) TOTAL_FLOOR="${2:-}"; shift 2 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done

if [ "${#TRX_FILES[@]}" -eq 0 ]; then
    echo "FAIL: $LABEL — no trx named. Nothing was verified." >&2
    exit 1
fi

DISCOVERED="$DISCOVERY_FILE"
if [ "$DISCOVER" = "1" ]; then
    [ -n "$SLN" ] || { echo "--discover needs --sln" >&2; exit 2; }
    # --no-build is only honest once the binaries are proven newer than their
    # sources; a script target is opaque to lib-runner's own guard, so ask here.
    "$ROOT/scripts/assert-fresh.sh" --configuration "${CONFIG:-Debug}" || exit $?
    DISCOVERED="$(mktemp)"
    trap 'rm -f "$DISCOVERED"' EXIT
    if ! dotnet test "$SLN" -c "${CONFIG:-Debug}" --no-build --list-tests \
            --filter "FullyQualifiedName~Redaction" 2>/dev/null \
            | sed -n 's/^    \(.*\)$/\1/p' | sed 's/[[:space:]]*$//' | grep -v '^$' \
            > "$DISCOVERED"; then
        : # a filter that selects nothing exits 0 too; the emptiness check is below
    fi
fi

if [ -n "$DISCOVERED" ] && [ ! -s "$DISCOVERED" ]; then
    # A proof against an empty population proves nothing, and that is exactly
    # how a renamed filter or an unbuilt solution would read.
    echo "FAIL: $LABEL — the FullyQualifiedName~Redaction population is EMPTY." >&2
    echo "      Discovery named zero tests (broken discovery, an unbuilt solution," >&2
    echo "      or a renamed filter). A union cannot be proven complete against" >&2
    echo "      nothing, so this is a failure, not a pass." >&2
    exit 1
fi

python3 - "$LABEL" "$SELECT" "$RUN_DIR" "$SLN" "$DISCOVERED" "$TOTAL_FLOOR" \
         "$(printf '%s\n' ${FLOORS[@]+"${FLOORS[@]}"} | tr '\n' ' ')" \
         "${TRX_FILES[@]}" <<'PY'
import os, sys, collections, xml.etree.ElementTree as ET

label, select, run_dir, sln, discovered, total_floor, floors_blob = sys.argv[1:8]
trx_files = sys.argv[8:]
select = select == "1"
N = "{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}"
fail = []


def parse_floor(spec, where):
    # RESULTS:PASSED, or RESULTS alone (PASSED floor 0).
    r, _, p = spec.partition(":")
    try:
        return int(r), int(p or 0)
    except ValueError:
        sys.exit("bad floor %r in %s" % (spec, where))


floors = {}
for item in floors_blob.split():
    asm, _, spec = item.partition("=")
    floors[asm] = parse_floor(spec, "--floor")

by_asm = collections.defaultdict(collections.Counter)
present_asm = set()
classes_seen = set()
failed_tests = []
unknown = 0

for path in trx_files:
    if not os.path.isfile(path):
        fail.append("no trx at %s — the producer did not run, or its evidence is gone" % path)
        continue
    if run_dir and not os.path.realpath(path).startswith(os.path.realpath(run_dir) + os.sep):
        # --resume hands a checkpointed producer the trx of an EARLIER run.
        # That is evidence this run did not observe, and the redaction gate
        # does not accept it. Re-run the row it names.
        fail.append("trx %s is not under this run's log directory %s — its producer "
                    "was checkpointed away; re-run that row" % (path, run_dir))
        continue
    try:
        tree = ET.parse(path)
    except ET.ParseError as exc:
        fail.append("trx %s is torn or truncated (%s)" % (path, exc))
        continue
    defs = {}
    for ut in tree.iter(N + "UnitTest"):
        m = ut.find(N + "TestMethod")
        if m is not None:
            defs[ut.get("id")] = (m.get("className") or "", m.get("name") or "",
                                  m.get("codeBase") or "")
    for res in tree.iter(N + "UnitTestResult"):
        cls, meth, code = defs.get(res.get("testId"), ("", "", ""))
        asm = os.path.basename(code)
        asm = asm[:-4] if asm.endswith(".dll") else ""
        if not asm:
            unknown += 1
            continue
        present_asm.add(asm)
        if select and "redaction" not in (cls + "." + meth).lower():
            continue
        outcome = res.get("outcome") or "?"
        by_asm[asm]["results"] += 1
        by_asm[asm][outcome] += 1
        classes_seen.add(cls)
        if outcome == "Failed":
            failed_tests.append(res.get("testName") or (cls + "." + meth))

if unknown:
    fail.append("%d result(s) carry no test definition — the trx is malformed" % unknown)

total = collections.Counter()
print("%s: %d trx file(s)%s" % (label, len(trx_files), ", selection ~Redaction" if select else ""))
for asm in sorted(set(by_asm) | set(floors)):
    c = by_asm.get(asm, collections.Counter())
    total.update(c)
    fr, fp = floors.get(asm, (0, 0))
    print("  %-28s results=%-5d passed=%-5d skipped=%-5d failed=%-3d (floor %d:%d)"
          % (asm, c["results"], c["Passed"], c["NotExecuted"], c["Failed"], fr, fp))
    if c["results"] < fr:
        fail.append("%s reported %d result(s); the floor is %d" % (asm, c["results"], fr))
    if c["Passed"] < fp:
        fail.append("%s PASSED %d test(s); the floor is %d (a mass skip leaves the "
                    "result count intact and verifies nothing)" % (asm, c["Passed"], fp))
print("  %-28s results=%-5d passed=%-5d skipped=%-5d failed=%-3d"
      % ("TOTAL", total["results"], total["Passed"], total["NotExecuted"], total["Failed"]))

if total_floor:
    fr, fp = parse_floor(total_floor, "--total-floor")
    if total["results"] < fr:
        fail.append("%d result(s) in total; the floor is %d" % (total["results"], fr))
    if total["Passed"] < fp:
        fail.append("%d passed in total; the floor is %d" % (total["Passed"], fp))

if failed_tests:
    fail.append("%d redaction test(s) FAILED: %s"
                % (len(failed_tests), ", ".join(sorted(failed_tests)[:5])))

if sln:
    # Containment without filter semantics: a producer set that runs every test
    # of every test project is a superset of any filter over the solution. So
    # every test project must be REPRESENTED (by a result of any kind — not a
    # selected one: Excise.Avalonia.Tests legitimately holds no redaction test).
    want = set()
    for line in open(sln, encoding="utf-8", errors="replace"):
        for tok in line.replace('"', ",").split(","):
            tok = tok.strip()
            if tok.endswith("Tests.csproj"):
                want.add(os.path.basename(tok.replace("\\", "/"))[:-len(".csproj")])
    if not want:
        fail.append("no *.Tests.csproj found in %s — the completeness check is vacuous" % sln)
    for asm in sorted(want - present_asm):
        fail.append("%s is a test project of %s and produced NO result here; the union "
                    "cannot be a superset of a solution-wide filter without it" % (asm, sln))
    print("  test projects in %s: %d, all represented: %s"
          % (os.path.basename(sln), len(want), "yes" if not (want - present_asm) else "NO"))

if discovered:
    disc_classes = set()
    n = 0
    for line in open(discovered, encoding="utf-8", errors="replace"):
        line = line.strip()
        if not line:
            continue
        n += 1
        disc_classes.add(line.split("(")[0].rsplit(".", 1)[0])
    orphans = sorted(disc_classes - classes_seen)
    print("  discovery: %d case(s) in %d class(es); classes with no result here: %d"
          % (n, len(disc_classes), len(orphans)))
    for cls in orphans[:10]:
        fail.append("class %s is collected by FullyQualifiedName~Redaction and has NO "
                    "result in the union" % cls)
    if len(orphans) > 10:
        fail.append("...and %d more class(es) collected by the filter with no result"
                    % (len(orphans) - 10))

if fail:
    print()
    print("FAIL: %s" % label)
    for f in fail:
        print("  - %s" % f)
    print()
    print("  Do NOT lower a floor to make this pass. The redaction gate is the one")
    print("  gate CLAUDE.md calls non-negotiable; a green it cannot earn is worse")
    print("  than a red (#1767, #1527).")
    sys.exit(1)
print("  ok")
PY
