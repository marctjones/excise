#!/usr/bin/env bash
#
# Skip budget (#619, simplified #1172).
#
# A skipped test is invisible coverage loss — a whole file can stop running
# while other tests keep the same lines "covered", and neither the coverage
# floors nor the #894 test-count gate can see it happen.
#
# The fix used to be an EXTERNAL, ENVIRONMENT-CONDITIONED ALLOWLIST
# (tests/skip-allowlist/*.txt, `[requires: corpus:X]` markers, #854) that let
# a corpus-less CI runner and a corpus-equipped dev box agree on what may
# skip. That machinery drifted, red-lit Linux CI for days, and could not be
# re-synced from a macOS-only box — see git history on this file before
# #1172 for the full mechanism, now deleted.
#
# #1172's replacement: every skip already carries its reason IN CODE. xUnit
# v3's `Assert.SkipWhen(cond, "reason")` / `Assert.SkipUnless(cond, "reason")`
# / `Assert.Skip("reason")` and `[Fact(Skip = "reason")]` /
# `[Theory(Skip = "reason")]` all land in the trx identically:
#
#   <UnitTestResult outcome="NotExecuted" testName="...">
#     <Output><ErrorInfo><Message>reason</Message></ErrorInfo></Output>
#   </UnitTestResult>
#
# So the gate reads that back and fails on any skip whose reason is missing
# or blank. No external allowlist file, no --update, no environment
# conditioning: the reason travels with the test, is reviewed in the same
# diff as the skip, and needs no re-syncing between runners — #854's failure
# mode does not exist here, by construction, not by more machinery.
#
# Usage:
#   scripts/check-skip-budget.sh <project.csproj> [--trx <file>]...
#
#   --trx <file>   reuse a trx from a run that already happened instead of
#                  executing the whole suite a second time just to read
#                  skips. May be REPEATED: the full-suite runner chunks the
#                  big projects by test class, and the union of the chunk
#                  trx files is exactly one unfiltered run. t1 passes the
#                  three filtered Rendering rows the same way.
#                  Every --trx must exist and be non-empty: each one is a PART
#                  of a union, and reading the others as the whole would
#                  report the missing part's skips as absent.
set -euo pipefail

PROJECT="${1:?usage: check-skip-budget.sh <project.csproj> [--trx <file>]...}"
shift
EXISTING_TRX=()
while [[ $# -gt 0 ]]; do
  case "$1" in
    --trx) EXISTING_TRX+=("${2:?--trx needs a file}"); shift ;;
    *) echo "check-skip-budget: unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
NAME="$(basename "$PROJECT" .csproj)"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

if [[ ${#EXISTING_TRX[@]} -gt 0 ]]; then
  echo "==> reading skips from ${#EXISTING_TRX[@]} trx file(s) (no second test run)"
  _i=0
  for _t in "${EXISTING_TRX[@]}"; do
    _i=$(( _i + 1 ))
    echo "    $_t"
    if [[ ! -s "$_t" ]]; then
      echo "FAIL: no trx produced at $_t — the run that should have written it did not complete."
      echo "      It is one part of a ${#EXISTING_TRX[@]}-file union; reading the rest as the whole"
      echo "      would report its skips as absent. Not treating that as 'no skips'."
      exit 1
    fi
    cp "$_t" "$TMP/r$_i.trx"
  done
else
  echo "==> running $NAME to enumerate skips"
  # #1144: this re-runs the WHOLE suite (slow -- minutes). If a trx already
  # exists from a prior run (the coverage run, or a --logger trx you kept),
  # reuse it:  scripts/check-skip-budget.sh <proj> --trx <that.trx>
  "$ROOT/scripts/assert-fresh.sh" --configuration Debug "$PROJECT"
  dotnet test "$PROJECT" --nologo --logger "trx;LogFileName=$TMP/r1.trx" >"$TMP/out.log" 2>&1 || true
fi

if ! ls "$TMP"/r*.trx >/dev/null 2>&1; then
  echo "FAIL: no trx produced — the run did not complete. Not treating that as 'no skips'."
  tail -20 "$TMP/out.log" 2>/dev/null || true
  exit 1
fi

# Skipped tests in a trx carry outcome="NotExecuted". Several trx files may be
# the chunks of one project run: read all of them. The reason (if any) lives
# in <Output><ErrorInfo><Message> — the same element for a static
# [Fact(Skip="...")] and for a dynamic Assert.SkipWhen/SkipUnless/Skip,
# verified empirically against a real xunit.v3 + vstest-trx-logger run
# (#1172): both land there identically.
# #1527 adds one check on top: a declared reason is not the same as a TRUE
# reason. "smoke corpus not present" satisfied #1172 for a month while the
# corpus sat seven directory levels above a bounded locator's reach — a skip
# whose reason asserted the absence of something that was present. So a reason
# that CLAIMS absence must name the absolute paths it searched
# (TestRepoLayout.AbsenceReason emits "[excise-searched: /abs/a | /abs/b]") and
# this gate re-tests every one of them. If a claimed-absent path exists, the
# skip is a lie and the gate fails.
#
# Note the checker does NO path resolution of its own, deliberately: resolving
# "test-pdfs/smoke" from this script's own directory in a worktree would
# reproduce the exact blindness being checked for, and agree with the lie.
# The test emits absolute paths; the checker only asks whether they exist.
python3 - "$TMP"/r*.trx <<'PY'
import os
import re
import sys
import xml.etree.ElementTree as ET

SEARCHED = re.compile(r"excise-searched:(?P<paths>[^\]]*)")

undeclared = []
untrue = []
claims_checked = 0
declared = 0
seen = set()

for path in sys.argv[1:]:
    root = ET.parse(path).getroot()
    for result in root.iter():
        if not result.tag.endswith("UnitTestResult") or result.get("outcome") != "NotExecuted":
            continue
        name = result.get("testName", "")
        exec_id = result.get("executionId", "")
        key = (name, exec_id)
        if key in seen:
            continue
        seen.add(key)

        message = ""
        for output in result:
            if not output.tag.endswith("Output"):
                continue
            for error_info in output:
                if not error_info.tag.endswith("ErrorInfo"):
                    continue
                for msg in error_info:
                    if msg.tag.endswith("Message"):
                        message = (msg.text or "").strip()

        if message:
            declared += 1
        else:
            undeclared.append(name)
            continue

        # The absence claim, if the reason carries one.
        found = SEARCHED.search(message)
        if not found:
            continue
        paths = [p.strip() for p in found.group("paths").split("|")]
        paths = [p for p in paths if p and p != "(none)"]
        if not paths:
            continue
        claims_checked += 1
        present = [p for p in paths if os.path.exists(p)]
        if present:
            untrue.append((name, message, present))

if untrue:
    print()
    print("FAIL: tests are skipping with a reason that is NOT TRUE.")
    print("      The reason claims something is absent. It is present. This is #1527:")
    print("      three redaction gates skipped every corpus row for a month behind")
    print('      "smoke corpus not present", while the corpus sat seven directory levels')
    print("      above a bounded locator's reach. #1172's gate was satisfied — a reason")
    print("      existed — and a redaction gate ran 2 of 195 assertions.")
    print()
    print("      Fix the LOCATOR, not the message. Repository fixtures and corpora are")
    print("      found with Excise.TestSupport.TestRepoLayout, which resolves the main")
    print("      checkout from a worktree's .git file; nothing should be counting '..'.")
    for n, msg, present in sorted(set((n, m, tuple(p)) for n, m, p in untrue)):
        print(f"        + {n}")
        print(f"          reason:  {msg}")
        for p in present:
            print(f"          EXISTS:  {p}")
    sys.exit(1)

if undeclared:
    print()
    print("FAIL: tests are skipping with no declared reason.")
    print("      A skip with no reason is coverage loss with no audit trail — it cannot")
    print("      be told apart from an accident, and nothing forces it to be revisited.")
    print("      Give it one, in the test itself:")
    print('        Assert.SkipWhen(condition, "why")')
    print('        Assert.SkipUnless(condition, "why")')
    print('        Assert.Skip("why")')
    print('        [Fact(Skip = "why")]  /  [Theory(Skip = "why")]')
    for n in sorted(set(undeclared)):
        print(f"        + {n}")
    sys.exit(1)

print(f"==> skip budget OK ({declared} skip(s), every one with a declared in-code reason; "
      f"{claims_checked} absence claim(s) re-checked against the filesystem)")
PY
