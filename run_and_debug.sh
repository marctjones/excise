#!/bin/bash

# Clear previous log
rm -f /tmp/crash_log.txt

echo "========================================="
echo " Excise.App Debug Session"
echo "========================================="
echo ""
echo "Building the latest version..."
echo ""

cd /home/marc/excise/Excise.App

# Build the application to ensure we have the latest code
if ! dotnet build --no-restore 2>&1 | tee -a /tmp/crash_log.txt; then
    echo ""
    echo "========================================="
    echo " BUILD FAILED"
    echo "========================================="
    echo ""
    echo "Build errors saved to: /tmp/crash_log.txt"
    exit 1
fi

echo ""
echo "Build successful!"
echo ""
echo "Starting the app with full logging..."
echo "All output will be saved to: /tmp/crash_log.txt"
echo ""
echo "Instructions:"
echo "1. The app window will open"
echo "2. Click 'Open' and select a PDF file"
echo "3. If it crashes, come back to this terminal"
echo "4. Press Ctrl+C to stop if needed"
echo ""
echo "========================================="
echo "Output:"
echo "========================================="
echo ""

# Run the app and capture ALL output (stdout + stderr)
dotnet run --no-build 2>&1 | tee -a /tmp/crash_log.txt

echo ""
echo "========================================="
echo " App Stopped"
echo "========================================="
echo ""
echo "Last 40 lines of log:"
echo ""
tail -40 /tmp/crash_log.txt
echo ""
echo "Full log saved to: /tmp/crash_log.txt"
echo ""
