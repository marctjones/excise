#!/usr/bin/env bash
#
# diff-behavior.sh — judge the DELTA of a change, not the state (#945).
#
# Runs the SAME mutating command twice — once against the current working
# tree, once against a given git ref — and diffs the two output PDFs through
# mutool as a word multiset. This is the instrument that actually caught
# #919's over-removal: no test knew to look, but "run the old binary and the
# new binary and diff what came out" answers `what ELSE changed?` in one
# command, with zero test-writing.
#
# Usage:
#   scripts/diff-behavior.sh <git-ref> <command ... {OUT} ...>
#
#   The command MUST contain the placeholder {OUT}; each side substitutes its
#   own output-PDF path. It is run with the side's checkout root as CWD, so:
#     - use ABSOLUTE paths for input files (gitignored corpora do not exist
#       in the ref-side worktree),
#     - use `dotnet run --project <proj> -c Release -- ...` so each side
#       builds and runs its OWN code (no stale-binary risk, #950).
#
# Example — what would have caught #919 before merge:
#   scripts/diff-behavior.sh HEAD~1 \
#     dotnet run --project Excise.Cli -c Release -- \
#       redact "$PWD/test-pdfs/smoke/irs-w9.pdf" --text "Name" -o {OUT}
#
# Exit codes: 0 = outputs textually identical (word multiset), 1 = they
# differ (report printed — a DIFF IS A QUESTION, not automatically a defect:
# the requested delta itself appears here too; judge whether everything
# listed is the delta you asked for), 2 = usage/environment error.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

die() { echo "✗ $*" >&2; exit 2; }

[ $# -ge 2 ] || die "usage: scripts/diff-behavior.sh <git-ref> <command ... {OUT} ...>"
command -v mutool >/dev/null 2>&1 || die "mutool is required (the oracle must not be excise itself)"

REF="$1"; shift
git -C "$ROOT" rev-parse --verify --quiet "$REF^{commit}" >/dev/null || die "not a git ref: $REF"

case "$*" in
    *"{OUT}"*) : ;;
    *) die "the command must contain the {OUT} placeholder for the output PDF" ;;
esac

TMP="$(mktemp -d)"
WORKTREE="$TMP/ref-checkout"
cleanup() {
    git -C "$ROOT" worktree remove --force "$WORKTREE" >/dev/null 2>&1 || true
    rm -rf "$TMP"
}
trap cleanup EXIT

run_side() { # $1=label $2=cwd $3=outpdf ... rest = command words
    local label="$1" cwd="$2" out="$3"; shift 3
    local cmd=()
    local w
    for w in "$@"; do cmd+=("${w//\{OUT\}/$out}"); done
    echo "▶ [$label] ${cmd[*]}"
    ( cd "$cwd" && "${cmd[@]}" ) > "$TMP/$label.log" 2>&1
    local rc=$?
    if [ $rc -ne 0 ]; then
        echo "✗ [$label] command exited $rc — last output:" >&2
        tail -20 "$TMP/$label.log" >&2
        exit 2
    fi
    # Refuse to diff a vacuous run: a missing/empty output is a failed
    # measurement, not a clean one. (Learned the hard way — a wrong flag once
    # printed help, produced nothing, and nearly read as success.)
    [ -s "$out" ] || { echo "✗ [$label] produced no output at $out — refusing to diff nothing" >&2; tail -20 "$TMP/$label.log" >&2; exit 2; }
}

echo "▶ preparing ref-side worktree at $REF"
git -C "$ROOT" worktree add --detach "$WORKTREE" "$REF" >/dev/null 2>&1 || die "could not create worktree for $REF"

run_side new "$ROOT" "$TMP/new.pdf" "$@"
run_side old "$WORKTREE" "$TMP/old.pdf" "$@"

mutool draw -F txt -o "$TMP/old.txt" "$TMP/old.pdf" >/dev/null 2>&1 || die "mutool could not read the OLD side's output"
mutool draw -F txt -o "$TMP/new.txt" "$TMP/new.pdf" >/dev/null 2>&1 || die "mutool could not read the NEW side's output"

python3 - "$TMP/old.txt" "$TMP/new.txt" <<'PY'
import sys, collections

def words(path):
    with open(path, encoding="utf-8", errors="replace") as f:
        return collections.Counter(w for w in f.read().split() if any(c.isalnum() for c in w))

old, new = words(sys.argv[1]), words(sys.argv[2])
gone  = old - new    # in old output, missing from new = DESTROYED by the change
added = new - old    # in new output, absent from old  = ADDED by the change

def letters(c): return sum(sum(1 for ch in w if ch.isalnum()) * n for w, n in c.items())

if not gone and not added:
    print("✓ outputs are word-identical: the change altered nothing mutool can see")
    sys.exit(0)

def show(title, counter, limit=40):
    total = sum(counter.values())
    print(f"\n{title} ({total} word occurrence(s), {letters(counter)} letters):")
    for w, n in counter.most_common(limit):
        print(f"  {n:5d} × {w!r}")
    if len(counter) > limit:
        print(f"  … and {len(counter) - limit} more distinct words")

print("Δ the outputs DIFFER — judge whether everything below is the delta you asked for:")
show("DESTROYED by the new side (present in old output, gone in new)", gone)
show("ADDED by the new side (absent in old output)", added)
sys.exit(1)
PY
