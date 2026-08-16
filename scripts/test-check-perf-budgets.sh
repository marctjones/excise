#!/usr/bin/env bash
#
# Selftest for scripts/check-perf-budgets.sh (#602, per #1012).
#
# The gate's design is deliberately asymmetric and that asymmetry is easy to
# break by accident, so all four branches are pinned here against synthetic
# profile output:
#
#   * ALLOCATION is the HARD gate (machine-stable). Over failRatio on a `hard`
#     workflow in --mode fail must exit 1.
#   * --mode warn reports the same violation and exits 0 — the shared-CI posture.
#     If that ever started failing, CI would be gated on wall-clock noise.
#   * A workflow that ERRORED in every pass is a violation, not a pass.
#   * A missing corpus SKIPS (exit 0) BY DESIGN: this is a performance signal
#     and must never masquerade as a correctness gate. Pinned so nobody
#     "hardens" it into one.
#
# Hermetic: a copy of the gate in $TMP/scripts makes $TMP the repo; a fake
# `dotnet` stands in for the profiler and writes the workflow-profile.json the
# comparison grades. No build, no corpus, no profiling.
#
# Sub-second. Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-perf-budgets.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

R="$TMP/root"
mkdir -p "$R/scripts" "$R/bin" "$R/corpus" "$R/tools/Excise.RenderTools/bin/Release/net10.0"
cp "$SCRIPT" "$R/scripts/check-perf-budgets.sh"
printf '%%PDF-1.7\n' > "$R/corpus/a.pdf"
: > "$R/tools/Excise.RenderTools/bin/Release/net10.0/Excise.RenderTools.dll"

BUDGETS="$R/budgets.json"
cat > "$BUDGETS" <<'JSON'
{
  "schemaVersion": 1,
  "capturedUtc": "2026-08-01T00:00:00Z",
  "commit": "deadbeef",
  "machine": "selftest",
  "workflows": {
    "redaction-save": {
      "gate": "hard",
      "hotPath": "Excise.Core redaction pipeline + writer",
      "baseline": { "totalMs": 1000, "totalAllocatedMB": 100 },
      "perPdf": { "a.pdf": { "ms": 1000, "mb": 100 } }
    }
  }
}
JSON

# Fake profiler. FAKE_ALLOC_MB / FAKE_STATUS drive what it "measured".
cat > "$R/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
out=""
prev=""
for a in "$@"; do
    [[ "$prev" == "--output-dir" ]] && out="$a"
    prev="$a"
done
[[ -z "$out" ]] && exit 0
mkdir -p "$out"
mb="${FAKE_ALLOC_MB:-100}"
status="${FAKE_STATUS:-OK}"
bytes=$(( mb * 1048576 ))
cat > "$out/workflow-profile.json" <<JSON
{ "perPdf": [ { "pdf": "a.pdf", "step": "redaction-save", "status": "$status",
               "elapsedMs": 1000, "allocatedBytes": $bytes } ] }
JSON
exit 0
FAKE
chmod +x "$R/bin/dotnet"

gate() {   # gate <mode> <alloc-mb> [status]
    local mode="$1" mb="$2" status="${3:-OK}"
    rm -rf "$R/out"
    set +e
    OUT="$(PATH="$R/bin:$PATH" CONFIG=Release FAKE_ALLOC_MB="$mb" FAKE_STATUS="$status" \
             "$R/scripts/check-perf-budgets.sh" --no-build --runs 1 \
             --corpus "$R/corpus" --budgets "$BUDGETS" --output-dir "$R/out" \
             --mode "$mode" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-perf-budgets.sh"

# ── 1. Within budget -> pass ────────────────────────────────────────────────
gate fail 100
[[ "$RC" -eq 0 ]] || fail "a measurement at the baseline must pass (exit $RC)
$OUT"
grep -q "within budget" <<<"$OUT" || fail "expected the clean-pass verdict:
$OUT"
echo "    at the baseline                      exit 0"

# ── 2. THE HARD GATE: allocation 2x over on a hard workflow -> FAIL ─────────
gate fail 200
[[ "$RC" -ne 0 ]] || fail "an allocation blowout on a hard workflow must fail in --mode fail
$OUT"
grep -q "^FAIL: redaction-save \[hard\] allocation" <<<"$OUT" \
    || fail "expected an allocation violation naming the workflow and its gate class:
$OUT"
echo "    allocation 2x over, --mode fail      exit $RC"

# ── 3. The same violation in --mode warn -> exit 0, deliberately ───────────
gate warn 200
[[ "$RC" -eq 0 ]] || fail "--mode warn must report and exit 0 (shared-CI posture) — exit $RC
$OUT"
grep -q "mode warn" <<<"$OUT" || fail "the warn-mode run must still say a violation was found:
$OUT"
echo "    same violation, --mode warn          exit 0 (by design)"

# ── 4. A workflow that ERRORED in every pass is a violation ────────────────
gate fail 100 ERROR
[[ "$RC" -ne 0 ]] || fail "a hard workflow that errored in every pass must fail, not be skipped
$OUT"
grep -q "ERRORED" <<<"$OUT" || fail "expected the errored-workflow verdict:
$OUT"
echo "    workflow errored every pass          exit $RC"

# ── 5. A missing corpus SKIPS by design — pinned, not fixed ────────────────
rm -rf "$R/out"
set +e
OUT="$(PATH="$R/bin:$PATH" CONFIG=Release "$R/scripts/check-perf-budgets.sh" --no-build --runs 1 \
         --corpus "$R/no-such-corpus" --budgets "$BUDGETS" --output-dir "$R/out" --mode fail 2>&1)"
RC=$?
set -e
[[ "$RC" -eq 0 ]] || fail "a missing corpus must SKIP: this is a performance signal, not a correctness gate
$OUT"
grep -q "SKIPPED" <<<"$OUT" || fail "expected an explicit SKIPPED line:
$OUT"
echo "    corpus absent                        exit 0 (SKIP, by design)"

echo "==> check-perf-budgets.sh selftest OK"
