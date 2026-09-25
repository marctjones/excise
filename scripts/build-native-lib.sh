#!/usr/bin/env bash
#
# Publish the Excise.Native NativeAOT shared library (C ABI over Excise.Core) into
# dist/native/<rid>/ together with include/excise.h. See docs/native-api.md.
#
# Usage: scripts/build-native-lib.sh [--rid RID] [--config Release]
#   RID defaults to the host (osx-arm64, osx-x64, linux-x64, linux-arm64, win-x64).
#   NativeAOT cannot cross-compile: build each RID on its own OS.
#
# Prints the path of the produced library on the last line.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

RID=""
CONFIG="Release"
while [ $# -gt 0 ]; do
  case "$1" in
    --rid) RID="${2:?--rid needs a value}"; shift 2 ;;
    --config) CONFIG="${2:?--config needs a value}"; shift 2 ;;
    -h|--help) sed -n 2,11p "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

if [ -z "$RID" ]; then
  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64) RID=osx-arm64 ;;
    Darwin-x86_64) RID=osx-x64 ;;
    Linux-x86_64) RID=linux-x64 ;;
    Linux-aarch64|Linux-arm64) RID=linux-arm64 ;;
    MINGW*|MSYS*|CYGWIN*) RID=win-x64 ;;
    *) echo "cannot infer a RID for $(uname -s)-$(uname -m); pass --rid" >&2; exit 2 ;;
  esac
fi

case "$RID" in
  osx-*) LIB=libexcise_native.dylib ;;
  win-*) LIB=excise_native.dll ;;
  *) LIB=libexcise_native.so ;;
esac

# The official Microsoft SDK only: Homebrew's runtime pack poisons NativeAOT output (CLAUDE.md).
BASE="$(dotnet --info | sed -n 's/^ *Base Path: *//p' | head -1)"
case "$BASE" in
  "$HOME"/.dotnet/*|C:\\Program\ Files\\dotnet\\*|/usr/share/dotnet/*|/usr/lib/dotnet/*|/usr/local/share/dotnet/*) ;;
  *) echo "FAIL: dotnet Base Path '$BASE' is not the official SDK location" >&2; exit 1 ;;
esac

OUT="dist/native/$RID"
STAGE="$(mktemp -d "${TMPDIR:-/tmp}/excise-native-publish.XXXXXX")"
trap 'rm -rf "$STAGE"' EXIT

dotnet publish Excise.Native/Excise.Native.csproj -c "$CONFIG" -r "$RID" -o "$STAGE" \
  -p:UseSharedCompilation=false --nologo -v minimal

[ -f "$STAGE/$LIB" ] || { echo "FAIL: publish did not produce $LIB (got: $(ls "$STAGE"))" >&2; exit 1; }

rm -rf "$OUT"
mkdir -p "$OUT/include"
cp "$STAGE/$LIB" "$OUT/$LIB"
cp Excise.Native/include/excise.h "$OUT/include/excise.h"

echo "size: $(wc -c < "$OUT/$LIB" | tr -d ' ') bytes"
echo "$OUT/$LIB"
