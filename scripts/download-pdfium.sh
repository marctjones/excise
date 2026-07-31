#!/usr/bin/env bash
#
# Fetch a prebuilt PDFium shared library so it can act as a reference oracle
# (#857).
#
# WHY NOT pdfium_test
# -------------------
# PdfiumReferenceRenderer shells out to `pdfium_test`, Chromium's sample
# renderer. That binary is not distributed anywhere: it is built from the
# pdfium source tree with depot_tools/gn/ninja, and no package manager ships
# it (`brew search pdfium` finds nothing). bblanchon/pdfium-binaries — the
# canonical prebuilt distribution — ships `lib/libpdfium.<ext>` and headers
# only, verified by listing the release tarball.
#
# So the shell-out renderer cannot be provisioned. PdfiumNativeReferenceRenderer
# calls the library directly instead, and this script fetches it.
#
# Why bother: PDFium is the Chrome/Foxit lineage, independent of MuPDF,
# Poppler, Ghostscript and PDFBox. It is also the most widely deployed PDF
# renderer in existence, so "excise disagrees with pdfium" is a statement about
# what most people will actually see.
#
# The library is ~10 MB and lands in tools/vendor/pdfium/ (gitignored).
#
# Usage:
#   ./scripts/download-pdfium.sh
#   ./scripts/download-pdfium.sh --export     # print the env line only
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1

VENDOR="$ROOT/tools/vendor/pdfium"
EXPORT_ONLY=0
[ "${1:-}" = "--export" ] && EXPORT_ONLY=1
say() { [ "$EXPORT_ONLY" = "1" ] || echo "$@"; }

case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  ASSET="pdfium-mac-arm64.tgz";  LIB="libpdfium.dylib" ;;
    Darwin-x86_64) ASSET="pdfium-mac-x64.tgz";    LIB="libpdfium.dylib" ;;
    Linux-x86_64)  ASSET="pdfium-linux-x64.tgz";  LIB="libpdfium.so" ;;
    Linux-aarch64) ASSET="pdfium-linux-arm64.tgz";LIB="libpdfium.so" ;;
    *) say "✗ unsupported platform $(uname -s)-$(uname -m)"; exit 1 ;;
esac

if [ -f "$VENDOR/lib/$LIB" ]; then
    say "✓ already present: $VENDOR/lib/$LIB ($(du -sh "$VENDOR/lib/$LIB" | cut -f1))"
else
    say "▶ resolving latest bblanchon/pdfium-binaries release"
    URL="$(curl -fsSL https://api.github.com/repos/bblanchon/pdfium-binaries/releases/latest 2>/dev/null \
        | python3 -c "
import json,sys
try: d=json.load(sys.stdin)
except Exception: sys.exit(1)
for a in d.get('assets',[]):
    if a['name']=='$ASSET': print(a['browser_download_url']); break
" 2>/dev/null)"

    if [ -z "$URL" ]; then
        say "✗ could not find asset $ASSET in the latest release"
        exit 1
    fi

    say "▶ fetching $ASSET"
    TMP="$(mktemp -d)"
    trap 'rm -rf "$TMP"' EXIT
    if ! curl -fsSL "$URL" -o "$TMP/pdfium.tgz"; then
        say "✗ download failed: $URL"
        exit 1
    fi
    mkdir -p "$VENDOR"
    tar -xzf "$TMP/pdfium.tgz" -C "$VENDOR"
    if [ ! -f "$VENDOR/lib/$LIB" ]; then
        say "✗ archive did not contain lib/$LIB"
        exit 1
    fi
    say "✓ $VENDOR/lib/$LIB ($(du -sh "$VENDOR/lib/$LIB" | cut -f1))"
    say "  version: $(cat "$VENDOR/VERSION" 2>/dev/null | tr '\n' ' ')"
fi

if [ "$EXPORT_ONLY" = "1" ]; then
    echo "export EXCISE_PDFIUM_LIB='$VENDOR/lib/$LIB'"
    exit 0
fi

echo ""
echo "PdfiumNativeReferenceRenderer discovers this automatically under"
echo "tools/vendor/pdfium/. To point at a different build:"
echo "    export EXCISE_PDFIUM_LIB='$VENDOR/lib/$LIB'"
echo ""
echo "Then re-run scripts/check-test-prereqs.sh to confirm."
