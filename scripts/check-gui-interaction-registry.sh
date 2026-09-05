#!/usr/bin/env bash
# #1374 — the GUI interaction registry gate.
#
# Regenerates tests/gui-interaction-registry.json from the XAML and the viewer
# control and diffs it against the tree, the same shape every other registry in
# this repo uses.
#
# What it protects:
#   - A command id that no menu item, shortcut or button reaches is unreachable
#     by a human. #1308 shipped exactly that: Sign Document was implemented,
#     tested, and callable from no production code.
#   - A control with no CommandAccessibility.CommandId cannot be driven by any
#     automation surface — not the batch runner, not the AppleScript terminology
#     (#1281), not the GUI workflow suite.
#   - Two menu items claiming one shortcut is a silent conflict.
#
# sourceRevision is stamped at generation time and differs on every commit by
# construction, so it is ignored in the diff — the same reasoning as #1357 for
# the capability registry, where diffing the timestamp made the gate
# unconditionally red.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
OUT=tests/gui-interaction-registry.json

python3 scripts/build-gui-interaction-registry.py

if [ "${1:-}" = "--update" ]; then
    echo "▶ $OUT regenerated; review the diff and commit it"
    exit 0
fi

rc=0
git diff --exit-code -I '"sourceRevision":' -- "$OUT" || rc=$?
if [ "$rc" = 0 ]; then
    # Leave the tree as we found it: only the revision stamp changed.
    git checkout -- "$OUT"
    exit 0
fi

cat >&2 <<'MSG'

FAIL: the GUI interaction registry is stale.

A menu item, shortcut, button or viewer gesture changed and the registry was not
regenerated with it. Run:

    scripts/check-gui-interaction-registry.sh --update

then READ the diff before committing. In particular:
  - a new entry under commandIdsWithNoGuiPath is a command no human can reach;
  - a new entry under controlsWithNoCommandId is a control no automation can drive;
  - a new duplicateShortcuts entry is two controls fighting over one key.
MSG
exit "$rc"
