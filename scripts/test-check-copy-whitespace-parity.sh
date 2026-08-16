#!/usr/bin/env bash
#
# Selftest for scripts/check-copy-whitespace-parity.sh (#837/#841, per #1012).
#
# Three properties, none of which can be observed by running the real gate on a
# machine that happens to have poppler and the corpus:
#
#   1. Strict mode (EXCISE_REQUIRE_PARITY_TOOLS=1, how CI runs it) turns every
#      SKIP reason into a hard FAIL. A skipping gate on CI measures nothing and
#      greens the ratchet vacuously.
#   2. A `--filter` matching no tests is a FAILURE. `dotnet test` exits 0 in
#      that case, so renaming the harness would otherwise leave a green gate
#      that ran nothing (#941).
#   3. The convenience skip still works locally, which is why the gate is
#      tolerated at all.
#
# Hermetic: the gate resolves everything relative to its own location, so a copy
# in $TMP/scripts makes $TMP the repo. PATH is reduced to a fixed set of
# symlinked essentials plus the fakes, so "poppler is absent" is a property of
# the test and not of the machine running it.
#
# Sub-second, no corpus, no dotnet. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-copy-whitespace-parity.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

# ── a PATH we control ────────────────────────────────────────────────────────
ESS="$TMP/essential"; mkdir -p "$ESS"
for t in bash sh env dirname basename mktemp cat tee grep sed ls rm cp mv awk python3 uname; do
    p="$(command -v "$t" 2>/dev/null || true)"
    [[ -n "$p" ]] && ln -sf "$p" "$ESS/$t"
done
command -v pdftotext >/dev/null 2>&1 && [[ -e "$ESS/pdftotext" ]] \
    && fail "the sanitized PATH leaked pdftotext — the absent-tool cases would be meaningless"

R="$TMP/root"
mkdir -p "$R/scripts" "$R/bin" "$R/test-pdfs/federal"
cp "$SCRIPT" "$R/scripts/check-copy-whitespace-parity.sh"

# The corpus files the gate requires, derived from the gate itself.
seed_corpus() {
    while IFS= read -r f; do
        [[ -z "$f" ]] && continue
        mkdir -p "$R/$(dirname "$f")"
        printf '%%PDF-1.7\n' > "$R/$f"
    done < <(sed -n '/^REQUIRED_CORPUS=(/,/^)/p' "$SCRIPT" | sed '1d;$d' | tr -d ' ')
}
seed_corpus
[[ -n "$(ls -A "$R"/test-pdfs/federal 2>/dev/null)" ]] || fail "no required corpus paths extracted from the gate"

printf '#!/usr/bin/env bash\nexit 0\n' > "$R/bin/pdftotext"; chmod +x "$R/bin/pdftotext"
# The fake harness runner. FAKE_DOTNET selects what `dotnet test` reports.
cat > "$R/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
case "${FAKE_DOTNET:-pass}" in
  nomatch) echo "No test matches the given testcase filter \`FullyQualifiedName~CopyWhitespaceParityHarness\` in suite"; exit 0 ;;
  regress) echo "  Failed CopyWhitespaceParityHarness.WordAgreement [1 ms]"; echo "Failed!  - Failed: 1"; exit 1 ;;
  *)       echo "  Passed CopyWhitespaceParityHarness.WordAgreement [1 ms]"; exit 0 ;;
esac
FAKE
chmod +x "$R/bin/dotnet"

run() {   # run <PATH-prefix-dirs> [env assignments via caller]
    set +e
    OUT="$(PATH="$1" "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
    RC=$?
    set -e
}

FULL="$R/bin:$ESS"

echo "==> selftest: check-copy-whitespace-parity.sh"

# ── 1. Tool absent, default mode -> documented convenience SKIP ─────────────
run "$ESS"
[[ "$RC" -eq 0 ]] || fail "without poppler the local default is a clean skip (exit $RC)
$OUT"
grep -q "^SKIP" <<<"$OUT" || fail "expected a SKIP:
$OUT"
echo "    poppler absent, local default        exit 0 (SKIP)"

# ── 2. Tool absent, STRICT mode -> FAIL. The guarded property. ──────────────
set +e
OUT="$(PATH="$ESS" EXCISE_REQUIRE_PARITY_TOOLS=1 "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "strict mode must refuse to skip — a skipping gate greens the ratchet vacuously
$OUT"
grep -q "refuses to silently skip" <<<"$OUT" || fail "expected the strict-mode refusal:
$OUT"
echo "    poppler absent, strict mode          exit $RC"

# ── 3. Corpus incomplete, strict mode -> FAIL ───────────────────────────────
missing="$(ls "$R"/test-pdfs/federal | head -1)"
mv "$R/test-pdfs/federal/$missing" "$TMP/held.pdf"
set +e
OUT="$(PATH="$FULL" EXCISE_REQUIRE_PARITY_TOOLS=1 "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "a partial corpus in strict mode must fail — it would measure less than it claims
$OUT"
echo "    corpus incomplete, strict mode       exit $RC"
mv "$TMP/held.pdf" "$R/test-pdfs/federal/$missing"

# ── 4. The harness was renamed away -> FAIL, not a green empty run ──────────
set +e
OUT="$(PATH="$FULL" FAKE_DOTNET=nomatch "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "a filter matching NO tests must fail — dotnet test exits 0 and measures nothing
$OUT"
grep -q "matched NO tests" <<<"$OUT" || fail "expected the vacuous-run refusal:
$OUT"
echo "    filter matched no tests              exit $RC"

# ── 5. A real floor regression propagates ───────────────────────────────────
set +e
OUT="$(PATH="$FULL" FAKE_DOTNET=regress "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
RC=$?
set -e
[[ "$RC" -ne 0 ]] || fail "a failing harness run must fail the gate
$OUT"
echo "    harness reports a regression         exit $RC"

# ── 6. Everything present and passing -> pass ───────────────────────────────
set +e
OUT="$(PATH="$FULL" FAKE_DOTNET=pass "$R/scripts/check-copy-whitespace-parity.sh" 2>&1)"
RC=$?
set -e
[[ "$RC" -eq 0 ]] || fail "a complete, passing run must pass, or the failures above prove nothing (exit $RC)
$OUT"
echo "    tools + corpus + passing harness     exit 0"

echo "==> check-copy-whitespace-parity.sh selftest OK"
