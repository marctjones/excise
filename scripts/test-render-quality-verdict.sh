#!/usr/bin/env bash
# test-render-quality-verdict.sh — selftest for the render-quality-scan verdict
# (#1519). Proves the PROCESS exits non-zero on a planted contract departure.
#
# Why this exists as a separate row from the xunit tests in
# Excise.Cli.Tests/RenderQualityVerdictTests.cs: until 2026-09-16 the
# render-quality-scan row of tier `full` — 2h28m, the biggest single row —
# could not go red for ANY rendering defect. Its exit code was
#
#     !strictContracts || report.summary.missingContractPages == 0
#
# so an expectation departure, a release/quality FAIL, a MISSING_CONTENT and an
# EXCISE_SIDE_GAP all exited 0, while the row's own note in tests/gates.tsv
# claimed "--strict-contracts fails on departure". The report already held every
# one of those signals; nothing consulted them.
#
# "A gate nobody has seen fail is not yet a gate" is this project's own rule
# (#1012, the SELFTEST class), and it is precisely what #1519 is about. So this
# script plants each violation and asserts the exit code, through the real CLI.
#
# It exercises `render-quality-classify`, NOT `render-quality-scan`: the two
# share EvaluateRenderingQualityVerdict / ReportRenderingQualityVerdict, and
# classify takes an existing raw report, so the whole selftest needs no corpus,
# no reference renderer and no rasterisation — it runs in about a second
# instead of 2h28m. If you ever give the two commands separate verdict code,
# this selftest stops covering the scan; don't.
#
# It writes only inside a temp directory. It must never touch a tracked file:
# rewriting one, even byte-identically, trips the --no-build freshness guard
# for every later row in the run.

set -uo pipefail
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(dirname "$SCRIPT_DIR")"
CONFIG="${CONFIG:-Debug}"
while [[ $# -gt 0 ]]; do
    case "$1" in
        # The runner substitutes $CONFIG into the target string, so the row
        # passes it as an argument rather than relying on an exported env var.
        # shift is guarded: a bare `--config` with no value would otherwise
        # `shift 2` past the end and loop forever (there is no set -e here).
        --config) CONFIG="${2:-Debug}"; if [[ $# -ge 2 ]]; then shift 2; else shift; fi ;;
        --config=*) CONFIG="${1#*=}"; shift ;;
        --help|-h) sed -n '2,31p' "$0"; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
# An empty --config (the placeholder expanded to nothing) must not silently
# become bin//net10.0 and then read as "binary not found".
CONFIG="${CONFIG:-Debug}"

GREEN=$'\033[32m'; RED=$'\033[31m'; RESET=$'\033[0m'
fails=0
ok()  { printf "  %s✓%s %s\n" "$GREEN" "$RESET" "$1"; }
bad() { printf "  %s✗%s %s\n" "$RED" "$RESET" "$1"; fails=$((fails+1)); }

echo "==> render-quality verdict selftest (#1519)"

# Same binary path convention as scripts/run-exploratory-corpus.sh. No build
# here: tier t0 runs the `build` row first, and building mid-run re-stamps
# shared DLLs so later --no-build rows refuse as stale.
BIN="$ROOT/tools/Excise.RenderTools/bin/$CONFIG/net10.0/Excise.RenderTools"
if [[ ! -x "$BIN" ]]; then
    echo "✗ Excise.RenderTools binary not found at $BIN" >&2
    echo "  Build it first: dotnet build -c $CONFIG tools/Excise.RenderTools/Excise.RenderTools.csproj" >&2
    exit 1
fi

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

write_contract() {
    # write_contract <file> <pdf-path> [expected-raw-status]
    local file="$1" pdf="$2" expected="${3:-PASS}"
    cat > "$file" <<JSON
{
  "Path": "$pdf",
  "Owner": "rendering:quality",
  "RootCause": "SYNTHETIC_FIXTURE",
  "Pages": {
    "1": {
      "ExpectedRawStatus": "$expected",
      "ReleaseStatus": "PASS",
      "QualityStatus": "PIXEL_EXACT",
      "PixelAgreement": "MATCHES_ALL_REQUIRED",
      "ReferenceSituation": "REFS_AGREE",
      "ReviewStatus": "REVIEWED",
      "QualityReason": "Synthetic fixture for the #1519 verdict selftest.",
      "Target": { "Mode": "REFERENCE_CONSENSUS", "Primary": "mutool" }
    }
  }
}
JSON
}

write_raw() {
    # write_raw <file> <entry-json>...
    local file="$1"; shift
    local entries=""
    local sep=""
    local e
    for e in "$@"; do
        entries="$entries$sep$e"
        sep=","
    done
    cat > "$file" <<JSON
{
  "generatedUtc": "2026-09-16T00:00:00.0000000Z",
  "corpus": "synthetic",
  "counts": {},
  "entries": [$entries]
}
JSON
}

entry() {
    # entry <path> <page> <status>
    printf '{"path":"%s","pageNumber":%s,"status":"%s","resultStatus":"PASS",' "$1" "$2" "$3"
    printf '"comparedOracles":3,"agreeingOracles":3,"oracleComparisonPairs":3,"oracleDisagreeingPairs":0}'
}

# run_case <expected-exit> <description> <case-dir-name> <raw-entries...>
#   contracts for the case are written by the caller into $WORK/<dir>/contracts
setup_case() {
    local name="$1"
    CASE_DIR="$WORK/$name"
    mkdir -p "$CASE_DIR/contracts"
}

classify() {
    # classify <extra-args...> ; uses $CASE_DIR
    "$BIN" render-quality-classify "$CASE_DIR/raw.json" \
        --contracts "$CASE_DIR/contracts" \
        --output "$CASE_DIR/quality.json" \
        "$@" >"$CASE_DIR/out.log" 2>&1
}

expect() {
    # expect <want-exit> <description>
    local want="$1" desc="$2" got="$3"
    if [[ "$got" == "$want" ]]; then
        ok "$desc (exit $got)"
    else
        bad "$desc: expected exit $want, got $got"
        sed 's/^/        /' "$CASE_DIR/out.log" | tail -30 >&2
    fi
}

# ---------------------------------------------------------------------------
# 1. The baseline. A clean scan must still pass, or every case below is
#    meaningless: a gate that fails on everything is not a gate either.
# ---------------------------------------------------------------------------
setup_case clean
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 PASS)"
classify --strict-contracts; expect 0 "clean scan passes" "$?"

# ---------------------------------------------------------------------------
# 2. THE bug. A page pinned PASS comes back DIFF. This exited 0 before #1519
#    and is the departure the manifest note already promised to fail on.
# ---------------------------------------------------------------------------
setup_case departure
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 DIFF)"
classify --strict-contracts; expect 1 "planted expectation departure fails" "$?"
if grep -q "synthetic/one.pdf#p1" "$CASE_DIR/out.log"; then
    ok "the failure NAMES the offending page"
else
    bad "the failure did not name the page — a bare count on a 2h28m row is a red people accept"
fi

# ---------------------------------------------------------------------------
# 3. --strict-contracts is what ARMS the verdict, so ad-hoc triage runs
#    (look at some pages, don't gate) still exit 0.
# ---------------------------------------------------------------------------
setup_case departure-unarmed
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 DIFF)"
classify; expect 0 "the same departure is not gated without --strict-contracts" "$?"

# ---------------------------------------------------------------------------
# 4. A scanned page nobody pinned. It cannot depart from anything, so it is
#    invisible to case 2 — this was the row's ONLY pre-#1519 exit term.
# ---------------------------------------------------------------------------
setup_case unpinned
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json" \
    "$(entry synthetic/one.pdf 1 PASS)" "$(entry synthetic/extra.pdf 1 PASS)"
classify --strict-contracts; expect 1 "a scanned page with no contract fails" "$?"

# ---------------------------------------------------------------------------
# 5. Coverage — a pinned page that was never scanned. #1527's lesson: the
#    pages that DID run all matched, which is exactly how "the input set
#    collapsed" reads as a clean sweep.
# ---------------------------------------------------------------------------
setup_case coverage
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_contract "$CASE_DIR/contracts/two.json" "synthetic/two.pdf"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 PASS)"
classify --strict-contracts; expect 1 "a contract page that was never scanned fails" "$?"

# ---------------------------------------------------------------------------
# 6. A scan of nothing is not a passing scan.
# ---------------------------------------------------------------------------
setup_case empty
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json"
classify --strict-contracts; expect 1 "zero scanned pages fails" "$?"

# ---------------------------------------------------------------------------
# 7. EXCISE_SIDE_GAP — an oracle rendered a page excise refused. The one class
#    that is unambiguously an excise defect, so a PIN must not buy it off.
#    CLAUDE.md: pinning it "would have excused an excise-side gap".
# ---------------------------------------------------------------------------
setup_case pinned-gap
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf" "EXCISE_SIDE_GAP"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 EXCISE_SIDE_GAP)"
classify --strict-contracts; expect 1 "an EXCISE_SIDE_GAP fails even when the contract pins it" "$?"

# ---------------------------------------------------------------------------
# 8. The measurement behind the rule: a pinned QualityStatus OVERWRITES the
#    inferred one, so report.failures is empty on a departing page. Gating on
#    `failures` alone — the shape #1519 suggested — would have been a second
#    gate that cannot go red. Asserted here so nobody "simplifies" the verdict
#    down to it later.
# ---------------------------------------------------------------------------
setup_case pin-masks
write_contract "$CASE_DIR/contracts/one.json" "synthetic/one.pdf"
write_raw "$CASE_DIR/raw.json" "$(entry synthetic/one.pdf 1 DIFF)"
classify --strict-contracts
if python3 - "$CASE_DIR/quality.json" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
assert len(d["failures"]) == 0, f"expected failures to be masked by the pin, got {len(d['failures'])}"
assert d["summary"]["expectationFailurePages"] == 1, d["summary"]["expectationFailurePages"]
PY
then
    ok "a pinned QualityStatus masks report.failures; the expectation term is what survives"
else
    bad "the pin/failures relationship changed — re-read the verdict's doc comment before trusting it"
fi

echo
if (( fails > 0 )); then
    echo "✗ render-quality verdict selftest: $fails failure(s)"
    exit 1
fi
echo "✓ render-quality verdict selftest: all cases pass"
