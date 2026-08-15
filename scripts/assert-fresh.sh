#!/usr/bin/env bash
# Refuse stale --no-build executions. Use EXCISE_ALLOW_STALE_NO_BUILD=1 only
# when intentionally measuring an old binary.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
exec python3 "$ROOT/scripts/assert_fresh_build.py" --repo-root "$ROOT" "$@"
