#!/bin/bash
# Fix remaining RedactArea calls that have only 2 parameters (page, area)
# Pattern: .RedactArea(page, area) -> .RedactArea(page, area, pdfPath)
# Careful: Don't touch calls that already have 3+ parameters

set -e

cd /home/marc/excise/Excise.App.Tests

echo "Fixing remaining Red

actArea calls..."

# Pattern: Match .RedactArea(arg, arg) followed by );
# Use perl for more precise matching
find . -name "*.cs" -type f | while read file; do
    # Skip if no RedactArea calls
    if ! grep -q "RedactArea" "$file"; then
        continue
    fi

    echo "Checking: $file"

    # Use perl for better regex support
    # Match: service.RedactArea(XXX, YYY); where XXX and YYY don't contain commas
    # Replace with: service.RedactArea(XXX, YYY, pdfPath);
    perl -i -pe 's/(service\.RedactArea\([^,]+,\s*[^,)]+)\);/$1, pdfPath);/g' "$file"
done

echo "Testing compilation..."
dotnet build > /dev/null 2>&1

if [ $? -eq 0 ]; then
    echo "✓ Build successful!"
    find . -name "*.bak" -delete
    exit 0
else
    echo "⚠ Still have errors"
    dotnet build 2>&1 | grep "error CS" | head -10
    exit 1
fi
