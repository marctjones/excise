#!/bin/bash
#
# Install Excise.App as a desktop application for the current user
#

set -e

APP_NAME="Excise.App"
APP_ID="cl.skpt.excise"
INSTALL_DIR="$HOME/.local/share/Excise.App"
DESKTOP_FILE="$HOME/.local/share/applications/${APP_ID}.desktop"
ICON_DIR="$HOME/.local/share/icons/hicolor/256x256/apps"

echo "=== Excise.App Desktop Installation ==="
echo ""

# Get the script directory (where the source code is)
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_DIR="$SCRIPT_DIR/Excise.App"

if [ ! -f "$PROJECT_DIR/Excise.App.csproj" ]; then
    echo "Error: Cannot find Excise.App.csproj in $PROJECT_DIR"
    exit 1
fi

echo "Step 1: Building release version..."
cd "$PROJECT_DIR"
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o "$INSTALL_DIR"

echo ""
echo "Step 2: Creating icon directory..."
mkdir -p "$ICON_DIR"
mkdir -p "$(dirname "$DESKTOP_FILE")"

# Create a simple SVG icon if none exists
ICON_PATH="$ICON_DIR/${APP_ID}.svg"
if [ ! -f "$ICON_PATH" ]; then
    echo "Step 3: Creating application icon..."
    cat > "$ICON_PATH" << 'ICONEOF'
<?xml version="1.0" encoding="UTF-8"?>
<svg width="256" height="256" viewBox="0 0 256 256" xmlns="http://www.w3.org/2000/svg">
  <!-- Background -->
  <rect x="20" y="10" width="180" height="236" rx="8" fill="#ffffff" stroke="#333333" stroke-width="4"/>

  <!-- Page fold -->
  <path d="M160 10 L200 50 L160 50 Z" fill="#e0e0e0" stroke="#333333" stroke-width="2"/>

  <!-- Text lines -->
  <rect x="40" y="70" width="140" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="90" width="120" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="110" width="130" height="8" rx="2" fill="#666666"/>

  <!-- Redaction box (black) -->
  <rect x="40" y="140" width="100" height="20" rx="2" fill="#000000"/>

  <!-- More text lines -->
  <rect x="40" y="175" width="140" height="8" rx="2" fill="#666666"/>
  <rect x="40" y="195" width="110" height="8" rx="2" fill="#666666"/>

  <!-- PDF label -->
  <rect x="130" y="210" width="50" height="25" rx="4" fill="#dc3545"/>
  <text x="155" y="228" font-family="Arial, sans-serif" font-size="14" font-weight="bold" fill="white" text-anchor="middle">PDF</text>

  <!-- Redaction indicator -->
  <circle cx="220" cy="220" r="30" fill="#dc3545"/>
  <rect x="205" y="215" width="30" height="10" rx="2" fill="white"/>
</svg>
ICONEOF
fi

echo "Step 4: Creating desktop entry..."
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

echo "Step 5: Updating desktop database..."
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

echo ""
echo "=== Installation Complete ==="
echo ""
echo "Location: $INSTALL_DIR/Excise.App"
echo "Desktop entry: $DESKTOP_FILE"
echo "Icon: $ICON_PATH"
echo ""
echo "You can now:"
echo "  1. Find 'PDF Editor' in your application menu"
echo "  2. Right-click a PDF and 'Open With' PDF Editor"
echo "  3. Run from terminal: $INSTALL_DIR/Excise.App"
echo ""
echo "To uninstall, run: ./uninstall-desktop.sh"
