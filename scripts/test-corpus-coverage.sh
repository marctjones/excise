#!/usr/bin/env bash
# Selftest for the corpus-coverage arithmetic (#1972, #958).
#
# run-full-suite.sh's footer once compared PDF FILE counts against manifest PAGE
# counts (685 vs 1279) and called a complete sweep partial. This plants each
# case in a temp corpus and asserts the verdict of the real helper both ways:
# a complete multi-page corpus must read FULL; a missing PDF, an empty manifest
# and an unscanned page must read PARTIAL / non-zero. A check that has never
# failed is not a check, so every failing case asserts the failing exit code.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CC="$ROOT/scripts/corpus_coverage.py"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }
cc() { python3 -I "$CC" "$@"; }

mkdir -p "$WORK/corpus/sub"
: > "$WORK/corpus/multi.pdf"
: > "$WORK/corpus/sub/single.pdf"
printf '# comment\nmulti.pdf\t1\tPASS\nmulti.pdf\t2\tPASS\nmulti.pdf\t3\tPASS\nsub/single.pdf\t1\tPASS\n' > "$WORK/m.tsv"

# (1) FULL: 2 PDFs / 4 pages. Counting files against pages (2 vs 4) was the defect.
got="$(cc preflight "$WORK/corpus" "$WORK/m.tsv")"
[ "$got" = "2 2 0 0 4" ] || fail "complete corpus preflight: expected '2 2 0 0 4', got '$got'"
read -r exp pres _ extra pages <<< "$got"
cc line corpus "$pres" "$exp" "$extra" "$pages" > "$WORK/line.txt" \
    || fail "a complete multi-page corpus must not read partial: $(cat "$WORK/line.txt")"

# (2) An extra unmanifested PDF is reported but never counts as coverage or as missing.
: > "$WORK/corpus/extra.pdf"
got="$(cc preflight "$WORK/corpus" "$WORK/m.tsv")"
[ "$got" = "2 2 0 1 4" ] || fail "extra file: expected '2 2 0 1 4', got '$got'"
read -r exp pres _ extra pages <<< "$got"
cc line corpus "$pres" "$exp" "$extra" "$pages" | grep -q 'unmanifested' || fail "extra PDFs must be mentioned"
rm "$WORK/corpus/extra.pdf"

# (3) PLANTED: an expected PDF is absent -> PARTIAL, nonzero.
rm "$WORK/corpus/sub/single.pdf"
got="$(cc preflight "$WORK/corpus" "$WORK/m.tsv")"
[ "$got" = "2 1 1 0 4" ] || fail "missing PDF: expected '2 1 1 0 4', got '$got'"
read -r exp pres _ extra pages <<< "$got"
if cc line corpus "$pres" "$exp" "$extra" "$pages" >/dev/null; then fail "a missing expected PDF must read partial"; fi
: > "$WORK/corpus/sub/single.pdf"

# (4) PLANTED: empty manifest is never 'full'.
if cc line corpus 0 0 0 0 >/dev/null; then fail "an empty manifest must read partial"; fi

# (5) Pages are judged on actual (path, page) keys of the scan report.
printf '%s\n' '{"results":[{"path":"multi.pdf","pageNumber":1},{"path":"multi.pdf","pageNumber":2},' \
    '{"path":"multi.pdf","pageNumber":3},{"path":"sub/single.pdf","pageNumber":1}]}' > "$WORK/full.json"
got="$(cc pages "$WORK/full.json" "$WORK/m.tsv" 2>/dev/null)"
[ "$got" = "4 4 0" ] || fail "fully scanned: expected '4 4 0', got '$got'"

# PLANTED: page 3 of multi.pdf not scanned. An unrelated extra row keeps the row COUNT at 4,
# which is exactly what a count comparison would have mistaken for complete.
printf '%s\n' '{"results":[{"path":"multi.pdf","pageNumber":1},{"path":"multi.pdf","pageNumber":2},' \
    '{"path":"sub/single.pdf","pageNumber":1},{"path":"other.pdf","pageNumber":1}]}' > "$WORK/short.json"
got="$(cc pages "$WORK/short.json" "$WORK/m.tsv" 2>"$WORK/err.txt")"
[ "$got" = "4 3 1" ] || fail "unscanned page: expected '4 3 1' (an unrelated extra row must not mask it), got '$got'"
grep -q 'NOT SCANNED  multi.pdf#p3' "$WORK/err.txt" || fail "the unscanned page must be named"

# (6) The strict post-scan check and the footer both use this helper.
grep -q 'corpus_coverage' "$ROOT/scripts/run-exploratory-corpus.sh" || fail "run-exploratory-corpus.sh must use the shared helper"
grep -q 'corpus_coverage.py' "$ROOT/scripts/run-full-suite.sh" || fail "run-full-suite.sh must use the shared helper"
if grep -n "find .*-name '\*.pdf'" "$ROOT/scripts/run-full-suite.sh" >/dev/null; then
    fail "run-full-suite.sh must not compare PDF file counts against manifest page counts again"
fi

echo "ok: corpus coverage arithmetic compares like with like"
