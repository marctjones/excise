#!/bin/bash
#
# Uninstall Excise.App desktop application
#

APP_ID="com.exciseditor.Excise.App"
INSTALL_DIR="$HOME/.local/share/Excise.App"
DESKTOP_FILE="$HOME/.local/share/applications/${APP_ID}.desktop"
ICON_PATH="$HOME/.local/share/icons/hicolor/256x256/apps/${APP_ID}.svg"

echo "=== Excise.App Desktop Uninstallation ==="
echo ""

if [ -d "$INSTALL_DIR" ]; then
    echo "Removing application files..."
    rm -rf "$INSTALL_DIR"
    echo "  Removed: $INSTALL_DIR"
fi

if [ -f "$DESKTOP_FILE" ]; then
    echo "Removing desktop entry..."
    rm -f "$DESKTOP_FILE"
    echo "  Removed: $DESKTOP_FILE"
fi

if [ -f "$ICON_PATH" ]; then
    echo "Removing icon..."
    rm -f "$ICON_PATH"
    echo "  Removed: $ICON_PATH"
fi

echo "Updating desktop database..."
update-desktop-database "$HOME/.local/share/applications" 2>/dev/null || true

echo ""
echo "=== Uninstallation Complete ==="
