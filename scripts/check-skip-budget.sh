#!/usr/bin/env bash
#
# Skip budget (#619).
#
# A skipped test is invisible coverage loss. Ours do not just skip for missing
# external tools — some skip on PROCESS-GLOBAL STATE, which means a test can stop
# running because an unrelated test was added to the same class, and nothing
# complains.
#
# That is not hypothetical:
#
#   MainWindowViewModelTests.HiddenTextToggles_DoNotLoadOcrAssemblyBeforeRasterizedScan
#     Assert.SkipWhen(IsAssemblyLoaded("Excise.Ocr"), ...)
#
#   It asserts that ordinary hidden-text reveal does NOT drag in the OCR
#   assembly — a real privacy/dependency property. Whether it runs depends on
#   which tests loaded Excise.Ocr earlier in the same process. On 2026-07-13,
#   adding unrelated tests to that class silently turned it off. The suite went
#   from 1 skip to 2 and stayed green.
#
# So: enumerate the skips, and fail the build when the set CHANGES. A new skip
# must be justified and added here on purpose. An allow-listed skip that stops
# skipping must be removed from here — that is coverage coming BACK, and the
# allowlist should not quietly hide it.
#
# Usage:
#   scripts/check-skip-budget.sh <project.csproj> [--update]
#
#   --update   rewrite the allowlist from the current run (review the diff!)
set -euo pipefail

PROJECT="${1:?usage: check-skip-budget.sh <project.csproj> [--update] [--trx <file>]}"
UPDATE="${2:-}"
# --trx lets CI reuse a trx from a run that already happened (the coverage run),
# instead of executing the whole suite a second time just to count skips.
EXISTING_TRX=""
if [[ "${2:-}" == "--trx" ]]; then EXISTING_TRX="${3:-}"; UPDATE=""; fi
if [[ "${3:-}" == "--trx" ]]; then EXISTING_TRX="${4:-}"; fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="$(basename "$PROJECT" .csproj)"
ALLOWLIST="$ROOT/tests/skip-allowlist/$NAME.txt"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$(dirname "$ALLOWLIST")"

if [[ -n "$EXISTING_TRX" ]]; then
  echo "==> reading skips from $EXISTING_TRX (no second test run)"
  cp "$EXISTING_TRX" "$TMP/r.trx" 2>/dev/null || true
else
  echo "==> running $NAME to enumerate skips"
  dotnet test "$PROJECT" --nologo --logger "trx;LogFileName=$TMP/r.trx" >"$TMP/out.log" 2>&1 || true
fi

if [[ ! -f "$TMP/r.trx" ]]; then
  echo "FAIL: no trx produced — the run did not complete. Not treating that as 'no skips'."
  tail -20 "$TMP/out.log"
  exit 1
fi

# Skipped tests in a trx carry outcome="NotExecuted".
python3 - "$TMP/r.trx" >"$TMP/actual.txt" <<'PY'
import sys, xml.etree.ElementTree as ET
ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
root = ET.parse(sys.argv[1]).getroot()
names = set()
for r in root.iter():
    if r.tag.endswith("UnitTestResult") and r.get("outcome") == "NotExecuted":
        names.add(r.get("testName", "").split("(")[0])   # strip Theory args
for n in sorted(names):
    print(n)
PY

touch "$ALLOWLIST"

# Entries may carry a justification:  TestName   # why it is skipped
# Compare on the NAME only, but PRESERVE the reason across --update. An
# allowlist without reasons is a dump, and a dump rots into "33 skips, shrug".
grep -vE '^\s*(#|$)' "$ALLOWLIST" \
  | sed -e 's/[[:space:]]*#.*$//' -e 's/[[:space:]]*$//' \
  | LC_ALL=C sort -u > "$TMP/expected.txt" || true

# comm needs BOTH sides in the same collation. Python sorts by codepoint;
# `sort` uses locale collation. Mixing them makes comm report the same line
# as both added AND removed. Force C collation on both sides.
LC_ALL=C sort -u "$TMP/actual.txt" -o "$TMP/actual.txt"

if [[ "$UPDATE" == "--update" ]]; then
  # Capture the OLD allowlist contents BEFORE opening the `> "$ALLOWLIST"`
  # redirection below. Bash sets up a compound command's output redirection
  # (which truncates $ALLOWLIST) before running any of the command's body, so
  # a `grep ... "$ALLOWLIST"` *inside* the loop below would always see an
  # empty file — reason would always be empty and every entry would fall
  # through to "TODO: justify or fix" regardless of what was there (#663).
  # Grepping against $OLD instead of the file sidesteps the ordering bug.
  OLD="$(cat "$ALLOWLIST" 2>/dev/null || true)"

  # Preserve hand-written comment BLOCKS across --update (#668). A comment block
  # that precedes an entry is a human note about that skip (e.g. a
  # "# --- veraPDF-dependent ---" grouping header). Previously --update emitted
  # only the auto-header + entries, silently discarding those notes. Tag every
  # hand-written comment line with the entry it immediately precedes, so notes
  # travel with their test across the sort. The regenerated auto-header is
  # filtered out so it can't accumulate. Map lines: NAME<TAB>comment.
  printf '%s\n' "$OLD" | awk '
    /^# (Skips allow-listed for|Every line is coverage we are NOT getting|Format:  TestName)/ { next }
    /^#$/ { next }
    /^[[:space:]]*#/ { buf[++n] = $0; next }        # hand-written comment
    /^[[:space:]]*$/ { n = 0; next }                # blank line ends a block
    {
      name = $0; sub(/[[:space:]]*#.*$/, "", name); sub(/[[:space:]]*$/, "", name);
      for (i = 1; i <= n; i++) printf "%s\t%s\n", name, buf[i];
      n = 0;
    }' > "$TMP/comment-map.txt"

  {
    echo "# Skips allow-listed for $NAME. See scripts/check-skip-budget.sh (#619)."
    echo "# Every line is coverage we are NOT getting. Justify it or delete it."
    echo "# Format:  TestName   # why"
    echo "#"
    while IFS= read -r name; do
      # Re-emit any hand-written comment block that preceded this entry (#668).
      awk -F'\t' -v n="$name" '$1 == n { sub(/^[^\t]*\t/, ""); print }' "$TMP/comment-map.txt"
      # `|| true` is load-bearing: with `set -e -o pipefail`, a grep that finds
      # nothing (the common case — a brand-new skip) returns 1 and would abort
      # the script mid-write, leaving an allowlist containing only its header.
      # `[^#]*#` (not `.*#`) matters: `.*` is greedy and matches through to
      # the LAST `#` on the line, so a reason that itself references another
      # issue (e.g. "#653: ...") would have everything up to and including
      # that inner `#` stripped too. `[^#]*` stops at the FIRST `#`, which is
      # the separator between the test name and the reason (discovered while
      # verifying #663 against real reasons that cite other issue numbers).
      reason="$( { printf '%s\n' "$OLD" | grep -E "^${name}([[:space:]]|#|\$)" 2>/dev/null || true; } \
                | head -1 | sed -n 's/[^#]*#[[:space:]]*//p')"
      if [[ -n "$reason" ]]; then
        printf '%s   # %s\n' "$name" "$reason"
      else
        printf '%s   # TODO: justify or fix\n' "$name"
      fi
    done < "$TMP/actual.txt"
  } > "$ALLOWLIST"
  echo "==> allowlist rewritten: $ALLOWLIST"
  echo "    REVIEW THE DIFF. Each new line is a test that stopped running."
  exit 0
fi

NEW="$(comm -13 "$TMP/expected.txt" "$TMP/actual.txt" || true)"
GONE="$(comm -23 "$TMP/expected.txt" "$TMP/actual.txt" || true)"

STATUS=0
if [[ -n "$NEW" ]]; then
  echo
  echo "FAIL: tests are skipping that are not allow-listed."
  echo "      A test that silently stops running is coverage loss you cannot see."
  echo "$NEW" | sed 's/^/        + /'
  STATUS=1
fi

if [[ -n "$GONE" ]]; then
  echo
  echo "FAIL: allow-listed skips are no longer skipping."
  echo "      That is coverage coming BACK — good. Remove them from the allowlist"
  echo "      so it cannot hide a future regression."
  echo "$GONE" | sed 's/^/        - /'
  STATUS=1
fi

if [[ $STATUS -eq 0 ]]; then
  echo "==> skip budget OK ($(wc -l < "$TMP/actual.txt" | tr -d ' ') allow-listed skip(s))"
else
  echo
  echo "To accept the current state: scripts/check-skip-budget.sh $PROJECT --update"
fi
exit $STATUS
