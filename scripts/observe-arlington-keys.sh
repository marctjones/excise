#!/usr/bin/env bash
# Regenerate test-pdfs/manifests/arlington-observation.json (#1703).
# A RECORDER, not a gate: it writes down what excise's parser exposes for each
# ISO 32000-2 object-model key over a real-document corpus. The test it runs
# asserts only that the walk reached something.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
EXCISE_OBSERVE_ARLINGTON=1 scripts/t.sh Excise.Rendering.Tests \
  --filter "FullyQualifiedName~ArlingtonKeyObservationTests"
echo
python3 scripts/report-arlington-status.py --only security --limit 15
