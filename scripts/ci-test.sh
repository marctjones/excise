#!/usr/bin/env bash
# ci-test.sh: Local CI gate simulation — thin wrapper (#646).
#
# This used to be a hand-duplicated subset of .github/workflows/ci.yml's
# steps, and drifted from it (missing check-gate-asymmetry.sh,
# check-skip-budget.sh, verify-true-redaction.sh at various points). That
# drift is exactly the problem #646 exists to fix: there is now ONE
# definition of "the correctness gate" — scripts/test-tier.sh t1 — and
# ci.yml, ci-test.sh, and a pre-push hook all point at it instead of each
# maintaining their own copy.
exec "$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/test-tier.sh" t1 "$@"
