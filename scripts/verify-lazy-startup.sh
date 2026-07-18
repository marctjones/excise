#!/usr/bin/env bash
# Verify the default Release package keeps heavy optional subsystems out of the
# normal startup path (#341): Roslyn scripting is not shipped unless explicitly
# enabled, repo-local tessdata is not bundled unless requested, and the GUI
# hidden-text toggle does not load Excise.Ocr before the user asks for raster OCR.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISH_DIR="${1:-/tmp/excise-release-lazy-startup}"

cd "$ROOT"
rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

echo "==> Publishing default Release GUI to $PUBLISH_DIR"
dotnet publish Excise.App/Excise.App.csproj \
    -c Release \
    -o "$PUBLISH_DIR" \
    -p:DebugType=None \
    -p:DebugSymbols=false

echo "==> Checking Roslyn scripting is absent from default Release output"
if find "$PUBLISH_DIR" -name 'Microsoft.CodeAnalysis*.dll' -print -quit | grep -q .; then
    echo "ERROR: Microsoft.CodeAnalysis assemblies were published unexpectedly" >&2
    find "$PUBLISH_DIR" -name 'Microsoft.CodeAnalysis*.dll' >&2
    exit 1
fi

deps="$PUBLISH_DIR/Excise.App.deps.json"
if [[ -f "$deps" ]] && grep -q 'Microsoft.CodeAnalysis.CSharp.Scripting' "$deps"; then
    echo "ERROR: Excise.App.deps.json contains Microsoft.CodeAnalysis.CSharp.Scripting" >&2
    exit 1
fi

echo "==> Checking tessdata is absent from default Release output"
if find "$PUBLISH_DIR" \( -path '*/tessdata/*' -o -name '*.traineddata' \) -print -quit | grep -q .; then
    echo "ERROR: tessdata/traineddata files were published unexpectedly" >&2
    find "$PUBLISH_DIR" \( -path '*/tessdata/*' -o -name '*.traineddata' \) >&2
    exit 1
fi

echo "==> Checking normal hidden-text toggles do not load Excise.Ocr"
dotnet test Excise.App.Tests/Excise.App.Tests.csproj \
    --filter "FullyQualifiedName~HiddenTextToggles_DoNotLoadOcrAssemblyBeforeRasterizedScan" \
    --logger "console;verbosity=minimal"

echo "OK: lazy-startup verification passed"
