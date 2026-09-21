#!/usr/bin/env bash
# Fetch the Arlington PDF Model at the revision pinned in the capability
# registry (#1709).
#
# WHY THIS IS A DOWNLOAD AND NOT A VENDORED COPY: the model is 36 MB and
# regenerating the inventory from it is deterministic, so the repo tracks the
# small generated inventory and this script, not the source. Same pattern as
# the twenty other download-*.sh corpora scripts.
#
# The git revision comes from test-pdfs/manifests/pdf-spec-registry/registry.json
# so there is ONE pin, not two that can drift. The tarball digest is checked
# because a git SHA names a tree, not the bytes GitHub happens to serve.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

REV="$(python3 -c "
import json
d=json.load(open('test-pdfs/manifests/pdf-spec-registry/registry.json'))
print(next(s['revision'] for s in d['sources'] if s['id']=='arlington-pdf-model'))")"
SHA256=587265b2af48561079147da04868ed5cd7f9753d1fa339c0ffb8cbf12cc6abc4
DEST=test-pdfs/arlington
TARBALL="$DEST/arlington-$REV.tar.gz"

if [ -d "$DEST/arlington-pdf-model-$REV" ]; then
  echo "already present: $DEST/arlington-pdf-model-$REV"
  exit 0
fi

mkdir -p "$DEST"
echo "fetching Arlington PDF Model @ $REV (Apache-2.0)"
curl -fsSL -o "$TARBALL" \
  "https://github.com/pdf-association/arlington-pdf-model/archive/$REV.tar.gz"

got="$(shasum -a 256 "$TARBALL" | awk '{print $1}')"
if [ "$got" != "$SHA256" ]; then
  echo "DIGEST MISMATCH for $TARBALL" >&2
  echo "  expected $SHA256" >&2
  echo "  got      $got" >&2
  echo "Refusing to extract. If the upstream tarball legitimately changed, re-pin" >&2
  echo "BOTH the revision in registry.json and SHA256 in this script, in one commit." >&2
  rm -f "$TARBALL"
  exit 1
fi

tar xzf "$TARBALL" -C "$DEST"
rm -f "$TARBALL"
echo "extracted: $DEST/arlington-pdf-model-$REV"
echo "next: python3 scripts/build-arlington-inventory.py"
