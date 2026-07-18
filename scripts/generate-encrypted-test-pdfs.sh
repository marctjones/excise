#!/usr/bin/env bash
# Regenerate the encrypted test PDFs used by the Excise.Core encryption tests.
# These files are deliberately gitignored — they're reproducible from the
# (committed) scrambled birth certificate source.
#
# Tests under Excise.Core.Tests/Parsing/ObjectStreamResolutionTests.cs early-
# return when the files are missing, so this script is only needed to
# exercise the RC4 decryption assertions locally.
#
# Requires: qpdf (sudo apt install qpdf  /  brew install qpdf).

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$REPO_ROOT/test-pdfs/sample-pdfs/birth-certificate-request-scrambled.pdf"
DEST_DIR="$REPO_ROOT/test-pdfs/encrypted"

if [ ! -f "$SRC" ]; then
  echo "Source PDF not found: $SRC" >&2
  exit 1
fi

mkdir -p "$DEST_DIR"

# RC4 V=2 R=3 (128-bit) — most common legacy encryption
qpdf --allow-weak-crypto --encrypt '' '' 128 -- "$SRC" "$DEST_DIR/birth-cert-rc4-128.pdf"

# RC4 V=1 R=2 (40-bit legacy) — earliest standard handler
qpdf --allow-weak-crypto --encrypt '' '' 40 -- "$SRC" "$DEST_DIR/birth-cert-rc4-40.pdf"

# AES-128 V=4 R=4 (CFM=AESV2) — modern but pre-PDF-2.0
qpdf --encrypt '' '' 128 --use-aes=y -- "$SRC" "$DEST_DIR/birth-cert-aes-128.pdf"

# AES-256 V=5 R=6 — PDF 2.0 native handler with SHA-256 KDF
qpdf --encrypt '' '' 256 -- "$SRC" "$DEST_DIR/birth-cert-aes-256.pdf"

echo "Regenerated:"
ls -la "$DEST_DIR"
