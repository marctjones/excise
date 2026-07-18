#!/bin/bash
# Script to fix RedactArea() calls to include pdfFilePath parameter
# This script updates all GUI tests to pass the file path to RedactArea()

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJECT_ROOT="$(dirname "$SCRIPT_DIR")"
TEST_DIR="$PROJECT_ROOT/Excise.App.Tests"
LOG_DIR="$PROJECT_ROOT/logs"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
LOG_FILE="$LOG_DIR/fix_redactarea_${TIMESTAMP}.log"
SUCCESS_LOG="$LOG_DIR/fix_redactarea_success_${TIMESTAMP}.log"
ERROR_LOG="$LOG_DIR/fix_redactarea_errors_${TIMESTAMP}.log"

# Create logs directory
mkdir -p "$LOG_DIR"

echo "========================================" | tee "$LOG_FILE"
echo "RedactArea() Call Fixer" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "Started: $(date)" | tee -a "$LOG_FILE"
echo "Test Directory: $TEST_DIR" | tee -a "$LOG_FILE"
echo "Log File: $LOG_FILE" | tee -a "$LOG_FILE"
echo "" | tee -a "$LOG_FILE"

# Counter
TOTAL_FILES=0
TOTAL_FIXES=0
TOTAL_ERRORS=0

# Find all C# files that contain RedactArea calls
FILES=$(find "$TEST_DIR" -name "*.cs" -type f -exec grep -l "\.RedactArea(" {} \;)

if [ -z "$FILES" ]; then
    echo "No files found with RedactArea() calls" | tee -a "$LOG_FILE"
    exit 0
fi

echo "Found files with RedactArea() calls:" | tee -a "$LOG_FILE"
echo "$FILES" | tee -a "$LOG_FILE"
echo "" | tee -a "$LOG_FILE"

# Process each file
for file in $FILES; do
    TOTAL_FILES=$((TOTAL_FILES + 1))
    RELATIVE_PATH="${file#$PROJECT_ROOT/}"

    echo "========================================" | tee -a "$LOG_FILE"
    echo "Processing: $RELATIVE_PATH" | tee -a "$LOG_FILE"
    echo "========================================" | tee -a "$LOG_FILE"

    # Create backup
    cp "$file" "$file.bak"

    # Count RedactArea calls before
    BEFORE_COUNT=$(grep -c "\.RedactArea(" "$file" || true)
    echo "  RedactArea() calls found: $BEFORE_COUNT" | tee -a "$LOG_FILE"

    # Pattern 1: RedactArea(page, area, renderDpi: N)
    # Replace with: RedactArea(page, area, pdfPath, renderDpi: N)
    sed -i 's/\.RedactArea(\([^,]*\),\s*\([^,]*\),\s*renderDpi:\s*\([0-9]*\))/\.RedactArea(\1, \2, pdfPath, renderDpi: \3)/g' "$file"

    # Pattern 2: redactionService.RedactArea(page, area, renderDpi: N)
    # Same replacement but might have different variable names
    # This pattern handles cases where pdfFilePath might need to be a different variable

    # Check if any fixes were applied
    AFTER_COUNT=$(grep -c "pdfPath, renderDpi:" "$file" || true)
    FIXED_COUNT=$((AFTER_COUNT - $(grep -c "pdfPath, renderDpi:" "$file.bak" || true)))

    if [ "$FIXED_COUNT" -gt 0 ]; then
        echo "  ✓ Fixed $FIXED_COUNT calls" | tee -a "$LOG_FILE" | tee -a "$SUCCESS_LOG"
        TOTAL_FIXES=$((TOTAL_FIXES + FIXED_COUNT))

        # Verify the file still compiles patterns correctly
        if grep -q "RedactArea([^)]*pdfPath.*renderDpi" "$file"; then
            echo "  ✓ Verification: pdfPath parameter added correctly" | tee -a "$LOG_FILE"
        else
            echo "  ⚠ Warning: Pattern might not be correct" | tee -a "$LOG_FILE" | tee -a "$ERROR_LOG"
        fi
    else
        echo "  ℹ No changes needed (already fixed or no matching pattern)" | tee -a "$LOG_FILE"
    fi

    # Check for any remaining old-style calls
    if grep -q "\.RedactArea([^)]*renderDpi:\s*[0-9]*)" "$file" | grep -v "pdfPath"; then
        REMAINING=$(grep -c "\.RedactArea([^)]*renderDpi:\s*[0-9]*)" "$file" | grep -v "pdfPath" || true)
        if [ "$REMAINING" -gt 0 ]; then
            echo "  ⚠ Warning: $REMAINING calls might still need manual fixing" | tee -a "$LOG_FILE" | tee -a "$ERROR_LOG"
            echo "    File: $RELATIVE_PATH" >> "$ERROR_LOG"
            TOTAL_ERRORS=$((TOTAL_ERRORS + REMAINING))
        fi
    fi

    echo "" | tee -a "$LOG_FILE"
done

# Now handle special cases that might need different variable names
echo "========================================" | tee -a "$LOG_FILE"
echo "Checking for special cases..." | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"

# Some files might use inputPath, filePath, testPath, etc. instead of pdfPath
# Let's detect and fix those
for file in $FILES; do
    RELATIVE_PATH="${file#$PROJECT_ROOT/}"

    # Check if file has RedactArea calls without pdfPath parameter
    if grep -q "\.RedactArea(" "$file"; then
        # Look for common variable patterns in the same file
        if grep -q "var inputPath\|string inputPath" "$file"; then
            sed -i 's/\.RedactArea(\([^,]*\),\s*\([^,]*\),\s*renderDpi:\s*\([0-9]*\))/\.RedactArea(\1, \2, inputPath, renderDpi: \3)/g' "$file"
            echo "  ✓ Fixed calls in $RELATIVE_PATH using 'inputPath'" | tee -a "$LOG_FILE" | tee -a "$SUCCESS_LOG"
        elif grep -q "var filePath\|string filePath" "$file" && ! grep -q "var pdfPath\|string pdfPath" "$file"; then
            sed -i 's/\.RedactArea(\([^,]*\),\s*\([^,]*\),\s*renderDpi:\s*\([0-9]*\))/\.RedactArea(\1, \2, filePath, renderDpi: \3)/g' "$file"
            echo "  ✓ Fixed calls in $RELATIVE_PATH using 'filePath'" | tee -a "$LOG_FILE" | tee -a "$SUCCESS_LOG"
        elif grep -q "var testPath\|string testPath" "$file"; then
            sed -i 's/\.RedactArea(\([^,]*\),\s*\([^,]*\),\s*renderDpi:\s*\([0-9]*\))/\.RedactArea(\1, \2, testPath, renderDpi: \3)/g' "$file"
            echo "  ✓ Fixed calls in $RELATIVE_PATH using 'testPath'" | tee -a "$LOG_FILE" | tee -a "$SUCCESS_LOG"
        fi
    fi
done

# Restore files if they have compilation issues
echo "" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "Testing compilation..." | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"

cd "$TEST_DIR"
if dotnet build > /dev/null 2>&1; then
    echo "✓ Compilation successful!" | tee -a "$LOG_FILE" | tee -a "$SUCCESS_LOG"
    # Remove backups
    find "$TEST_DIR" -name "*.cs.bak" -delete
else
    echo "⚠ Compilation failed. Check errors below:" | tee -a "$LOG_FILE" | tee -a "$ERROR_LOG"
    dotnet build 2>&1 | grep "error CS" | tee -a "$ERROR_LOG"
    echo "" | tee -a "$LOG_FILE"
    echo "Backups are preserved as *.cs.bak files" | tee -a "$LOG_FILE"
    echo "You can restore them with: find $TEST_DIR -name '*.cs.bak' -exec bash -c 'mv \"\$1\" \"\${1%.bak}\"' _ {} \;" | tee -a "$LOG_FILE"
fi

# Summary
echo "" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "SUMMARY" | tee -a "$LOG_FILE"
echo "========================================" | tee -a "$LOG_FILE"
echo "Files processed: $TOTAL_FILES" | tee -a "$LOG_FILE"
echo "Calls fixed: $TOTAL_FIXES" | tee -a "$LOG_FILE"
echo "Potential issues: $TOTAL_ERRORS" | tee -a "$LOG_FILE"
echo "" | tee -a "$LOG_FILE"
echo "Completed: $(date)" | tee -a "$LOG_FILE"
echo "" | tee -a "$LOG_FILE"
echo "Detailed logs:" | tee -a "$LOG_FILE"
echo "  All: $LOG_FILE" | tee -a "$LOG_FILE"
echo "  Success: $SUCCESS_LOG" | tee -a "$LOG_FILE"
echo "  Errors: $ERROR_LOG" | tee -a "$LOG_FILE"

# Return exit code based on compilation
if dotnet build > /dev/null 2>&1; then
    exit 0
else
    exit 1
fi
