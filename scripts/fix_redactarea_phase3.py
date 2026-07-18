#!/usr/bin/env python3
"""
Phase 3: Fix remaining RedactArea calls with inline Rect constructors.
Handles: .RedactArea(page, new Rect(...), renderDpi: N)
"""

import re
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).parent.parent
TEST_DIR = PROJECT_ROOT / "Excise.App.Tests"

def find_path_var(content):
    """Find the file path variable."""
    patterns = [
        r'var\s+(testPdf|pdfPath|inputPath|filePath)\s*=',
        r'string\s+(testPdf|pdfPath|inputPath|filePath)\s*=',
        r'PdfReader\.Open\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,',
    ]

    for pattern in patterns:
        match = re.search(pattern, content)
        if match:
            var = match.group(1)
            if 'redacted' not in var.lower() and 'output' not in var.lower():
                return var

    return 'pdfPath'

def fix_content(content):
    """Fix all RedactArea patterns in content."""
    path_var = find_path_var(content)

    # Pattern 1: .RedactArea(page, new Rect(...), renderDpi: N)
    pattern1 = r'(\.RedactArea\([^,]+,\s*new Rect\([^)]+\)),\s*renderDpi:\s*(\d+)\)'
    replacement1 = rf'\1, {path_var}, renderDpi: \2)'
    content = re.sub(pattern1, replacement1, content)

    # Pattern 2: .RedactArea(page, new Rect(...)  // With inline comment
    pattern2 = r'(\.RedactArea\([^,]+,\s*new Rect\([^)]+\))\);(\s*//)'
    replacement2 = rf'\1, {path_var});\2'
    content = re.sub(pattern2, replacement2, content)

    # Pattern 3: .RedactArea(page, new Rect(...));  // No renderDpi
    pattern3 = r'(\.RedactArea\([^,]+,\s*new Rect\([^)]+\))\);'
    replacement3 = rf'\1, {path_var});'
    content = re.sub(pattern3, replacement3, content)

    return content

def main():
    files = [
        "Security/ContentRemovalVerificationTests.cs",
        "Integration/BlindRedactionVerificationTests.cs",
        "Integration/ContentStreamConsolidationTests.cs",
        "Integration/ExcessiveRedactionTests.cs",
        "Integration/ExternalToolRedactionValidationTests.cs",
        "Integration/ForensicRedactionVerificationTests.cs",
        "Integration/BlackBoxRedactionTests.cs",
        "Integration/VisualCoordinateVerificationTests.cs",
        "Integration/PreciseRedactionTests.cs",
        "Integration/SpecializedRedactionTests.cs",
        "UI/ViewModelIntegrationTests.cs",
    ]

    total_fixes = 0

    for rel_path in files:
        file_path = TEST_DIR / rel_path
        if not file_path.exists():
            continue

        print(f"Processing: {rel_path}")

        with open(file_path, 'r', encoding='utf-8') as f:
            original = f.read()

        fixed = fix_content(original)

        if fixed != original:
            # Count fixes
            fixes = fixed.count('.RedactArea(') - original.count('.RedactArea(')
            if fixes < 0:
                fixes = 0

            # Create backup
            with open(str(file_path) + '.p3.bak', 'w', encoding='utf-8') as f:
                f.write(original)

            # Write fixed
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(fixed)

            # Count actual changes
            changes = sum(1 for a, b in zip(original.split('\n'), fixed.split('\n')) if a != b)
            print(f"  ✓ Fixed {changes} lines")
            total_fixes += changes
        else:
            print(f"  ℹ No changes")

    print(f"\nTotal lines fixed: {total_fixes}")
    return 0

if __name__ == '__main__':
    sys.exit(main())
