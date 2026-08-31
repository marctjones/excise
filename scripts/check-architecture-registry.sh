#!/usr/bin/env bash
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

exec python3 scripts/check_architecture_registry.py \
  --check-conformance \
  --check-diagrams \
  "$@"
