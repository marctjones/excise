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
# Entries may declare what they depend on, so the same allowlist is correct both
# on a corpus-less CI runner and on a corpus-equipped dev machine (#854):
#
#   Some.Test.Name   # needs the poppler corpus [requires: corpus:poppler]
#
#   tool:NAME    NAME on PATH        corpus:NAME  test-pdfs/NAME non-empty
#   env:NAME     $NAME set non-empty  file:GLOB    repo-relative path/glob exists
#
# All listed specs must be present. Present => the test is expected to RUN here,
# so the reverse check stays silent for it. Absent, or no marker at all, => the
# original behaviour. The FORWARD check is never relaxed.
#
# Usage:
#   scripts/check-skip-budget.sh <project.csproj> [--update]
#
#   --update   rewrite the allowlist from the current run (review the diff!)
#              Keeps conditioned entries whose prerequisites are satisfied, so
#              running it on a dev machine cannot strip the entries CI needs.
#
# Environment:
#   SKIP_BUDGET_FORCE_ABSENT=spec[,spec]   force specs to resolve absent
#                                          (used by test-check-skip-budget.sh)
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

# ---------------------------------------------------------------------------
# Per-entry prerequisite conditioning (#854)
# ---------------------------------------------------------------------------
# Most entries here are gated on something the CI runner does not have: a
# gitignored corpus, or an optional external tool. Those tests skip on CI and
# RUN on a corpus-equipped dev machine. With an unconditional allowlist that
# made this gate un-greenable outside CI — it failed the reverse check on every
# local run, on all three projects, in both directions. A gate that always fails
# is a gate people stop reading, which is exactly how six un-allow-listed skips
# reddened test-linux for 8+ consecutive runs.
#
# So an entry may declare what it needs, INSIDE its justification:
#
#   Some.Test.Name   # needs the poppler corpus [requires: corpus:poppler]
#
# The marker lives inside the reason deliberately: name extraction and
# --update's reason preservation are untouched, and the marker travels with the
# reason for free (#663/#665/#668 keep passing unmodified).
#
# Specs:  tool:NAME    -> NAME is on PATH
#         corpus:NAME  -> test-pdfs/NAME exists and is non-empty
#         env:NAME     -> environment variable NAME is set and non-empty
#         file:GLOB    -> a repo-relative path (glob allowed) exists. Needed for
#                         dependencies that are a downloaded FILE rather than a
#                         tool on PATH or an env var -- e.g. the PDFBox jar,
#                         which the renderer auto-discovers in tools/vendor/.
# Multiple specs are space-separated and ALL must be present.
#
# Semantics — the FORWARD check is untouched. A skip that is not allow-listed
# still fails, always. Only the REVERSE check ("allow-listed but no longer
# skipping") is conditioned: if an entry declares prerequisites and they are
# all present, the test is EXPECTED to run here, so its running is not a
# finding. An entry with NO marker keeps today's exact behaviour, which makes
# "unconditioned" the safe default for any entry whose gate is unclear.
#
# Map lines: NAME<TAB>spec spec ...
#
# The name capture MUST match the forward check's above (line ~98), which
# strips from the first `#` and trims. It previously used `[^[:space:]#]*` —
# everything up to the first SPACE — so any name containing one could be
# allow-listed but never conditioned. That is every `[Theory]` case, whose
# display name is `Method(param: "value")`.
#
# The failure was silent and permanent in the worst direction: the forward
# check accepted the entry, the reverse check could not read its `[requires:]`
# marker, so on any machine where the prerequisites WERE present the entry
# reported "allow-listed skips are no longer skipping" on every single run.
# A gate that always fails locally is a gate people stop reading — the exact
# rot #854 was written to stop.
grep -vE '^\s*(#|$)' "$ALLOWLIST" \
  | sed -n 's/^\([^#]*[^[:space:]#]\)[[:space:]]*#.*\[requires:[[:space:]]*\([^]]*\)\].*$/\1\t\2/p' \
  | LC_ALL=C sort -u > "$TMP/conditioned.txt" || true

# SKIP_BUDGET_FORCE_ABSENT lets the selftest exercise the absent-prerequisite
# branch deterministically. The CI environment cannot be simulated by hiding
# 888MB of corpora, and testing the resolver beats testing the filesystem.
# Resolution is MEMOISED in $TMP/spec-cache. Without it this is called once per
# allow-listed entry — 200+ times on Excise.Rendering.Tests — and each corpus
# check hit the filesystem again. The first version also used
# `[[ -n "$(ls -A DIR)" ]]`, which slurps an entire directory listing into a
# string; against test-pdfs/ghent (308MB) and altona (268MB), a few hundred
# times over, that alone took the gate from ~6 minutes to 30+. Use a
# short-circuiting find instead, and resolve each distinct spec exactly once.
SPEC_CACHE_DIR="$TMP/spec-cache"
mkdir -p "$SPEC_CACHE_DIR"

spec_present() {
  local spec="$1"
  case ",${SKIP_BUDGET_FORCE_ABSENT:-}," in *",$spec,"*) return 1 ;; esac

  # No forks on the cache-hit path: bash-native slug (specs are ~20 chars, far
  # short of where 3.2's substitution gets slow) and `read < file`, a builtin.
  # This runs once per allow-listed entry — 200+ times on Rendering — so
  # $(printf|tr) plus $(cat) per call was ~1700 needless processes.
  local key="$SPEC_CACHE_DIR/${spec//[^A-Za-z0-9._-]/_}"
  if [[ -f "$key" ]]; then
    local cached
    read -r cached < "$key"
    [[ "$cached" == "1" ]]
    return
  fi

  local kind="${spec%%:*}" val="${spec#*:}" ok=1
  case "$kind" in
    tool)   command -v "$val" >/dev/null 2>&1 || ok=0 ;;
    # -print -quit stops at the FIRST entry instead of listing the directory.
    corpus) if [[ -d "$ROOT/test-pdfs/$val" ]] &&
                 [[ -n "$(find "$ROOT/test-pdfs/$val" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" ]]
            then ok=1; else ok=0; fi ;;
    env)    [[ -n "${!val:-}" ]] || ok=0 ;;
    # Glob, so a version-pinned filename does not have to be restated here
    # every time the vendored dependency is bumped.
    file)   compgen -G "$ROOT/$val" >/dev/null 2>&1 || ok=0 ;;
    # Unknown spec kind resolves ABSENT on purpose: a typo must not silently
    # disable the reverse check for that entry.
    *)      ok=0 ;;
  esac

  printf '%s' "$ok" > "$key"
  [[ "$ok" == "1" ]]
}

# 0 (true) only if the entry declares prerequisites AND every one is present.
entry_prereqs_present() {
  local name="$1" specs spec
  specs="$(awk -F'\t' -v n="$name" '$1 == n { print $2; exit }' "$TMP/conditioned.txt")"
  [[ -n "$specs" ]] || return 1
  for spec in $specs; do
    spec_present "$spec" || return 1
  done
  return 0
}

if [[ "$UPDATE" == "--update" ]]; then
  # --update writes the tests that are skipping NOW. On a machine where a
  # conditioned entry's prerequisite is PRESENT, that test is running, so a
  # naive rewrite would DELETE a correct CI entry — turning this flag from
  # "won't add the skip you need" into "removes the ones you had". Keep any
  # conditioned entry whose prerequisites are satisfied here (#854).
  while IFS=$'\t' read -r cname _; do
    [[ -n "$cname" ]] || continue
    entry_prereqs_present "$cname" || continue
    grep -qxF "$cname" "$TMP/actual.txt" || echo "$cname" >> "$TMP/actual.txt"
  done < "$TMP/conditioned.txt"
  LC_ALL=C sort -u "$TMP/actual.txt" -o "$TMP/actual.txt"

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

# Split the reverse check: an entry whose declared prerequisites are all
# present here is EXPECTED to run, so it is reported as satisfied, not failed
# (#854). Entries with no marker fall through to the original failure.
# Accumulate into FILES, not shell strings. macOS ships bash 3.2, whose
# ${var//pattern/} is pathologically slow on large values: stripping newlines
# from the ~20KB accumulated list of 218 Rendering entries measured at 7m57s
# on this machine (bash 3.2.57, arm64). Two such expansions turned a ~5-minute
# gate into a ~20-minute one that looked like a hang. Files keep it O(n) and
# make the "is it empty" test a stat instead of a full-string rewrite.
GONE_REAL_FILE="$TMP/gone-real.txt"
GONE_EXPECTED_FILE="$TMP/gone-expected.txt"
: > "$GONE_REAL_FILE"
: > "$GONE_EXPECTED_FILE"
if [[ -n "$GONE" ]]; then
  while IFS= read -r name; do
    [[ -n "$name" ]] || continue
    if entry_prereqs_present "$name"; then
      echo "$name" >> "$GONE_EXPECTED_FILE"
    else
      echo "$name" >> "$GONE_REAL_FILE"
    fi
  done <<< "$GONE"
fi

if [[ -s "$GONE_EXPECTED_FILE" ]]; then
  echo
  echo "==> $(wc -l < "$GONE_EXPECTED_FILE" | tr -d ' ') allow-listed skip(s) are running here because their"
  echo "    declared prerequisites are present. Expected — not a finding (#854)."
fi

if [[ -s "$GONE_REAL_FILE" ]]; then
  echo
  echo "FAIL: allow-listed skips are no longer skipping."
  echo "      That is coverage coming BACK — good. Remove them from the allowlist"
  echo "      so it cannot hide a future regression."
  echo "      (If instead this is environment-dependent, declare what it needs:"
  echo "       Test.Name   # why [requires: corpus:NAME] — see the header.)"
  sed 's/^/        - /' "$GONE_REAL_FILE"
  STATUS=1
fi

if [[ $STATUS -eq 0 ]]; then
  echo "==> skip budget OK ($(wc -l < "$TMP/actual.txt" | tr -d ' ') allow-listed skip(s))"
else
  echo
  echo "To accept the current state: scripts/check-skip-budget.sh $PROJECT --update"
fi
exit $STATUS
