#!/bin/bash
#
# Download and install Excise.App from GitHub releases
# No .NET SDK required - downloads pre-built binary
#

set -e

APP_NAME="Excise.App"
APP_ID="cl.skpt.excise"
REPO_OWNER="marctjones"
REPO_NAME="excise"
INSTALL_DIR="$HOME/.local/share/Excise.App"
DESKTOP_FILE="$HOME/.local/share/applications/${APP_ID}.desktop"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"
VERSION="${1:-latest}"

echo "=== Excise.App Installation from GitHub ==="
echo ""

# Detect architecture
ARCH=$(uname -m)
case "$ARCH" in
    x86_64)
        PLATFORM="linux-x64"
        ;;
    aarch64|arm64)
        PLATFORM="linux-arm64"
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        exit 1
        ;;
esac

echo "Platform: $PLATFORM"
echo ""

# Get release info
echo "Step 1: Finding release..."

if [ "$VERSION" = "latest" ]; then
    API_URL="https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest"
else
    API_URL="https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/tags/$VERSION"
fi

RELEASE_JSON=$(curl -s "$API_URL")

TAG_NAME=$(echo "$RELEASE_JSON" | grep -o '"tag_name": "[^"]*"' | head -1 | cut -d'"' -f4)

if [ -z "$TAG_NAME" ]; then
    echo "Error: Could not find release. Check https://github.com/$REPO_OWNER/$REPO_NAME/releases"
    exit 1
fi

echo "  Found release: $TAG_NAME"

# Find asset URL
ASSET_URL=$(echo "$RELEASE_JSON" | grep -o "\"browser_download_url\": \"[^\"]*${PLATFORM}[^\"]*\.tar\.gz\"" | head -1 | cut -d'"' -f4)

if [ -z "$ASSET_URL" ]; then
    echo "Error: No $PLATFORM release found for $TAG_NAME"
    echo "Available assets:"
    echo "$RELEASE_JSON" | grep -o '"name": "[^"]*"' | cut -d'"' -f4 | sed 's/^/  - /'
    exit 1
fi

ASSET_NAME=$(basename "$ASSET_URL")
echo "  Asset: $ASSET_NAME"

# Download
echo ""
echo "Step 2: Downloading..."

TEMP_DIR=$(mktemp -d)
TARBALL="$TEMP_DIR/$ASSET_NAME"

curl -L -o "$TARBALL" "$ASSET_URL" --progress-bar

echo "  Downloaded to: $TARBALL"

# Extract
echo ""
echo "Step 3: Extracting..."

# Remove old installation
if [ -d "$INSTALL_DIR" ]; then
    echo "  Removing previous installation..."
    rm -rf "$INSTALL_DIR"
fi

mkdir -p "$INSTALL_DIR"
tar -xzf "$TARBALL" -C "$TEMP_DIR"

# Find and move extracted contents
EXTRACTED_DIR=$(find "$TEMP_DIR" -maxdepth 1 -type d -name "Excise.App-*" | head -1)
if [ -n "$EXTRACTED_DIR" ]; then
    mv "$EXTRACTED_DIR"/* "$INSTALL_DIR/"
else
    # Files might be directly extracted
    mv "$TEMP_DIR"/Excise.App "$INSTALL_DIR/" 2>/dev/null || true
fi

# Make executable
chmod +x "$INSTALL_DIR/Excise.App"

echo "  Installed to: $INSTALL_DIR"

# Create icon
echo ""
echo "Step 4: Creating icon..."

mkdir -p "$ICON_DIR"
ICON_PATH="$ICON_DIR/${APP_ID}.svg"

cat > "$ICON_PATH" << 'ICONEOF'
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg">
  <rect x="20" y="10" width="180" height="236" rx="8" fill="#ffffff" stroke="#333333" stroke-width="4"/>
  <path d="M160 10 L200 50 L160 50 Z" fill="#e0e0e0" stroke="#333333" stroke-width="2"/>
  <rect x="40" y="70" width="140" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="90" width="120" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="110" width="130" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="140" width="100" height="20" rx="2" fill="#000000"/>
  <rect x="40" y="175" width="140" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="195" width="110" height="8" rx="2" fill="#666666"/>
  <rect x="130" y="210" width="50" height="25" rx="4" fill="#dc3545"/>
  <text x="155" y="228" font-family="Arial, sans-serif" font-size="14" font-weight="bold" fill="white" text-anchor="middle">PDF</text>
  <circle cx="220" cy="220" r="30" fill="#dc3545"/>
  <rect x="205" y="215" width="30" height="10" rx="2" fill="white"/>
</svg>
ICONEOF

echo "  Created: $ICON_PATH"

# Create desktop entry
echo ""
echo "Step 5: Creating desktop entry..."

mkdir -p "$(dirname "$DESKTOP_FILE")"

cat > "$DESKTOP_FILE" << EOF
[Desktop Entry]
Version=1.0
Type=Application
Name=PDF Editor
GenericName=PDF Editor
Comment=PDF viewer and editor with TRUE redaction capabilities
Exec=$INSTALL_DIR/Excise.App %f
Icon=$ICON_PATH
Terminal=false
Categories=Office;Viewer;Graphics;
MimeType=application/pdf;
Keywords=pdf;redact;redaction;edit;view;
StartupNotify=true
StartupWMClass=Excise.App
EOF

echo "  Created: $DESKTOP_FILE"

# Update desktop database
echo ""
echo "Step 6: Updating desktop database..."
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

# Cleanup
echo ""
echo "Step 7: Cleaning up..."
rm -rf "$TEMP_DIR"
echo "  Removed temporary files"

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Version: $TAG_NAME"
echo "Location: $INSTALL_DIR"
echo ""
echo "You can now:"
echo "  1. Find 'PDF Editor' in your application menu"
echo "  2. Right-click a PDF and 'Open With' PDF Editor"
echo "  3. Run from terminal: $INSTALL_DIR/Excise.App"
echo ""
echo "To uninstall:"
echo "  rm -rf $INSTALL_DIR"
echo "  rm -f $DESKTOP_FILE"
echo "  rm -f $ICON_PATH"
