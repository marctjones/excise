#!/usr/bin/env bash
set -euo pipefail

python3 scripts/check-pdf-spec-registry.py --write-summary --write-verification-gaps test-pdfs/manifests/pdf-spec-registry/generated/verification-gaps.json
python3 scripts/build-pdf-corpus-governance.py
python3 scripts/collect-pdf-capability-evidence.py
python3 scripts/build-renderer-test-evidence-map.py
python3 scripts/build-pdf-evidence-maps.py
python3 scripts/build-renderer-promotion-queue.py
python3 scripts/build-pdf-evidence-deficiency-report.py
python3 scripts/collect-pdf-test-outcomes.py
python3 scripts/collect-pdf-reference-tool-evidence.py
python3 scripts/build-pdf-atomic-fixture-map.py
python3 scripts/build-pdf-evidence-attribution.py
python3 scripts/build-pdf-feature-cluster-scorecard.py
python3 scripts/build-pdf-capability-scorecard.py
# recordedAt and gitRevision are provenance stamped at generation time, so
# they differ on EVERY run by construction (#1357). Diffing them made this
# gate unconditionally red: two consecutive runs on a clean tree both failed,
# on the timestamp the gate itself had just written. Ignore those lines and
# compare the evidence, which is what the gate is for.
#
# On a PASS the only worktree change is the stamp this gate just wrote, so it
# is restored: otherwise the next runner_state_init keys the run "-dirty",
# every ledger row says treeDirty=yes and the clean-tree checkpoints become
# unreachable. On a FAIL the files stay put for review.
GENERATED=(
  test-pdfs/manifests/pdf-spec-registry/generated/summary.json
  test-pdfs/manifests/pdf-spec-registry/generated/capability-scorecard.json
  test-pdfs/manifests/pdf-spec-registry/generated/capability-scorecard.md
  test-pdfs/manifests/pdf-spec-registry/generated/verification-gaps.json
  test-pdfs/manifests/pdf-spec-registry/generated/corpus-governance.json
  test-pdfs/manifests/pdf-spec-registry/generated/evidence-collection.json
  test-pdfs/manifests/pdf-spec-registry/generated/renderer-test-evidence-map.json
  test-pdfs/manifests/pdf-spec-registry/generated/renderer-promotion-queue.json
  test-pdfs/manifests/pdf-spec-registry/generated/evidence-deficiency-report.json
  test-pdfs/manifests/pdf-spec-registry/generated/test-outcomes.json
  test-pdfs/manifests/pdf-spec-registry/generated/reference-tool-evidence.json
  test-pdfs/manifests/pdf-spec-registry/generated/atomic-fixture-evidence.json
  test-pdfs/manifests/pdf-spec-registry/generated/evidence-attribution.json
  test-pdfs/manifests/pdf-spec-registry/generated/feature-cluster-scorecard.json
  test-pdfs/manifests/pdf-spec-registry/generated/implementation-evidence-map.json
  test-pdfs/manifests/pdf-spec-registry/generated/test-suite-evidence-map.json
)
rc=0
git diff --exit-code -I '"recordedAt":' -I '"gitRevision":' -- "${GENERATED[@]}" || rc=$?
if [ "$rc" = 0 ]; then
  git checkout -- "${GENERATED[@]}"
fi
exit "$rc"
