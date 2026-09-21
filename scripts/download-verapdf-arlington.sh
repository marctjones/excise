#!/usr/bin/env bash
# Fetch veraPDF's Arlington PDF Model checker as a CONTAINER IMAGE (#1709).
#
# WHY A CONTAINER AND NOT A COPY. veraPDF is GPL-3/MPL-2, Java, and updated
# weekly; excise must never link it or vendor it (tests/license-policy.tsv).
# Running the upstream image keeps it in its own environment, at the upstream's
# own build, invoked over HTTP -- the same boundary as VeraPdfReferenceValidator
# (subprocess only), and nothing of it enters git. This script is the only
# thing tracked.
#
# WHAT IT IS. The Arlington PDF Model (ISO 32000-2:2020 object model) turned
# into a veraPDF validation profile by veraPDF-arlington-tools. It judges a FILE
# against the model: required keys, types, allowed values, direct/indirect,
# version rules, ~40 predicates. It does not render, and it does not test a
# library -- it is the oracle for "is this file's structure legal".
#
# PINNED BY DIGEST, not by tag: a tag is a name someone can move. Re-pin the
# tag and the digest together, in one commit, after reading the upstream notes.
#
# ENGINE. podman or docker, whichever is present (CONTAINER_ENGINE overrides).
# Note the image is linux/amd64 only; on Apple Silicon it runs emulated, so
# expect it slower than native.
#
#   scripts/download-verapdf-arlington.sh          pull + verify + record the pin
#   scripts/run-arlington-check.sh up|down|check   then use it
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

IMAGE_REPO=docker.io/verapdf/arlington
IMAGE_TAG=v1.30.2
IMAGE_DIGEST=sha256:1543368902c393771557e2a1da69e64ad72b69db32e6efebd38a09a7b86eacbc
DEST=tools/vendor/verapdf-arlington
MARKER="$DEST/PINNED"

ENGINE="${CONTAINER_ENGINE:-}"
if [ -z "$ENGINE" ]; then
  for e in podman docker; do command -v "$e" >/dev/null 2>&1 && { ENGINE="$e"; break; }; done
fi
[ -n "$ENGINE" ] || { echo "need podman or docker on PATH (both free); neither found" >&2; exit 77; }

if ! "$ENGINE" info >/dev/null 2>&1; then
  echo "$ENGINE is installed but not reachable." >&2
  [ "$ENGINE" = podman ] && echo "  try: podman machine start" >&2
  exit 77
fi

REF="$IMAGE_REPO@$IMAGE_DIGEST"
if "$ENGINE" image exists "$REF" 2>/dev/null || "$ENGINE" image inspect "$REF" >/dev/null 2>&1; then
  echo "already present: $REF"
else
  echo "pulling veraPDF Arlington $IMAGE_TAG by digest (~80 MB, linux/amd64)"
  "$ENGINE" pull "$REF"
fi

# A digest pull is verified by the engine; this re-reads what we ended up with
# rather than trusting that the pull command did what its name says.
got="$("$ENGINE" image inspect "$REF" --format '{{.Digest}}' 2>/dev/null || true)"
case "$got" in
  "$IMAGE_DIGEST") ;;
  "") echo "note: $ENGINE reported no digest for the image; relying on the by-digest pull itself" >&2 ;;
  *) echo "DIGEST MISMATCH: expected $IMAGE_DIGEST, engine reports $got" >&2; exit 1 ;;
esac

mkdir -p "$DEST"
cat > "$MARKER" <<EOF
engine=$ENGINE
image=$REF
tag=$IMAGE_TAG
fetched=$(date -u +%Y-%m-%dT%H:%M:%SZ)
EOF
echo "recorded: $MARKER"
echo "next: scripts/run-arlington-check.sh check <file.pdf | dir>"
