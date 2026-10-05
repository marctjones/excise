#!/usr/bin/env bash
set -euo pipefail
exec python3 "$(dirname "$0")/code-health-diagnostics.py" check "$@"
