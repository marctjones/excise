#!/usr/bin/env python3
"""
Final comprehensive fix for all RedactArea calls.
This script intelligently handles all patterns and variable names.
"""

import re
import sys
from pathlib import Path
from datetime import datetime

PROJECT_ROOT = Path(__file__).parent.parent
TEST_DIR = PROJECT_ROOT / "Excise.App.Tests"
LOG_DIR = PROJECT_ROOT / "logs"
TIMESTAMP = datetime.now().strftime("%Y%m%d_%H%M%S")
LOG_FILE = LOG_DIR / f"fix_all_final_{TIMESTAMP}.log"

LOG_DIR.mkdir(exist_ok=True)

def log(msg):
    with open(LOG_FILE, 'a') as f:
        f.write(msg + '\n')
    print(msg)

def find_path_variables(content):
    """Find all potential file path variables in order of preference."""
    patterns = [
        (r'var\s+(pdfPath|inputPath|filePath|testPath|path)\s*=\s*.*\.pdf', 1),
        (r'string\s+(pdfPath|inputPath|filePath|testPath|path)\s*=\s*.*\.pdf', 1),
        (r'PdfReader\.Open\(\s*"([^"]+)"\s*,', None),  # Literal string
        (r'PdfReader\.Open\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,', 1),
    ]

    found_vars = []
    for pattern, group_idx in patterns:
        for match in re.finditer(pattern, content):
            if group_idx is not None:
                var_name = match.group(group_idx)
                if var_name not in ['redactedPath', 'outputPath', 'output']:
                    found_vars.append(var_name)

    # Return most common or first found
    if found_vars:
        # Prefer pdfPath, inputPath, filePath in that order
        for preferred in ['pdfPath', 'inputPath', 'filePath', 'testPath']:
            if preferred in found_vars:
                return preferred
        return found_vars[0]

    return 'pdfPath'  # Fallback

def fix_file(file_path):
    """Fix all RedactArea calls in a file."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
    except Exception as e:
        log(f"  ✗ Error reading {file_path}: {e}")
        return 0

    original_content = content
    path_var = find_path_variables(content)
    log(f"  Using path variable: {path_var}")

    fixes = 0

    # Pattern 1: .RedactArea(page, area, renderDpi: N) - ALREADY has pdfPath? Check first
    # Pattern 2: .RedactArea(page, area, renderDpi: N) - needs pdfPath
    # Pattern 3: .RedactArea(page, area) - needs pdfPath
    # Pattern 4: .RedactArea(page, area, N) - needs pdfPath

    # First, let's find all RedactArea calls and analyze them
    lines = content.split('\n')
    new_lines = []

    for i, line in enumerate(lines):
        new_line = line

        if 'RedactArea(' in line and 'void RedactArea' not in line and '//' not in line:
            # Extract the call
            # Check if it already has pdfPath or filePath as a parameter
            if f', {path_var},' in line or f', {path_var})' in line:
                # Already fixed
                new_lines.append(line)
                continue

            # Pattern: .RedactArea(page, area, renderDpi: N)
            match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^,]+),\s*renderDpi:\s*(\d+)\)(.*)', line)
            if match:
                prefix = match.group(1)
                arg1 = match.group(2)
                arg2 = match.group(3)
                dpi = match.group(4)
                suffix = match.group(5)
                new_line = f'{prefix}.RedactArea({arg1}, {arg2}, {path_var}, renderDpi: {dpi}){suffix}'
                fixes += 1
                log(f"    Line {i+1}: Fixed renderDpi pattern")
                new_lines.append(new_line)
                continue

            # Pattern: .RedactArea(page, area, N) where N is just digits
            match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^,]+),\s*(\d+)\)(.*)', line)
            if match:
                prefix = match.group(1)
                arg1 = match.group(2)
                arg2 = match.group(3)
                dpi = match.group(4)
                suffix = match.group(5)
                new_line = f'{prefix}.RedactArea({arg1}, {arg2}, {path_var}, {dpi}){suffix}'
                fixes += 1
                log(f"    Line {i+1}: Fixed positional DPI pattern")
                new_lines.append(new_line)
                continue

            # Pattern: .RedactArea(page, area) - just 2 args
            match = re.search(r'(.*)\.RedactArea\(([^,]+),\s*([^)]+)\);(.*)', line)
            if match and ',' in match.group(3) and match.group(3).count(',') == 0:  # Ensure only 2 args
                prefix = match.group(1)
                arg1 = match.group(2)
                arg2 = match.group(3)
                suffix = match.group(4)
                new_line = f'{prefix}.RedactArea({arg1}, {arg2}, {path_var});{suffix}'
                fixes += 1
                log(f"    Line {i+1}: Fixed 2-arg pattern")
                new_lines.append(new_line)
                continue

        new_lines.append(new_line)

    if fixes == 0:
        return 0

    new_content = '\n'.join(new_lines)

    # Create backup
    backup_path = str(file_path) + '.final.bak'
    with open(backup_path, 'w', encoding='utf-8') as f:
        f.write(original_content)

    # Write fixed content
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write(new_content)

    return fixes

def main():
    log("="*80)
    log("FINAL COMPREHENSIVE RedactArea Fixer")
    log("="*80)
    log(f"Started: {datetime.now()}")
    log("")

    files_to_fix = [
        "Integration/BlackBoxRedactionTests.cs",
        "Integration/BlindRedactionVerificationTests.cs",
        "Integration/CharacterLevelRedactionTests.cs",
        "Integration/ComprehensiveRedactionTests.cs",
        "Integration/ContentStreamConsolidationTests.cs",
        "Integration/CoordinateConversionTests.cs",
        "Integration/ExcessiveRedactionTests.cs",
        "Integration/ExternalToolRedactionValidationTests.cs",
        "Integration/ForensicRedactionVerificationTests.cs",
        "Integration/GlyphRemovalVerificationTests.cs",
        "Integration/GuiRedactionSimulationTests.cs",
        "Integration/InlineImageRedactionTests.cs",
        "Integration/MetadataSanitizationTests.cs",
        "Integration/OriginalFileProtectionTests.cs",
        "Integration/Pdf17SupportTests.cs",
        "Integration/Pdf20SupportTests.cs",
        "Integration/PdfConformanceTests.cs",
        "Integration/PreciseRedactionTests.cs",
        "Integration/RedactionCoordinateSystemTests.cs",
        "Integration/RedactionIntegrationTests.cs",
        "Integration/SelectiveInstanceRedactionTests.cs",
        "Integration/SpecializedRedactionTests.cs",
        "Integration/TextExtractionAfterRedactionTests.cs",
        "Integration/VeraPdfConformanceTests.cs",
        "Integration/VisualCoordinateVerificationTests.cs",
        "Security/ContentRemovalVerificationTests.cs",
        "UI/MouseEventSimulationTests.cs",
        "UI/ViewModelIntegrationTests.cs",
    ]

    total_fixes = 0

    for rel_path in files_to_fix:
        file_path = TEST_DIR / rel_path
        if not file_path.exists():
            log(f"⚠ File not found: {rel_path}")
            continue

        log(f"\nProcessing: {rel_path}")
        fixes = fix_file(file_path)

        if fixes > 0:
            log(f"  ✓ Fixed {fixes} calls")
            total_fixes += fixes
        else:
            log(f"  ℹ No fixes needed")

    log("")
    log("="*80)
    log("SUMMARY")
    log("="*80)
    log(f"Total files processed: {len(files_to_fix)}")
    log(f"Total fixes: {total_fixes}")
    log(f"Log: {LOG_FILE}")

    return 0

if __name__ == '__main__':
    sys.exit(main())
