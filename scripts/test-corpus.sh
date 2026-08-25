#!/usr/bin/env bash
# test-corpus.sh — selftest for scripts/corpus.sh.
#
# Every case here is a bug the script actually had during development, kept so
# it cannot come back:
#
#   1. bash 3.2 — the first draft used `mapfile`, which macOS's /bin/bash does
#      not have. It failed with "mapfile: command not found" and then reported
#      "no corpora in tier 'core'", which is a MISLEADING error, not a loud
#      one. Two other scripts in this repo carry comments saying they avoid
#      mapfile for exactly this reason; the lesson had already been learned
#      and I re-introduced it.
#   2. asking for a PLANNED corpus exited 0 — "you asked for a corpus and got
#      nothing" looked like success to any caller.
#   3. `verify` must fail when a destination is not gitignored. This found a
#      real one on its first run (tessdata/ ignored only *.traineddata, so
#      anything else dropped there was committable).
#   4. `remove` must refuse without --yes.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
CORPUS="$SCRIPT_DIR/corpus.sh"

GREEN=$'\033[32m'; RED=$'\033[31m'; RESET=$'\033[0m'
fails=0
ok()   { printf "  %s✓%s %s\n" "$GREEN" "$RESET" "$1"; }
bad()  { printf "  %s✗%s %s\n" "$RED" "$RESET" "$1"; fails=$((fails+1)); }

expect_exit() {
    local want="$1"; local desc="$2"; shift 2
    "$@" >/dev/null 2>&1
    local got=$?
    [ "$got" = "$want" ] && ok "$desc (exit $got)" || bad "$desc: expected exit $want, got $got"
}

echo "==> corpus.sh selftest"

# 1. Runs under bash 3.2. Not "under whatever bash is first in PATH" — the
#    point is the OLD one, since that is what a stock macOS invokes.
for cmd in list verify du; do
    expect_exit 0 "bash 3.2: $cmd" /bin/bash "$CORPUS" "$cmd"
done
if /bin/bash "$CORPUS" list 2>&1 | grep -qiE "command not found|unbound variable"; then
    bad "bash 3.2: list emitted a shell error"
else
    ok "bash 3.2: list is clean of shell errors"
fi

# 2. Unfulfilled requests are never exit 0.
expect_exit 1 "unknown corpus fails"            /bin/bash "$CORPUS" fetch definitely-not-a-corpus
expect_exit 3 "planned corpus does not succeed" /bin/bash "$CORPUS" fetch gwg-processing-steps
expect_exit 2 "fetch with no arguments fails"   /bin/bash "$CORPUS" fetch
expect_exit 2 "unknown tier fails"              /bin/bash "$CORPUS" fetch --tier nonsense

# 3. verify is the load-bearing gate: it must FAIL on a non-gitignored
#    destination. Proven by adding one, not by trusting that it would.
TMP_REG="$(mktemp)"; trap 'rm -f "$TMP_REG"' EXIT
cp "$ROOT/tests/corpora.tsv" "$TMP_REG"
printf 'selftest-committable\tcore\tscripts\tdownload-test-pdfs.sh\t1M\tnone\tdeliberately NOT gitignored\n' \
    >> "$ROOT/tests/corpora.tsv"
if /bin/bash "$CORPUS" verify >/dev/null 2>&1; then
    bad "verify PASSED with a non-gitignored destination — the gate is vacuous"
else
    ok "verify fails on a non-gitignored destination"
fi
cp "$TMP_REG" "$ROOT/tests/corpora.tsv"
expect_exit 0 "verify passes again once reverted" /bin/bash "$CORPUS" verify

# 4. A planned row without an issue reference is a registry error: "planned"
#    with nowhere to look is indistinguishable from "forgotten".
cp "$ROOT/tests/corpora.tsv" "$TMP_REG"
printf 'selftest-noissue\tplanned\ttest-pdfs/selftest\tsomeday\t1M\tnone\tno issue cited\n' \
    >> "$ROOT/tests/corpora.tsv"
if /bin/bash "$CORPUS" verify >/dev/null 2>&1; then
    bad "verify PASSED on a planned row with no issue reference"
else
    ok "verify fails on a planned row with no issue reference"
fi
cp "$TMP_REG" "$ROOT/tests/corpora.tsv"

# 5. remove refuses without --yes. Nothing is deleted by this selftest.
expect_exit 2 "remove refuses without --yes" /bin/bash "$CORPUS" remove verapdf

# 6. Anti-vacuity: the registry must actually contain rows, or every check
#    above passes over an empty file.
n=$(awk -F'\t' '!/^#/ && NF>=7' "$ROOT/tests/corpora.tsv" | wc -l | tr -d ' ')
[ "$n" -ge 10 ] && ok "registry has $n rows" || bad "registry has only $n rows — checks may be vacuous"

echo
if [ "$fails" -eq 0 ]; then
    echo "${GREEN}corpus.sh selftest PASSED${RESET}"
else
    echo "${RED}corpus.sh selftest FAILED ($fails)${RESET}" >&2; exit 1
fi
