#!/usr/bin/env bash
#
# Gate asymmetry (#618).
#
#   CORRECTNESS is a BLOCKING GATE.   PERFORMANCE is a BUDGET.
#   A performance regression may NEVER be resolved by weakening a correctness
#   assertion.
#
# "Correctness first, performance second" is a slogan until something enforces
# it. Nothing did — and it showed:
#
#   8a8e661 ("perf: coalesce continuous-scroll tile renders") changed the tile
#   quantization constants AND, in the same commit, rewrote the expected values
#   of Excise.Avalonia.Tests/ContinuousDpiTests — a 400x600 viewport that asserted
#   a precise clip rect now asserted a 1280x1280 tile with entirely different
#   numbers. The edit was legitimate; the MECHANISM is not. A perf optimization
#   was able to redefine what a correctness test considered correct, with no
#   signal to anyone.
#
# This check makes that impossible to do quietly. It flags a diff that BOTH:
#   (a) touches a performance-sensitive code path, AND
#   (b) changes the EXPECTED VALUES of assertions in a test.
#
# It does not forbid the combination — sometimes a contract genuinely changes.
# It forces you to SEPARATE them: land the perf change and the expectation
# change in different pushes, so neither can hide inside the other.
#
# The durable fix for a flagged test is usually to state it as an INVARIANT
# instead of pinned numbers (#617): an invariant survives a legal optimization
# and still fails an illegal one, so it never needs rewriting in the first place.
#
# Usage:
#   scripts/check-gate-asymmetry.sh [base-ref]      # default: origin/develop
#
# WHY THE DEFAULT IS origin/develop, NOT origin/main (#965)
# ---------------------------------------------------------
# This gate asks "did one commit-range both touch a perf path AND rewrite a
# correctness expectation?" — a question about ONE change under review. Over an
# entire release cycle (origin/main...HEAD) the answer is legitimately yes:
# separate, individually-reviewed commits touch perf paths and other commits
# legitimately update expectations (the §9.4.2 fix, #928's test deletions).
# The old `origin/main` default therefore always fired, and every real caller —
# t0 and all three CI jobs — already passed `origin/develop` explicitly. The
# documented no-arg invocation was the ONLY one that failed, which reads as a
# broken gate and trains people to ignore it.
set -euo pipefail

BASE="${1:-origin/develop}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# A missing base ref used to `exit 0` with "skipping" — i.e. on a shallow clone
# with no origin refs this gate was silently INERT while reporting success.
# That is the vacuous-green shape #941 purged from five other gates, so it is
# a hard failure here too. GATE_ASYMMETRY_ALLOW_NO_BASE=1 is the explicit
# escape hatch for a context that genuinely cannot fetch the base ref; it says
# so out loud and still exits non-zero unless the caller opted in.
if ! git rev-parse --verify --quiet "$BASE" >/dev/null; then
  echo "check-gate-asymmetry: base ref '$BASE' not found."
  echo "  This gate compares HEAD against a base; without one it can only"
  echo "  pretend to pass. Fetch the ref (git fetch origin develop) or pass an"
  echo "  existing base: scripts/check-gate-asymmetry.sh <base-ref>"
  if [ "${GATE_ASYMMETRY_ALLOW_NO_BASE:-0}" = "1" ]; then
    # Exit 77 is the runner's "prerequisite missing" protocol (LOCAL_GATES.md):
    # never 0, so a run without a base shows a SKIPPED row, not a green.
    echo "  GATE_ASYMMETRY_ALLOW_NO_BASE=1 — continuing WITHOUT this gate (SKIPPED)."
    exit 77
  fi
  exit 1
fi

RANGE="$BASE...HEAD"
# The base is printed resolved so an acceptance in tests/gates.tsv can be SCOPED
# to one range ("#1358/base=<sha>"): the same red against any other base is NEW.
echo "==> range $RANGE base=$(git rev-parse "$BASE")"

# (a) Performance-sensitive paths: the render/scroll/tile hot paths and anything
#     under a benchmarks/hotspot tree.
#
# ⚠️ THESE ARE GIT PATHSPECS, AND A PATHSPEC THAT MATCHES NOTHING IS SILENT.
# Two of these entries were dead until the #941 audit: `…/PdfViewerControl` and
# `…/ContentStreamParser` were written without an extension or a wildcard, and
# git only takes a prefix as a match at a DIRECTORY boundary — every real file
# is `PdfViewerControl.*` / `ContentStreamParser.cs`, so both matched zero files
# and this gate quietly skipped them. Measured 2026-08-15: a commit touching
# Excise.Avalonia/Controls/PdfViewerControl.Continuous.cs while rewriting a
# numeric test expectation reported "OK (no performance-sensitive paths
# touched)" — the exact file family and the exact combination of 8a8e661, the
# commit this gate was written for.
#
# The preflight below turns that from a silent skip into a hard failure.
PERF_PATHS='
Excise.Rendering/
Excise.Avalonia/Controls/PdfViewerControl*
Excise.Core/Content/ContentStreamParser*
Excise.Core/Fonts/
tools/Excise.RenderTools/
Excise.Benchmarks/
'

# Preflight: every declared perf path must resolve to at least one TRACKED file.
# Without this, a rename (the pdfe->Excise one already did this once) silently
# narrows what the gate watches, and nothing anywhere reports the loss. A gate
# that quietly stops watching is worse than no gate: it still reads as green.
dead_specs=""
while IFS= read -r p; do
  [[ -z "$p" ]] && continue
  if [[ -z "$(git ls-files -- "$p" 2>/dev/null)" ]]; then
    dead_specs+="  $p"$'\n'
  fi
done <<< "$PERF_PATHS"

if [[ -n "$dead_specs" ]]; then
  cat <<DEAD

FAIL: check-gate-asymmetry declares performance-sensitive paths that match NO
tracked file. The gate is not watching them, and would report green for any
change under them:

$dead_specs
Fix the pathspec (a bare prefix like 'dir/Foo' does NOT match 'dir/Foo.cs' —
git matches a prefix only at a directory boundary; add '*' or the extension),
or drop the entry if the code genuinely moved away.
DEAD
  exit 1
fi

perf_hits=""
while IFS= read -r p; do
  [[ -z "$p" ]] && continue
  hit="$(git diff --name-only "$RANGE" -- "$p" 2>/dev/null || true)"
  [[ -n "$hit" ]] && perf_hits+="$hit"$'\n'
done <<< "$PERF_PATHS"

if [[ -z "${perf_hits// /}" ]]; then
  echo "==> gate asymmetry OK (no performance-sensitive paths touched)"
  exit 0
fi

# (b) Changed EXPECTED VALUES in tests. We look for modified assertion lines that
#     carry a literal — that is what "rewriting the expectation" looks like.
#     Added assertions are fine (new coverage). Only CHANGED ones are suspicious,
#     so we inspect removed (-) assertion lines: an expectation that used to exist
#     and no longer does.
test_files="$(git diff --name-only "$RANGE" -- '*Tests*.cs' '*.Tests/*.cs' 2>/dev/null || true)"

rewritten=""
if [[ -n "$test_files" ]]; then
  while IFS= read -r f; do
    [[ -z "$f" ]] && continue
    removed="$(git diff -U0 "$RANGE" -- "$f" \
      | grep -E '^-[^-]' \
      | grep -E '\.Should\(\)\.(Be|BeApproximately|Equal|BeExactly)\(' \
      | grep -E '[0-9]' || true)"
    [[ -n "$removed" ]] && rewritten+="$f"$'\n'
  done <<< "$test_files"
fi

if [[ -z "${rewritten// /}" ]]; then
  echo "==> gate asymmetry OK (perf paths touched, no correctness expectations rewritten)"
  exit 0
fi

# NO ESCAPE HATCH, deliberately (#1036). This gate used to be waived by a
# `Correctness-Expectations-Changed:` commit trailer. Commit trailers are
# banned in this repo, and replacing it with an env var or a magic phrase
# would be the same mechanism wearing a different hat.
#
# The consequence, stated so nobody has to rediscover it: a change that
# legitimately rewrites a correctness expectation cannot be pushed in the same
# range as a perf-path change. Push them separately. Splitting into two
# COMMITS does not help — this evaluates $BASE..HEAD as a range — so it has to
# be two pushes. That friction is the whole point: the gate exists because
# 8a8e661 changed tile quantization constants and rewrote the expected values
# of ContinuousDpiTests in the same commit, letting a perf optimisation
# silently redefine what a correctness test considered correct.

cat <<MSG

FAIL: a performance-sensitive change also REWROTE correctness expectations.

  perf-sensitive files touched:
$(echo "$perf_hits" | sed '/^$/d' | sort -u | sed 's/^/      /')

  tests whose expected values were changed or removed:
$(echo "$rewritten" | sed '/^$/d' | sort -u | sed 's/^/      /')

This is the exact shape of 8a8e661, where a perf change silently redefined what
a correctness test considered correct. Correctness is a BLOCKING GATE;
performance is a BUDGET. A perf regression may never be resolved by weakening a
correctness assertion.

Do ONE of these:

  1. PREFERRED — restate the test as an INVARIANT rather than pinned numbers
     (#617). An invariant survives a legal optimization and still fails an
     illegal one, so it never needs rewriting. See
     Excise.Avalonia.Tests/ContinuousDpiTests.ContinuousTileRequest_SatisfiesItsContract.

  2. If the contract genuinely changed, land it SEPARATELY from the perf
     change — two pushes, not two commits (this evaluates a range, so
     splitting commits does not separate them). Reviewing them apart is the
     point; a perf change and a redefinition of "correct" should never arrive
     together.

MSG
exit 1
