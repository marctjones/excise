#!/usr/bin/env bash
#
# Selftest for scripts/check-extraction-parity.sh (#645, per #1012).
#
# This gate produces the redaction-security number this repo leans on:
# "excise cannot redact what excise cannot read". Four ways it can report that
# number without having earned it, all exercised here against known input:
#
#   * mutool or the corpus absent — it must FAIL, never skip (that is the bug it
#     exists to fix);
#   * the generator test renamed away — `dotnet test --filter` exits 0 having
#     run nothing;
#   * a STALE report left from an earlier run — measured in #941: with a planted
#     report and a filter matching zero tests, the gate printed
#     "==> extraction parity OK: 332 pages at or above their baseline floor"
#     having run nothing at all;
#   * a real coverage regression, which must fail.
#
# Hermetic: a copy of the gate in $TMP/scripts makes $TMP the repo, PATH is
# reduced to controlled essentials plus fakes, and the "measurement" is a
# hand-written report. No mutool, no corpus, no dotnet.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-extraction-parity.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

ESS="$TMP/essential"; mkdir -p "$ESS"
for t in bash sh env dirname basename mktemp cat tee grep sed ls rm cp mv awk python3 uname; do
    p="$(command -v "$t" 2>/dev/null || true)"
    [[ -n "$p" ]] && ln -sf "$p" "$ESS/$t"
done
[[ -e "$ESS/mutool" ]] && fail "the sanitized PATH leaked mutool — the absent-tool case would be meaningless"

R="$TMP/root"
mkdir -p "$R/scripts" "$R/bin" "$R/test-pdfs/smoke" "$R/tests/extraction-parity" "$R/logs/extraction-parity"
cp "$SCRIPT" "$R/scripts/check-extraction-parity.sh"
printf '%%PDF-1.7\n' > "$R/test-pdfs/smoke/fixture.pdf"
printf '#!/usr/bin/env bash\necho "mutool version 1.24.0"\n' > "$R/bin/mutool"; chmod +x "$R/bin/mutool"

REPORT="$R/logs/extraction-parity/latest-report.json"
BASELINE="$R/tests/extraction-parity/baseline.json"

write_report() {   # write_report <path> <coverage>
    cat > "$1" <<JSON
{ "generatedUtc": "2026-08-16T00:00:00Z", "mutoolVersion": "1.24.0",
  "aggregateCoverage": $2, "pageCount": 1,
  "pages": [ { "file": "fixture.pdf", "page": 1, "coverageRatio": $2, "similarity": 0.99 } ] }
JSON
}
cat > "$BASELINE" <<'JSON'
{ "generatedUtc": "2026-08-01T00:00:00Z", "mutoolVersion": "1.24.0",
  "aggregateCoverage": 0.99, "pageCount": 1,
  "pages": { "fixture.pdf#1": { "coverageFloor": 0.99, "similarityFloor": 0.98 } } }
JSON

# Fake `dotnet test`: FAKE_DOTNET selects the shape of the run, and — when it
# "runs" — it writes the report the gate then grades.
cat > "$R/bin/dotnet" <<FAKE
#!/usr/bin/env bash
case "\${FAKE_DOTNET:-pass}" in
  nomatch)  echo "No test matches the given testcase filter \\\`FullyQualifiedName~ExtractionParityTests.GenerateExtractionParityReport\\\` in suite"; exit 0 ;;
  noreport) echo "  Passed something else [1 ms]"; exit 0 ;;
  regress)  echo "  Passed ExtractionParityTests.GenerateExtractionParityReport [1 ms]"
            printf '%s' "\$(cat "$TMP/report-low.json")" > "$REPORT"; exit 0 ;;
  *)        echo "  Passed ExtractionParityTests.GenerateExtractionParityReport [1 ms]"
            printf '%s' "\$(cat "$TMP/report-ok.json")" > "$REPORT"; exit 0 ;;
esac
FAKE
chmod +x "$R/bin/dotnet"
write_report "$TMP/report-ok.json" 0.99
write_report "$TMP/report-low.json" 0.80

FULL="$R/bin:$ESS"
gate() {   # gate <PATH> [FAKE_DOTNET]
    set +e
    OUT="$(PATH="$1" FAKE_DOTNET="${2:-pass}" "$R/scripts/check-extraction-parity.sh" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-extraction-parity.sh"

# ── 1. mutool absent -> FAIL, never a skip ──────────────────────────────────
rm -f "$REPORT"
gate "$ESS"
[[ "$RC" -ne 0 ]] || fail "without mutool this gate must FAIL — silently skipping is the bug it exists to fix
$OUT"
echo "    mutool absent                        exit $RC"

# ── 2. corpus absent -> FAIL ────────────────────────────────────────────────
mv "$R/test-pdfs/smoke/fixture.pdf" "$TMP/held.pdf"
gate "$FULL"
[[ "$RC" -ne 0 ]] || fail "without the smoke corpus this gate must FAIL, not skip
$OUT"
echo "    smoke corpus absent                  exit $RC"
mv "$TMP/held.pdf" "$R/test-pdfs/smoke/fixture.pdf"

# ── 3. Generator renamed away, WITH A STALE REPORT PLANTED -> FAIL ─────────
# The #941 measurement. If the gate did not delete the previous report first,
# it would grade the leftover and print a green parity number over an empty run.
cp "$TMP/report-ok.json" "$REPORT"
gate "$FULL" nomatch
[[ "$RC" -ne 0 ]] || fail "a filter matching no tests must fail even with a stale report on disk (#941)
$OUT"
grep -q "matched NO tests" <<<"$OUT" || fail "expected the vacuous-run refusal:
$OUT"
grep -q "extraction parity OK" <<<"$OUT" \
    && fail "the gate graded a STALE report — the pre-run deletion is not working"
echo "    renamed generator + stale report     exit $RC"

# ── 4. Ran, but produced no report -> FAIL ─────────────────────────────────
cp "$TMP/report-ok.json" "$REPORT"
gate "$FULL" noreport
[[ "$RC" -ne 0 ]] || fail "no report means nothing was measured — that is not a pass
$OUT"
echo "    ran but produced no report           exit $RC"

# ── 5. A coverage REGRESSION -> FAIL. The guarded property. ────────────────
gate "$FULL" regress
[[ "$RC" -ne 0 ]] || fail "coverage below the checked-in floor must fail
$OUT"
grep -q "extraction-parity regression" <<<"$OUT" || fail "expected the regression verdict:
$OUT"
echo "    coverage below floor                 exit $RC"

# ── 6. At the floor -> pass ────────────────────────────────────────────────
gate "$FULL" pass
[[ "$RC" -eq 0 ]] || fail "a measurement at the floor must pass, or the failures above prove nothing (exit $RC)
$OUT"
grep -q "extraction parity OK" <<<"$OUT" || fail "expected the OK verdict:
$OUT"
echo "    coverage at the floor                exit 0"

echo "==> check-extraction-parity.sh selftest OK"
