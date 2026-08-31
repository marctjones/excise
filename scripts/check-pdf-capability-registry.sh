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
python3 scripts/build-pdf-capability-scorecard.py
git diff --exit-code -- \
  test-pdfs/manifests/pdf-spec-registry/generated/summary.json \
  test-pdfs/manifests/pdf-spec-registry/generated/capability-scorecard.json \
  test-pdfs/manifests/pdf-spec-registry/generated/capability-scorecard.md \
  test-pdfs/manifests/pdf-spec-registry/generated/verification-gaps.json \
  test-pdfs/manifests/pdf-spec-registry/generated/corpus-governance.json \
  test-pdfs/manifests/pdf-spec-registry/generated/evidence-collection.json \
  test-pdfs/manifests/pdf-spec-registry/generated/renderer-test-evidence-map.json \
  test-pdfs/manifests/pdf-spec-registry/generated/renderer-promotion-queue.json \
  test-pdfs/manifests/pdf-spec-registry/generated/evidence-deficiency-report.json \
  test-pdfs/manifests/pdf-spec-registry/generated/test-outcomes.json \
  test-pdfs/manifests/pdf-spec-registry/generated/reference-tool-evidence.json \
  test-pdfs/manifests/pdf-spec-registry/generated/atomic-fixture-evidence.json \
  test-pdfs/manifests/pdf-spec-registry/generated/implementation-evidence-map.json \
  test-pdfs/manifests/pdf-spec-registry/generated/test-suite-evidence-map.json
