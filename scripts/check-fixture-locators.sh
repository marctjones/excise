#!/usr/bin/env bash
#
# Fixture-locator architecture guard (#1527, #1525).
#
# #1527: three redaction gates ran 2 of 195 assertions in every git-worktree
# session for a month. Not because anything was disabled — CLAUDE.md's rule
# that "there is no flag to skip them" was literally true. Each of thirteen
# test files had its OWN copy of "walk up N directories from the test binary
# looking for test-pdfs/", the bound differed per copy (6 or 8), and from a
# worktree the corpora are 7 levels up. The rows did not fail and mostly did
# not even skip: the MemberData sources enumerate the corpus at DISCOVERY, so
# 1,071 rows were never COLLECTED. "Passed! Failed: 0" on 39 rows is
# byte-identical to "Passed! Failed: 0" on 1,110.
#
# Two things had to be true for that to ship, and this gate forbids both.
#
#   1. Thirteen hand-rolled locators with thirteen different bounds. No single
#      review saw them together. There is now ONE:
#      Excise.Core.Tests/TestSupport/TestRepoLayout.cs, which reads git's own
#      worktree plumbing (the .git file's "gitdir:" and its "commondir") to
#      find the MAIN checkout, where the gitignored corpora live. A bigger
#      bound is not a fix: it only works because this repo's worktrees happen
#      to sit inside the main checkout.
#
#   2. Nothing related "this class needs a corpus" to "this class must produce
#      rows". The runtime floor is CorpusRowFloorGateTests, and its registry is
#      a hand-written list — so this gate DERIVES the population instead and
#      fails if the two disagree. A hand-listed registry omits exactly the
#      gate nobody was thinking about; deriving it here is what turned up
#      ReferenceRedactorComparisonTests, which #1527 named and the first draft
#      of the registry missed.
#
# Static, no build, no corpus, no renderer: a t0 row.
#
# Usage: scripts/check-fixture-locators.sh
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

LAYOUT="Excise.Core.Tests/TestSupport/TestRepoLayout.cs"
REGISTRY="Excise.Rendering.Tests/Differential/CorpusRowFloorGateTests.cs"
FAIL=0

TEST_PROJECTS=()
while IFS= read -r d; do TEST_PROJECTS+=("$d"); done < <(
  find . -maxdepth 1 -type d -name 'Excise.*.Tests' -not -path '*/.claude/*' | sed 's|^\./||' | sort
)
[ ${#TEST_PROJECTS[@]} -gt 0 ] || { echo "FAIL: found no Excise.*.Tests directories to check"; exit 1; }

echo "==> fixture-locator guard over: ${TEST_PROJECTS[*]}"

# ---------------------------------------------------------------------------
# 1. The shared locator must exist and must be compiled into every test
#    project that has a reason to resolve repository fixtures.
# ---------------------------------------------------------------------------
if [ ! -f "$LAYOUT" ]; then
  echo "FAIL: the shared fixture locator is missing: $LAYOUT"
  echo "      #1527's fix is ONE locator, not a larger depth bound."
  exit 1
fi

# ---------------------------------------------------------------------------
# 2. No BOUNDED ancestor walk anywhere in test code.
#
#    A bounded walk is the #1527/#1525 defect in its exact shipped form. There
#    is deliberately NO allowlist: an upward walk is correct for read-only
#    repository data (and TestRepoLayout does it, unbounded, in one place), and
#    for BUILD OUTPUT the answer is not a different bound but
#    TestRepoLayout.FindFileInLocalCheckout — a worktree resolving the main
#    checkout's excise.dll would silently test the wrong binary.
# ---------------------------------------------------------------------------
BOUNDED=$(grep -rnE \
  'for *\([^;]*= *0; *[A-Za-z_]+ *< *[0-9]+ *(&&[^;]*(dir|directory|d|folder)[^;]*!= *null)? *;[^)]*\)' \
  --include='*.cs' "${TEST_PROJECTS[@]}" 2>/dev/null \
  | grep -E 'up *<|(&&[^;]*(dir|directory|folder)[^;]*!= *null)' || true)

if [ -n "$BOUNDED" ]; then
  echo
  echo "FAIL: a BOUNDED upward directory walk in test code."
  echo "      This is #1527 in its shipped form. Three redaction gates ran 2 of 195"
  echo "      assertions for a month because a bound of 6 could not reach a corpus that"
  echo "      was 7 levels up, and the rows were never collected so nothing could see it."
  echo
  echo "      Use the ONE shared locator instead:"
  echo "        TestRepoLayout.FindFile(\"test-pdfs\", \"smoke\", \"x.pdf\")   read-only repo data"
  echo "        TestRepoLayout.FindDirectory(\"test-pdfs/smoke\")"
  echo "        TestRepoLayout.FindFileInLocalCheckout(...)              BUILD OUTPUT ONLY"
  echo
  echo "      Do NOT raise a bound, and do NOT add an exception here."
  echo "$BOUNDED" | sed 's/^/        /'
  FAIL=1
fi

# ---------------------------------------------------------------------------
# 3. No hand-rolled '..'-counting from the working directory.
#
#    The three bound-6 locators counted ".." from the test host's CWD, which is
#    the assembly output directory — a different anchor from the bound-8 family
#    and part of why the two groups needed different depths and nobody noticed.
# ---------------------------------------------------------------------------
DOTDOT=$(grep -rn 'Repeat("\.\.", ' --include='*.cs' "${TEST_PROJECTS[@]}" 2>/dev/null || true)
if [ -n "$DOTDOT" ]; then
  echo
  echo "FAIL: test code counting '..' upward by hand."
  echo "      Anchor on TestRepoLayout, which resolves from AppContext.BaseDirectory"
  echo "      and does not depend on the test host's working directory at all."
  echo "$DOTDOT" | sed 's/^/        /'
  FAIL=1
fi

# ---------------------------------------------------------------------------
# 3b. No hand-rolled ANCHOR walk: the literal ".git" / "excise.sln" in test code.
#
#    Checks 2 and 3 forbid a walk that is BOUNDED. #1706 is the walk that is
#    UNBOUNDED and still wrong: it stops at the FIRST ".git" or "excise.sln",
#    and in a git worktree that is the WORKTREE root -- which holds only the
#    tracked subset, not the gitignored corpora. Both marks the CLAUDE.md
#    warning ("neither is anchoring on .git or excise.sln") named; for a month
#    the gate enforced only the loop's shape, so 57 files grew under it and a
#    redaction differential, an unredaction reference comparison and two
#    competitor-adapter tests skipped in every worktree behind a false
#    "not present". Checks 2 and 3 tested boundedness when the property that
#    mattered was the anchor.
#
#    ⚠️ Keyed on the ANCHOR LITERAL, not on loop syntax, because loop syntax is
#    exactly what let #1706 hide. The population was first derived twice by
#    shape and undercounted both times: once by a `.Parent` traversal (missing
#    a walk written with Directory.GetParent) and once by the `test-pdfs`
#    corpus name (missing a walk that joined the gitignored tools/vendor/).
#    A walk must TEST FOR the anchor to know where to stop, whatever loop it
#    uses, so the literal is the invariant.
#
#    Exempt, by construction rather than by allowlist: the locator itself and
#    its own tests (the one place that legitimately knows what a checkout looks
#    like); comment lines; and lines that CREATE an anchor
#    (CreateDirectory / WriteAll*), because a test that builds a synthetic
#    worktree to exercise a locator is a builder, not a walker.
#
#    Limits, stated rather than hidden: an anchor assembled from parts at
#    runtime ("." + "git") or read from a constant in another file evades
#    this. Nothing in the repo does either today.
# ---------------------------------------------------------------------------
ANCHOR=$(grep -rnE '"(\.git|excise\.sln)"' --include='*.cs' "${TEST_PROJECTS[@]}" 2>/dev/null \
  | grep -vE 'TestSupport/TestRepoLayout(Tests)?\.cs:' \
  | grep -vE '^[^:]+:[0-9]+:[[:space:]]*(//|/\*|\*)' \
  | grep -vE 'CreateDirectory|WriteAll' || true)

if [ -n "$ANCHOR" ]; then
  echo
  echo "FAIL: a hand-rolled upward walk anchored on \".git\" or \"excise.sln\" in test code."
  echo "      This is #1706. Both anchors mark the CHECKOUT you are running in, and in a git"
  echo "      worktree that checkout holds only the TRACKED subset: the gitignored corpora"
  echo "      (test-pdfs/smoke, pdfjs, ...) and tool directories (tools/vendor) live in the"
  echo "      MAIN checkout. A walk that stops at the first anchor either misses them and"
  echo "      skips with a false \"not present\", or finds a partial test-pdfs and believes"
  echo "      it has a complete corpus. It does not matter that the walk is unbounded."
  echo
  echo "      Use the ONE shared locator instead:"
  echo "        TestRepoLayout.FindFile(\"test-pdfs\", \"smoke\", \"x.pdf\")     gitignored OR tracked data"
  echo "        TestRepoLayout.FindDirectory(\"tools\", \"vendor\", \"itext\")"
  echo "        TestRepoLayout.AbsenceReason(what, paths...)                 a skip reason a checker can falsify"
  echo "        TestRepoLayout.LocalCheckoutRoot                             THIS worktree's own source / artifacts"
  echo "        TestRepoLayout.FindFileInLocalCheckout(...)                  BUILD OUTPUT ONLY"
  echo
  echo "      Building a synthetic checkout to test a locator? Put the anchor on a"
  echo "      CreateDirectory/WriteAll* line and it is exempt. Do NOT add an exception here."
  echo "$ANCHOR" | sed 's/^/        /'
  FAIL=1
fi

# ---------------------------------------------------------------------------
# 4. Every class that enumerates a GITIGNORED corpus into a theory must have a
#    declared collected-row floor.
#
#    ⚠️ Detection is a SAME-FILE heuristic and its limits are #1531: a theory
#    whose enumeration sits behind a helper in another file, a corpus not in
#    the list below, or a corpus-gated theory in a project with no floor
#    registry all escape it. Accepted for now; it already earned its keep.
#
#    Population DERIVED, not listed: a file that (a) builds a TheoryData,
#    (b) enumerates a directory, and (c) names one of the gitignored corpora is
#    a class whose rows vanish when the corpus is unreachable. That is the
#    #1527 shape exactly, and it is the one thing no other gate can see.
# ---------------------------------------------------------------------------
# Measured with `git check-ignore`, not assumed: test-pdfs/pdf20,
# generated-regressions, manifests, rendering-contracts and sample-pdfs are
# TRACKED and present in every worktree. pdf20 was in this list until it was
# checked, and being wrong here over-reports classes as corpus-gated.
# Deriving the list from git is #1531.
GITIGNORED='test-pdfs/(smoke|federal|pdfjs|pdfium|verapdf-corpus|itext|poppler|isartor|altona|ghent|pdfua|samples|local-real-world|redaction-adversarial|redaction-synthetic|baselines)'
MISSING=""
FOUND=0
while IFS= read -r f; do
  [ "$f" = "$REGISTRY" ] && continue
  grep -q 'TheoryData' "$f" || continue
  grep -qE 'EnumerateFiles|GetFiles' "$f" || continue
  grep -qE "$GITIGNORED" "$f" || continue

  FOUND=$(( FOUND + 1 ))
  cls="$(basename "$f" .cs)"
  if ! grep -q "\"$cls\"" "$REGISTRY"; then
    MISSING="$MISSING        + $cls   ($f)
"
  fi
done < <(grep -rl 'TheoryData' --include='*.cs' "${TEST_PROJECTS[@]}" 2>/dev/null | sort)

if [ -n "$MISSING" ]; then
  echo
  echo "FAIL: a corpus-gated theory class has no declared collected-row floor."
  echo "      These classes build their theory rows by enumerating a GITIGNORED corpus"
  echo "      at discovery time. When the corpus is unreachable the rows are not skipped"
  echo "      and not failed — they are never COLLECTED, so #1172's skip gate has no"
  echo "      NotExecuted row to read and #894's count gate has nothing to count. The"
  echo "      run reports itself green (#1527)."
  echo
  echo "      Register each one in CorpusRowFloorGateTests.Gates with the corpora it"
  echo "      needs, its public row source, and a floor:"
  printf '%s' "$MISSING"
  FAIL=1
fi
echo "==> $FOUND corpus-gated theory class(es) found; each must have a row floor"

# ---------------------------------------------------------------------------
# 5. The registry itself must not be empty, and must resolve corpora through
#    the shared locator rather than re-deriving them.
# ---------------------------------------------------------------------------
if [ ! -f "$REGISTRY" ]; then
  echo "FAIL: the collected-row floor registry is missing: $REGISTRY"
  FAIL=1
elif ! grep -q 'TestRepoLayout' "$REGISTRY"; then
  echo "FAIL: $REGISTRY must resolve corpora through TestRepoLayout — the whole point is"
  echo "      that it answers 'is the corpus reachable' INDEPENDENTLY of the gate classes."
  FAIL=1
fi

if [ "$FAIL" -ne 0 ]; then
  exit 1
fi

echo "==> fixture-locator guard OK: one shared locator, no bounded walks,"
echo "    every corpus-gated theory class has a declared collected-row floor."
