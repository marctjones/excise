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

# Extensions we author. Deliberately includes .json/.tsv/.txt: the checked-in
# baselines, manifests and expectation files this project gates on are data we
# wrote, and losing one silently is as bad as losing a .cs.
SOURCE_EXT='\.(cs|axaml|xaml|csproj|sln|props|targets|sh|ps1|bat|py|tsv|json|md|yml|yaml|resx)$'

# Directories that legitimately hold generated or vendored content.
OUTPUT_PREFIX='^(artifacts|logs|dist|dist-[^/]*|coverage|coverage-report|coverage-results|publish|output|test-output|screenshots|test-pdfs|packages|\.claude|\.vs|\.idea|\.wiki|tools/vendor|tools/\.store|node_modules)/'

# Per-project build output, at any depth.
BUILD_DIR='(^|/)(bin|obj|TestResults|__pycache__)/'

# Allowlist: ignored paths that are genuinely not ours to track. One per line,
# as an anchored regex, followed by # and the reason.
ALLOW=(
  '\.cs\.json$'      # per-test tool sidecars written next to the test file, not authored
)

violations=()
while IFS= read -r path; do
  [[ -n "$path" ]] || continue
  [[ "$path" =~ $SOURCE_EXT ]] || continue
  [[ "$path" =~ $OUTPUT_PREFIX ]] && continue
  [[ "$path" =~ $BUILD_DIR ]] && continue

  allowed=0
  for rule in "${ALLOW[@]}"; do
    pattern="${rule%%#*}"
    pattern="${pattern%"${pattern##*[![:space:]]}"}"
    [[ "$path" =~ $pattern ]] && { allowed=1; break; }
  done
  (( allowed )) || violations+=("$path")
done < <(git ls-files --others --ignored --exclude-standard)

if (( ${#violations[@]} > 0 )); then
  echo "❌ ${#violations[@]} source file(s) are gitignored and would never be committed:" >&2
  printf '     %s\n' "${violations[@]}" >&2
  echo "   Find the rule with: git check-ignore -v <path>" >&2
  echo "   Usually the fix is to ANCHOR an output pattern (/coverage/ not coverage/)," >&2
  echo "   not to add an exception." >&2
  exit 1
fi

echo "✅ no source file is gitignored."
