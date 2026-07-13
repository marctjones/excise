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
#   of Pdfe.Avalonia.Tests/ContinuousDpiTests — a 400x600 viewport that asserted
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
# It forces you to say so out loud, with:
#
#   Correctness-Expectations-Changed: <why>
#
# in the commit message. That is a design review, not a rubber stamp.
#
# The durable fix for a flagged test is usually to state it as an INVARIANT
# instead of pinned numbers (#617): an invariant survives a legal optimization
# and still fails an illegal one, so it never needs rewriting in the first place.
#
# Usage:
#   scripts/check-gate-asymmetry.sh [base-ref]      # default: origin/main
set -euo pipefail

BASE="${1:-origin/main}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

git rev-parse --verify --quiet "$BASE" >/dev/null || {
  echo "check-gate-asymmetry: base ref '$BASE' not found; skipping."
  exit 0
}

RANGE="$BASE...HEAD"

# (a) Performance-sensitive paths: the render/scroll/tile hot paths and anything
#     under a benchmarks/hotspot tree.
PERF_PATHS='
Pdfe.Rendering/
Pdfe.Avalonia/Controls/PdfViewerControl
Pdfe.Core/Content/ContentStreamParser
Pdfe.Core/Fonts/
tools/Pdfe.RenderTools/
Pdfe.Benchmarks/
'

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

# Escape hatch: an explicit, reviewed acknowledgement in the commit message.
if git log "$BASE..HEAD" --format=%B | grep -qi '^Correctness-Expectations-Changed:'; then
  echo "==> gate asymmetry: expectations changed, but ACKNOWLEDGED in the commit message."
  git log "$BASE..HEAD" --format=%B | grep -i '^Correctness-Expectations-Changed:' | sed 's/^/    /'
  exit 0
fi

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
     Pdfe.Avalonia.Tests/ContinuousDpiTests.ContinuousTileRequest_SatisfiesItsContract.

  2. If the contract genuinely changed, say so out loud. Add to the commit body:

       Correctness-Expectations-Changed: <what changed and why it is still correct>

     That is a design review, not a rubber stamp.

MSG
exit 1
