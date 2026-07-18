#!/bin/bash
set -e

# Target directories (Both Main App and Tests)
# We need to target the runtime-specific native folder where .NET looks
APP_DIR="Excise.App/bin/Debug/net8.0/runtimes/linux-x64/native"
TEST_DIR="Excise.App.Tests/bin/Debug/net8.0/runtimes/linux-x64/native"

echo "Fixing OCR library links..."

# Find system libraries (preferring x86_64)
LEPT_LIB=$(find /usr/lib/x86_64-linux-gnu -name "libleptonica.so.6*" | head -n 1)
TESS_LIB=$(find /usr/lib/x86_64-linux-gnu -name "libtesseract.so.5*" | head -n 1)
DL_LIB=$(find /usr/lib/x86_64-linux-gnu -name "libdl.so.2*" | head -n 1)

if [ -z "$LEPT_LIB" ]; then
    echo "Error: Could not find system libleptonica"
    exit 1
fi

if [ -z "$TESS_LIB" ]; then
    echo "Error: Could not find system libtesseract"
    exit 1
fi

if [ -z "$DL_LIB" ]; then
    echo "Error: Could not find system libdl"
    exit 1
fi

echo "Found system libleptonica at $LEPT_LIB"
echo "Found system libtesseract at $TESS_LIB"
echo "Found system libdl at $DL_LIB"

# Create symlinks in the specific runtime native folders
for target_dir in "$APP_DIR" "$TEST_DIR"; do
    # Ensure directories exist
    mkdir -p "$target_dir"
    
    echo "Linking in $target_dir..."
    ln -sf "$LEPT_LIB" "$target_dir/libleptonica-1.82.0.so"
    ln -sf "$TESS_LIB" "$target_dir/libtesseract50.so"
    ln -sf "$DL_LIB" "$target_dir/libdl.so"
done

echo "Symlinks created successfully."
