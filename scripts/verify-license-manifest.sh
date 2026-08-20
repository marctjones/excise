#!/usr/bin/env bash
#
# The release must SHIP the manifest that was reviewed (#1082).
#
# release.yml used to run generate-license-manifest.sh and ship whatever came
# out. So the attribution users saw was not the attribution that was reviewed,
# tested and gated -- it was whatever the NuGet cache produced on the release
# runner that day. Every gate in t0 protected a file the release then replaced.
#
# This regenerates into a temp path and DIFFS against the checked-in manifest.
# Drift means a dependency changed without the manifest being regenerated and
# reviewed, which is precisely the event worth stopping a release for.
#
# Only possible since #1081 made regeneration byte-identical; with a wall-clock
# timestamp inside, every release drifted by construction and this check could
# only ever have failed.
#
# Usage: scripts/verify-license-manifest.sh
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

COMMITTED="Excise.App/Assets/third-party-licenses.json"
[[ -f "$COMMITTED" ]] || { echo "❌ no manifest at $COMMITTED" >&2; exit 1; }

BACKUP="$(mktemp)"
cp "$COMMITTED" "$BACKUP"
# Restore the reviewed file no matter how we leave: this script must never be
# the reason a release ships a regenerated manifest.
trap 'cp "$BACKUP" "$COMMITTED"; rm -f "$BACKUP"' EXIT

echo "▶ regenerating to compare (the reviewed file is restored on exit)"
./scripts/generate-license-manifest.sh >/dev/null

if diff -u "$BACKUP" "$COMMITTED" > /tmp/license-manifest.diff 2>&1; then
    echo "✅ the checked-in licence manifest matches what the dependencies produce."
    exit 0
fi

cat >&2 <<'MSG'
❌ the checked-in licence manifest does NOT match the current dependencies.

   A dependency changed without the manifest being regenerated and reviewed.
   The release is stopped rather than silently shipping attribution nobody
   looked at.

   To fix:
       ./scripts/generate-license-manifest.sh
       scripts/check-license-compliance.sh      # policy: permitted/banned/review
       git add Excise.App/Assets/third-party-licenses.json
       # review the diff -- a NEW package means a new licence to classify

   Diff (regenerated vs checked-in):
MSG
sed 's/^/     /' /tmp/license-manifest.diff >&2
exit 1
