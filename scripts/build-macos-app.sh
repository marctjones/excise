#!/usr/bin/env bash
# Build a macOS .app bundle for pdfe (the PdfEditor GUI), self-contained.
# Produces dist/pdfe-<version>-macos-<arch>.zip (+ .sha256).
#
# Must run on macOS (uses sips / iconutil / ditto). The .app is UNSIGNED —
# Gatekeeper will quarantine it; users open via right-click → Open or
# `xattr -dr com.apple.quarantine pdfe.app`. Signing/notarization needs an
# Apple Developer cert (not configured).
#
# Usage:
#   scripts/build-macos-app.sh --version 2.4.1 [--rid osx-arm64] [--output dist]

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

VERSION="0.0.0"
RID="osx-arm64"
OUT="$ROOT/dist"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --version) VERSION="$2"; shift 2 ;;
        --rid)     RID="$2"; shift 2 ;;
        --output)  OUT="$2"; shift 2 ;;
        --help|-h) sed -n '2,12p' "$0"; exit 0 ;;
        *) echo "Unknown arg: $1" >&2; exit 2 ;;
    esac
done

ARCH="${RID#osx-}"            # arm64 | x64
APP_NAME="pdfe"
BUNDLE="$OUT/${APP_NAME}.app"
PUBLISH_DIR="$ROOT/artifacts/macos-publish"

rm -rf "$BUNDLE" "$PUBLISH_DIR"
mkdir -p "$OUT"

echo "▶ Publishing PdfEditor ($RID, self-contained)"
dotnet publish "$ROOT/PdfEditor/PdfEditor.csproj" \
    -c Release -r "$RID" --self-contained true \
    -p:PublishSingleFile=false \
    -o "$PUBLISH_DIR"

echo "▶ Assembling $BUNDLE"
mkdir -p "$BUNDLE/Contents/MacOS" "$BUNDLE/Contents/Resources"
cp -R "$PUBLISH_DIR/." "$BUNDLE/Contents/MacOS/"
chmod +x "$BUNDLE/Contents/MacOS/PdfEditor" || true

# ── Icon (best-effort): SVG -> 1024 PNG -> .iconset -> .icns ────────────────
ICON_SVG="$ROOT/PdfEditor/Assets/pdfe_logo.svg"
ICON_PLIST=""
MAGICK="$(command -v magick || command -v convert || true)"
if [[ -n "$MAGICK" && -f "$ICON_SVG" ]]; then
    ICON_SET="$PUBLISH_DIR/pdfe.iconset"
    mkdir -p "$ICON_SET"
    if "$MAGICK" -background none -density 512 "$ICON_SVG" -resize 1024x1024 "$PUBLISH_DIR/icon_1024.png"; then
        sips -z 16 16     "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_16x16.png"      >/dev/null 2>&1 || true
        sips -z 32 32     "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_16x16@2x.png"   >/dev/null 2>&1 || true
        sips -z 32 32     "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_32x32.png"      >/dev/null 2>&1 || true
        sips -z 64 64     "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_32x32@2x.png"   >/dev/null 2>&1 || true
        sips -z 128 128   "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_128x128.png"    >/dev/null 2>&1 || true
        sips -z 256 256   "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_128x128@2x.png" >/dev/null 2>&1 || true
        sips -z 256 256   "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_256x256.png"    >/dev/null 2>&1 || true
        sips -z 512 512   "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_256x256@2x.png" >/dev/null 2>&1 || true
        sips -z 512 512   "$PUBLISH_DIR/icon_1024.png" --out "$ICON_SET/icon_512x512.png"    >/dev/null 2>&1 || true
        cp "$PUBLISH_DIR/icon_1024.png" "$ICON_SET/icon_512x512@2x.png" 2>/dev/null || true
        if iconutil -c icns "$ICON_SET" -o "$BUNDLE/Contents/Resources/pdfe.icns"; then
            ICON_PLIST=$'    <key>CFBundleIconFile</key>\n    <string>pdfe</string>'
        fi
    fi
else
    echo "::warning::ImageMagick not found (or icon missing); building .app without a custom icon"
fi

echo "▶ Writing Info.plist"
cat > "$BUNDLE/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>pdfe</string>
    <key>CFBundleDisplayName</key>
    <string>pdfe</string>
    <key>CFBundleIdentifier</key>
    <string>com.marcjones.pdfe</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleExecutable</key>
    <string>PdfEditor</string>
${ICON_PLIST}
    <key>LSMinimumSystemVersion</key>
    <string>11.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>LSApplicationCategoryType</key>
    <string>public.app-category.productivity</string>
</dict>
</plist>
PLIST

ZIP="${APP_NAME}-${VERSION}-macos-${ARCH}.zip"
echo "▶ Zipping $OUT/$ZIP"
rm -f "$OUT/$ZIP"
# ditto preserves bundle structure + resource forks; keepParent zips pdfe.app/.
( cd "$OUT" && /usr/bin/ditto -c -k --sequesterRsrc --keepParent "${APP_NAME}.app" "$ZIP" )
( cd "$OUT" && shasum -a 256 "$ZIP" > "$ZIP.sha256" )

echo "✔ Built $OUT/$ZIP"
