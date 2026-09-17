#!/usr/bin/env bash
# Build tools/screen-probe (#1544) with the system Swift toolchain.
# Output: tools/screen-probe/bin/screen-probe (a build product; see .gitignore).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
mkdir -p "$ROOT/tools/screen-probe/bin"
xcrun swiftc -O -o "$ROOT/tools/screen-probe/bin/screen-probe" "$ROOT/tools/screen-probe/ScreenProbe.swift"
echo "built $ROOT/tools/screen-probe/bin/screen-probe"
