#!/usr/bin/env python3
"""
Phase 4: Fix remaining RedactArea issues
- Wrong argument positions (5/6 argument errors)
- Missing pdfPath variable declarations
- CS7036 errors (missing pdfFilePath entirely)
- BatchRedactService missing RedactionOptions
"""

import re
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).parent.parent
TEST_DIR = PROJECT_ROOT / "Excise.App.Tests"

def find_path_var(content):
    """Find or infer the file path variable."""
    patterns = [
        r'var\s+(testPdf|pdfPath|inputPath|filePath|inputPdf)\s*=',
        r'string\s+(testPdf|pdfPath|inputPath|filePath|inputPdf)\s*=',
        r'PdfReader\.Open\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,',
    ]

    for pattern in patterns:
        match = re.search(pattern, content)
        if match:
            var = match.group(1)
            if 'redacted' not in var.lower() and 'output' not in var.lower():
                return var

    return 'testPdf'

def fix_content(content, file_path):
    """Fix all RedactArea patterns."""
    path_var = find_path_var(content)
    original_lines = content.split('\n')
    fixed_lines = []

    for line in original_lines:
        fixed_line = line

        # Pattern 1: .RedactArea(page, area, renderDpi: N) - 4 args, need to insert path
        # This became 5 args after previous scripts incorrectly inserted pdfPath
        match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^,]+),\s*([^,]+),\s*renderDpi:\s*(\d+)\)(.*)', line)
        if match:
            prefix, arg1, arg2, arg3, dpi, suffix = match.groups()
            # arg3 is the wrongly positioned pdfPath - remove it and put it in correct position
            # Correct: .RedactArea(page, area, pdfPath, renderDpi: N)
            fixed_line = f'{prefix}.RedactArea({arg1}, {arg2}, {path_var}, renderDpi: {dpi}){suffix}'

        # Pattern 2: .RedactArea(page, area, pdfPath, renderDpi, dpi: N) - 6 args from over-fixing
        match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^,]+),\s*([^,]+),\s*renderDpi:\s*(\d+),\s*renderDpi:\s*(\d+)\)(.*)', line)
        if match:
            prefix, arg1, arg2, arg3, dpi1, dpi2, suffix = match.groups()
            # Take the first renderDpi
            fixed_line = f'{prefix}.RedactArea({arg1}, {arg2}, {arg3}, renderDpi: {dpi1}){suffix}'

        # Pattern 3: .RedactArea(page, area, pdfPath, renderDpi, N) - 6 args, wrong syntax
        match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^,]+),\s*([^,]+),\s*renderDpi,\s*(\d+)\)(.*)', line)
        if match:
            prefix, arg1, arg2, arg3, dpi, suffix = match.groups()
            fixed_line = f'{prefix}.RedactArea({arg1}, {arg2}, {arg3}, renderDpi: {dpi}){suffix}'

        # Pattern 4: .RedactArea(page, area) - missing pdfPath and renderDpi
        match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^)]+)\);(.*)', line)
        if match and 'renderDpi' not in line and path_var not in line:
            prefix, arg1, arg2, suffix = match.groups()
            # Only fix if there are exactly 2 arguments
            if arg2.count(',') == 0:  # No commas means it's the second arg
                fixed_line = f'{prefix}.RedactArea({arg1}, {arg2}, {path_var});{suffix}'

        # Pattern 5: BatchRedactService.RedactMatches - missing RedactionOptions
        if 'BatchRedactService' in line and 'RedactMatches' in line and 'new RedactionOptions' not in line:
            match = re.search(r'(.*)\.RedactMatches\(([^,]+),\s*([^,]+),\s*([^)]+)\)(.*)', line)
            if match:
                prefix, arg1, arg2, arg3, suffix = match.groups()
                # Check if we're missing the options parameter
                if arg3.count(',') == 0:  # Only 3 args, need 4
                    fixed_line = f'{prefix}.RedactMatches({arg1}, {arg2}, {arg3}, new RedactionOptions {{ UseGlyphLevelRedaction = true }}){suffix}'

        fixed_lines.append(fixed_line)

    return '\n'.join(fixed_lines)

def main():
    # Get list of files with errors from build log
    files_to_fix = [
        "Integration/VisualCoordinateVerificationTests.cs",
        "UI/ViewModelIntegrationTests.cs",
        "Integration/CoordinateConversionTests.cs",
        "Integration/VeraPdfConformanceTests.cs",
        "UI/MouseEventSimulationTests.cs",
        "Integration/ComprehensiveRedactionTests.cs",
        "Integration/TextExtractionAfterRedactionTests.cs",
        "Integration/FormXObjectRedactionTests.cs",
        "Integration/GlyphRemovalVerificationTests.cs",
        "Integration/OriginalFileProtectionTests.cs",
        "Integration/GuiRedactionSimulationTests.cs",
        "Integration/Pdf20SupportTests.cs",
        "Integration/InlineImageRedactionTests.cs",
        "Integration/SelectiveInstanceRedactionTests.cs",
        "Integration/SearchAndRedactTests.cs",
        "Integration/Pdf17SupportTests.cs",
        "Integration/MetadataSanitizationTests.cs",
    ]

    total_fixes = 0

    for rel_path in files_to_fix:
        file_path = TEST_DIR / rel_path
        if not file_path.exists():
            print(f"⚠ File not found: {rel_path}")
            continue

        print(f"Processing: {rel_path}")

        with open(file_path, 'r', encoding='utf-8') as f:
            original = f.read()

        fixed = fix_content(original, file_path)

        if fixed != original:
            # Create backup
            with open(str(file_path) + '.p4.bak', 'w', encoding='utf-8') as f:
                f.write(original)

            # Write fixed
            with open(file_path, 'w', encoding='utf-8') as f:
                f.write(fixed)

            # Count changes
            changes = sum(1 for a, b in zip(original.split('\n'), fixed.split('\n')) if a != b)
            print(f"  ✓ Fixed {changes} lines")
            total_fixes += changes
        else:
            print(f"  ℹ No changes")

    print(f"\nTotal lines fixed: {total_fixes}")
    return 0

if __name__ == '__main__':
    sys.exit(main())
