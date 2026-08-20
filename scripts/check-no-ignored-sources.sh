#!/usr/bin/env bash
#
# No source file may be gitignored.
#
# ── The defect this catches ──────────────────────────────────────────────────
#
# .gitignore line 102 was `coverage/` — unanchored, so it matched a directory
# of that name at ANY depth. A new test directory, Excise.App.Tests/UI/Coverage/,
# holding three source files, was therefore invisible to git. `git add -A`
# added nothing, `git status` showed nothing, the local build was green because
# the files were on disk, and CI would have failed to compile a commit that
# looked complete. It was noticed by accident.
#
# That is the whole class: a pattern meant for build output that also matches
# something we wrote. It fails SILENTLY and in the direction of losing work.
#
# ── What it checks ───────────────────────────────────────────────────────────
#
# Every ignored file that LOOKS like source (by extension) and does not live in
# a known output location is a failure. Locations are matched from the front of
# the path, so `/artifacts/` is exempt and `Excise.Core/artifacts-doc/` is not.
#
# An entry that genuinely should stay ignored goes in the allowlist below, with
# a reason — same posture as tests/skip-allowlist.
#
# Usage: scripts/check-no-ignored-sources.sh
#
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO"

# Both checks run in ONE python pass. The first cut looped in bash over every
# ignored path — 216,229 of them on this repo — with a regex per iteration, and
# cost 30-60s of t0. The git plumbing itself takes under two seconds; the shell
# loop was the whole bill.
python3 - "$REPO" <<'PYEOF'
import os, re, subprocess, sys

repo = sys.argv[1]

# Extensions we author. Deliberately includes .json/.tsv/.txt: the checked-in
# baselines, manifests and expectation files this project gates on are data we
# wrote, and losing one silently is as bad as losing a .cs.
SOURCE_EXT = re.compile(r"\.(cs|axaml|xaml|csproj|sln|props|targets|sh|ps1|bat|py|tsv|json|md|yml|yaml|resx)$")

# Directories that legitimately hold generated or vendored content. Matched from
# the FRONT of the path, so `artifacts/` is exempt and `Excise.Core/artifacts-doc/`
# is not.
OUTPUT_PREFIX = re.compile(
    r"^(artifacts|logs|dist|dist-[^/]*|coverage|coverage-report|coverage-results|publish|"
    r"output|test-output|screenshots|test-pdfs|packages|\.claude|\.vs|\.idea|\.wiki|"
    r"tools/vendor|tools/\.store|node_modules)/")

# Per-project build output, at any depth.
BUILD_DIR = re.compile(r"(^|/)(bin|obj|TestResults|__pycache__)/")

# Ignored paths that are genuinely not ours to track, with the reason.
ALLOW = [
    (re.compile(r"\.cs\.json$"), "per-test tool sidecars written beside the test file, not authored"),
]

def git(*args):
    return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True).stdout.split("\n")

# ── 1. Source files that are GITIGNORED ─────────────────────────────────────
violations = []
for path in git("ls-files", "--others", "--ignored", "--exclude-standard"):
    if not path or not SOURCE_EXT.search(path):
        continue
    if OUTPUT_PREFIX.match(path) or BUILD_DIR.search(path):
        continue
    if any(rx.search(path) for rx, _ in ALLOW):
        continue
    violations.append(path)

if violations:
    print(f"\u274c {len(violations)} source file(s) are gitignored and would never be committed:",
          file=sys.stderr)
    for v in violations:
        print(f"     {v}", file=sys.stderr)
    print("   Find the rule with: git check-ignore -v <path>", file=sys.stderr)
    print("   Usually the fix is to ANCHOR an output pattern (/coverage/ not coverage/),", file=sys.stderr)
    print("   not to add an exception.", file=sys.stderr)
    sys.exit(1)

# ── 2. Source files that are BINARY to text tools ───────────────────────────
#
# A literal control byte (NUL, 0x01...) inside a string literal is valid C# and
# compiles fine, but it makes `file` report the source as "data" — and grep
# SKIPS binary files silently. Every grep-based gate then reads that file as
# EMPTY: check-doc-claim-freshness.sh, check-gate-asymmetry.sh, the unwired-api
# scan, verify-true-redaction.sh.
#
# Found by the #1029 audit: five files were in that state, one of them
# ScannedRasterRedactionLeakTests.cs — a REDACTION leak test invisible to the
# redaction-architecture guard. The fix is a C# escape (\u0001): identical
# character, readable file.
binary = []
for path in git("ls-files", "*.cs", "*.sh", "*.md", "*.axaml", "*.csproj", "*.props", "*.targets"):
    if not path:
        continue
    try:
        data = open(os.path.join(repo, path), "rb").read()
    except OSError:
        continue
    if any(b < 9 or b in (11, 12) or 13 < b < 32 for b in data):
        binary.append(path)

if binary:
    print(f"\u274c {len(binary)} source file(s) contain literal control bytes, so grep skips them:",
          file=sys.stderr)
    for b in binary:
        print(f"     {b}", file=sys.stderr)
    print("   Every grep-based gate reads these as EMPTY. Replace the literal byte", file=sys.stderr)
    print("   with a C# escape (\\u0001, \\0) — same character, readable file.", file=sys.stderr)
    sys.exit(1)

print("\u2705 no source file is gitignored, and none is binary to grep.")
PYEOF
