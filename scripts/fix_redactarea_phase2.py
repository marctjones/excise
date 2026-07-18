#!/usr/bin/env python3
"""
Phase 2: Fix RedactArea() calls that don't have renderDpi parameter.
Pattern: .RedactArea(page, area) -> .RedactArea(page, area, pdfPath)
"""

import re
import sys
from pathlib import Path
from datetime import datetime

PROJECT_ROOT = Path(__file__).parent.parent
TEST_DIR = PROJECT_ROOT / "Excise.App.Tests"
LOG_DIR = PROJECT_ROOT / "logs"
TIMESTAMP = datetime.now().strftime("%Y%m%d_%H%M%S")
LOG_FILE = LOG_DIR / f"fix_redactarea_phase2_{TIMESTAMP}.log"

LOG_DIR.mkdir(exist_ok=True)

def log(msg):
    """Log to file and stdout"""
    with open(LOG_FILE, 'a') as f:
        f.write(msg + '\n')
    print(msg)

def find_path_variable(content: str, file_path: str) -> str:
    """Find the file path variable name"""
    patterns = [
        r'(?:var|string)\s+(pdfPath)\s*=',
        r'(?:var|string)\s+(inputPath)\s*=',
        r'(?:var|string)\s+(filePath)\s*=',
        r'(?:var|string)\s+(testPath)\s*=',
        r'(?:var|string)\s+(redactedPath)\s*=.*Save',  # Sometimes they use redactedPath for output
        r'PdfReader\.Open\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,',
    ]

    for pattern in patterns:
        match = re.search(pattern, content)
        if match:
            var_name = match.group(1)
            # Skip if it's the output path
            if 'redacted' in var_name.lower() or 'output' in var_name.lower():
                continue
            return var_name

    return "pdfPath"

def fix_file(file_path: Path) -> int:
    """Fix a single file. Returns number of fixes."""
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            content = f.read()
    except Exception as e:
        log(f"  ✗ Failed to read {file_path}: {e}")
        return 0

    # Find path variable
    path_var = find_path_variable(content, str(file_path))

    # Pattern: .RedactArea(arg1, arg2) with NO third parameter
    # This pattern is more careful to avoid matching calls that already have 3+ parameters
    pattern = r'\.RedactArea\(\s*([^,]+),\s*([^,)]+)\s*\)(?!,)'

    matches = list(re.finditer(pattern, content))

    if not matches:
        return 0

    # Create backup
    backup_path = str(file_path) + '.phase2.bak'
    with open(backup_path, 'w', encoding='utf-8') as f:
        f.write(content)

    # Replace from end to start to preserve positions
    for match in reversed(matches):
        start, end = match.span()
        arg1 = match.group(1).strip()
        arg2 = match.group(2).strip()

        # Build replacement
        replacement = f'.RedactArea({arg1}, {arg2}, {path_var})'

        # Replace
        content = content[:start] + replacement + content[end:]

    # Write fixed content
    with open(file_path, 'w', encoding='utf-8') as f:
        f.write(content)

    log(f"  ✓ Fixed {len(matches)} calls (pattern: .RedactArea(page, area))")
    return len(matches)

def main():
    log("=" * 80)
    log("Phase 2: Fix RedactArea() calls without renderDpi parameter")
    log("=" * 80)
    log(f"Started: {datetime.now()}")
    log("")

    # Get files with compilation errors
    import subprocess
    result = subprocess.run(
        ['dotnet', 'build'],
        cwd=TEST_DIR,
        capture_output=True,
        text=True
    )

    # Extract files with errors
    error_lines = [line for line in result.stderr.split('\n') if 'error CS7036' in line and 'RedactArea' in line]
    error_files = set()

    for line in error_lines:
        match = re.search(r'([^:]+\.cs)\(\d+,\d+\):', line)
        if match:
            file_name = match.group(1)
            error_files.add(TEST_DIR / file_name)

    log(f"Found {len(error_files)} files with RedactArea errors")
    log("")

    total_fixes = 0

    for file_path in sorted(error_files):
        relative_path = file_path.relative_to(PROJECT_ROOT)
        log(f"Processing: {relative_path}")

        fixes = fix_file(file_path)
        total_fixes += fixes
        log("")

    # Test compilation
    log("=" * 80)
    log("Testing compilation...")
    log("=" * 80)

    result = subprocess.run(
        ['dotnet', 'build'],
        cwd=TEST_DIR,
        capture_output=True,
        text=True,
        timeout=120
    )

    if result.returncode == 0:
        log("✓ Compilation successful!")

        # Remove backups
        for backup in TEST_DIR.rglob('*.phase2.bak'):
            backup.unlink()
    else:
        log("⚠ Still have errors:")
        errors = [line for line in result.stderr.split('\n') if 'error CS' in line][:10]
        for error in errors:
            log(f"  {error}")

    log("")
    log("=" * 80)
    log("SUMMARY")
    log("=" * 80)
    log(f"Files processed: {len(error_files)}")
    log(f"Calls fixed: {total_fixes}")
    log(f"Log: {LOG_FILE}")

    return 0 if result.returncode == 0 else 1

if __name__ == '__main__':
    sys.exit(main())
