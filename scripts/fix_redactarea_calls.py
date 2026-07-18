#!/usr/bin/env python3
"""
Script to fix RedactArea() calls to include pdfFilePath parameter.
This script intelligently detects the correct file path variable and updates all calls.
"""

import os
import re
import sys
import subprocess
from pathlib import Path
from datetime import datetime
from typing import List, Tuple, Dict

# Configuration
PROJECT_ROOT = Path(__file__).parent.parent
TEST_DIR = PROJECT_ROOT / "Excise.App.Tests"
LOG_DIR = PROJECT_ROOT / "logs"
TIMESTAMP = datetime.now().strftime("%Y%m%d_%H%M%S")

# Log files
LOG_FILE = LOG_DIR / f"fix_redactarea_{TIMESTAMP}.log"
SUCCESS_LOG = LOG_DIR / f"fix_redactarea_success_{TIMESTAMP}.log"
ERROR_LOG = LOG_DIR / f"fix_redactarea_errors_{TIMESTAMP}.log"
DETAILS_LOG = LOG_DIR / f"fix_redactarea_details_{TIMESTAMP}.log"

# Create logs directory
LOG_DIR.mkdir(exist_ok=True)

class Logger:
    """Simple logger that writes to multiple files and stdout"""

    def __init__(self):
        self.log_file = open(LOG_FILE, 'w')
        self.success_file = open(SUCCESS_LOG, 'w')
        self.error_file = open(ERROR_LOG, 'w')
        self.details_file = open(DETAILS_LOG, 'w')

    def log(self, msg, to_stdout=True):
        """Log to main log file and optionally stdout"""
        self.log_file.write(msg + '\n')
        self.log_file.flush()
        if to_stdout:
            print(msg)

    def success(self, msg):
        """Log success"""
        self.success_file.write(msg + '\n')
        self.success_file.flush()
        self.log(msg)

    def error(self, msg):
        """Log error"""
        self.error_file.write(msg + '\n')
        self.error_file.flush()
        self.log(msg)

    def detail(self, msg):
        """Log detailed info"""
        self.details_file.write(msg + '\n')
        self.details_file.flush()

    def close(self):
        self.log_file.close()
        self.success_file.close()
        self.error_file.close()
        self.details_file.close()

logger = Logger()

def find_path_variable(content: str, file_path: str) -> str:
    """
    Intelligently find the file path variable name used in the test.
    Looks for common patterns like pdfPath, filePath, inputPath, testPath.
    """
    # Common variable patterns in order of preference
    patterns = [
        r'(?:var|string)\s+(pdfPath)\s*=',
        r'(?:var|string)\s+(inputPath)\s*=',
        r'(?:var|string)\s+(filePath)\s*=',
        r'(?:var|string)\s+(testPath)\s*=',
        r'(?:var|string)\s+(path)\s*=.*\.pdf',
        r'PdfReader\.Open\(\s*([a-zA-Z_][a-zA-Z0-9_]*)\s*,',
    ]

    for pattern in patterns:
        match = re.search(pattern, content)
        if match:
            var_name = match.group(1)
            logger.detail(f"  Found path variable '{var_name}' in {file_path}")
            return var_name

    # Fallback: look for any variable that contains "Path" or "path"
    match = re.search(r'(?:var|string)\s+([a-zA-Z_]*[Pp]ath[a-zA-Z_]*)\s*=', content)
    if match:
        var_name = match.group(1)
        logger.detail(f"  Found fallback path variable '{var_name}' in {file_path}")
        return var_name

    logger.error(f"  ⚠ Could not find path variable in {file_path}")
    return "pdfPath"  # Default fallback

def fix_redactarea_calls(file_path: Path) -> Tuple[int, int]:
    """
    Fix RedactArea() calls in a single file.
    Returns (number_of_fixes, number_of_errors)
    """
    try:
        with open(file_path, 'r', encoding='utf-8') as f:
            original_content = f.read()
    except Exception as e:
        logger.error(f"  ✗ Failed to read {file_path}: {e}")
        return 0, 1

    # Find the correct path variable for this file
    path_var = find_path_variable(original_content, str(file_path))

    # Pattern to match RedactArea calls WITHOUT pdfFilePath parameter
    # Matches: .RedactArea(page, area, renderDpi: 150)
    # or: .RedactArea(page, area, 150)

    patterns = [
        # Pattern 1: .RedactArea(arg1, arg2, renderDpi: N)
        (
            r'\.RedactArea\(\s*([^,]+),\s*([^,]+),\s*renderDpi:\s*(\d+)\s*\)',
            rf'.RedactArea(\1, \2, {path_var}, renderDpi: \3)'
        ),
        # Pattern 2: .RedactArea(arg1, arg2, N) where N is just a number
        (
            r'\.RedactArea\(\s*([^,]+),\s*([^,]+),\s*(\d+)\s*\)',
            rf'.RedactArea(\1, \2, {path_var}, \3)'
        ),
    ]

    content = original_content
    total_fixes = 0

    for pattern, replacement in patterns:
        # Count matches before replacement
        matches_before = len(re.findall(pattern, content))

        if matches_before > 0:
            # Perform replacement
            content_after = re.sub(pattern, replacement, content)

            # Count how many were actually replaced
            matches_after = len(re.findall(pattern, content_after))
            fixes_made = matches_before - matches_after

            if fixes_made > 0:
                logger.detail(f"    Pattern '{pattern}' - fixed {fixes_made} calls")
                total_fixes += fixes_made
                content = content_after

    # Check if we made any changes
    if content == original_content:
        return 0, 0

    # Write the fixed content
    try:
        # Create backup
        backup_path = str(file_path) + '.bak'
        with open(backup_path, 'w', encoding='utf-8') as f:
            f.write(original_content)

        # Write fixed version
        with open(file_path, 'w', encoding='utf-8') as f:
            f.write(content)

        logger.detail(f"    Backup saved to {backup_path}")
        return total_fixes, 0

    except Exception as e:
        logger.error(f"  ✗ Failed to write {file_path}: {e}")
        return total_fixes, 1

def main():
    logger.log("=" * 80)
    logger.log("RedactArea() Call Fixer (Python Version)")
    logger.log("=" * 80)
    logger.log(f"Started: {datetime.now()}")
    logger.log(f"Test Directory: {TEST_DIR}")
    logger.log(f"Log Files:")
    logger.log(f"  Main: {LOG_FILE}")
    logger.log(f"  Success: {SUCCESS_LOG}")
    logger.log(f"  Errors: {ERROR_LOG}")
    logger.log(f"  Details: {DETAILS_LOG}")
    logger.log("")

    # Find all C# files with RedactArea calls
    cs_files = []
    for root, dirs, files in os.walk(TEST_DIR):
        for file in files:
            if file.endswith('.cs'):
                file_path = Path(root) / file
                try:
                    with open(file_path, 'r', encoding='utf-8') as f:
                        if '.RedactArea(' in f.read():
                            cs_files.append(file_path)
                except:
                    continue

    if not cs_files:
        logger.log("No files found with RedactArea() calls")
        logger.close()
        return 0

    logger.log(f"Found {len(cs_files)} files with RedactArea() calls")
    logger.log("")

    # Process each file
    total_files = 0
    total_fixes = 0
    total_errors = 0

    for file_path in cs_files:
        total_files += 1
        relative_path = file_path.relative_to(PROJECT_ROOT)

        logger.log("=" * 80)
        logger.log(f"Processing: {relative_path}")
        logger.log("=" * 80)

        # Count RedactArea calls before
        with open(file_path, 'r', encoding='utf-8') as f:
            before_content = f.read()
            before_count = before_content.count('.RedactArea(')

        logger.log(f"  RedactArea() calls found: {before_count}")

        # Fix the file
        fixes, errors = fix_redactarea_calls(file_path)

        if fixes > 0:
            logger.success(f"  ✓ Fixed {fixes} calls in {relative_path}")
            total_fixes += fixes
        elif errors > 0:
            logger.error(f"  ✗ Errors processing {relative_path}")
            total_errors += errors
        else:
            logger.log(f"  ℹ No changes needed (already fixed or no matching pattern)")

        logger.log("")

    # Test compilation
    logger.log("=" * 80)
    logger.log("Testing compilation...")
    logger.log("=" * 80)

    try:
        result = subprocess.run(
            ['dotnet', 'build'],
            cwd=TEST_DIR,
            capture_output=True,
            text=True,
            timeout=120
        )

        if result.returncode == 0:
            logger.success("✓ Compilation successful!")

            # Remove backups
            for backup in TEST_DIR.rglob('*.cs.bak'):
                backup.unlink()
            logger.log("  Removed backup files")
        else:
            logger.error("⚠ Compilation failed. Errors:")
            errors = [line for line in result.stderr.split('\n') if 'error CS' in line]
            for error in errors[:20]:  # Show first 20 errors
                logger.error(f"    {error}")

            logger.log("")
            logger.log("Backups preserved as *.cs.bak files")
            logger.log(f"To restore: find {TEST_DIR} -name '*.cs.bak' -exec bash -c 'mv \"$1\" \"${{1%.bak}}\"' _ {{}} \\;")

    except subprocess.TimeoutExpired:
        logger.error("⚠ Compilation timed out")
        total_errors += 1
    except Exception as e:
        logger.error(f"⚠ Compilation error: {e}")
        total_errors += 1

    # Summary
    logger.log("")
    logger.log("=" * 80)
    logger.log("SUMMARY")
    logger.log("=" * 80)
    logger.log(f"Files processed: {total_files}")
    logger.log(f"Calls fixed: {total_fixes}")
    logger.log(f"Errors encountered: {total_errors}")
    logger.log("")
    logger.log(f"Completed: {datetime.now()}")
    logger.log("")
    logger.log("Detailed logs:")
    logger.log(f"  All: {LOG_FILE}")
    logger.log(f"  Success: {SUCCESS_LOG}")
    logger.log(f"  Errors: {ERROR_LOG}")
    logger.log(f"  Details: {DETAILS_LOG}")

    logger.close()

    return 0 if total_errors == 0 else 1

if __name__ == '__main__':
    sys.exit(main())
