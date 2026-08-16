#!/usr/bin/env bash
#
# Selftest for scripts/check-contract-manifest-agreement.sh (#977, per #1012).
#
# The gate compares two checked-in descriptions of the same page —
# test-pdfs/rendering-contracts/** and tests/corpus-expectations*.tsv — which
# nothing crossed until #977, at which point 65 pages had silently drifted.
#
# Its normal run over the real trees can only ever print "0 disagreements", so
# the failing branch is never exercised. This drives it over a synthetic
# two-page fixture: one page where the contract and the manifest agree, one
# where they do not. Also pins the two silent-inertness shapes — a wildcard on
# either side is compatible with anything (deliberate, must keep passing), and a
# corpus with no manifest is simply not compared (must not read as agreement).
#
# Costs four `dotnet run` invocations of the already-built RenderTools (~6s on
# this machine, the most expensive gate selftest); no corpus, no renderer.
# Runs in t0.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCRIPT="$ROOT/scripts/check-contract-manifest-agreement.sh"

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
fail() { echo "FAIL: $*" >&2; exit 1; }

C="$TMP/contracts/pdfium"
mkdir -p "$C" "$TMP/repo/tests"
# The vocabulary files the contract loader validates against (it skips '_'
# prefixed files as contracts and reads them as defaults/schema).
cp "$ROOT/test-pdfs/rendering-contracts/_defaults.json" "$TMP/contracts/" 2>/dev/null || true
cp "$ROOT/test-pdfs/rendering-contracts/_schema.json" "$TMP/contracts/" 2>/dev/null || true

contract() {   # contract <name> <expected-raw-status>
    cat > "$C/$1.json" <<JSON
{
  "Path": "pdfium/$1.pdf",
  "Source": "selftest fixture",
  "Owner": "rendering:quality",
  "RootCause": "FULL_CORPUS_BASELINE",
  "ImprovementPriority": "NONE",
  "Confidence": "MEDIUM",
  "Notes": "Synthetic contract for scripts/test-check-contract-manifest-agreement.sh.",
  "Pages": {
    "1": {
      "ExpectedRawStatus": "$2",
      "ReleaseStatus": "PASS",
      "QualityStatus": "PIXEL_EXACT",
      "PixelAgreement": "MATCHES_ALL_REQUIRED",
      "ReferenceSituation": "REFS_AGREE",
      "RootCause": "RAW_PIXEL_MATCH",
      "Target": { "Mode": "REFERENCE_CONSENSUS", "Reason": "selftest fixture" },
      "ImprovementPriority": "NONE",
      "Confidence": "HIGH",
      "QualityReason": "selftest fixture",
      "Notes": "selftest fixture"
    }
  }
}
JSON
}

manifest() {   # manifest <name> <expected-status> [more lines via stdin]
    printf '# selftest manifest\n%s.pdf\t1\t%s\n' "$1" "$2" > "$TMP/repo/tests/corpus-expectations-pdfium.tsv"
}

run() {
    set +e
    OUT="$("$SCRIPT" --contracts "$TMP/contracts" --repo-root "$TMP/repo" 2>&1)"
    RC=$?
    set -e
}

echo "==> selftest: check-contract-manifest-agreement.sh"

# ── 1. Contract and manifest agree -> pass ──────────────────────────────────
contract agreeing PASS
manifest agreeing PASS
run
[[ "$RC" -eq 0 ]] || fail "agreeing descriptions must pass (exit $RC)
$OUT"
grep -q "comparable to a manifest row: 1" <<<"$OUT" \
    || fail "the gate must report that it actually compared the page:
$OUT"
echo "    contract == manifest                 exit 0"

# ── 2. THE GUARDED PROPERTY: they disagree -> FAIL ─────────────────────────
# The #932 shape: a contract stuck at PASS_ONE while the manifest says
# MISSING_CONTENT, indistinguishable from a reviewed pin by reading.
rm -f "$C"/*.json
contract drifted PASS_ONE
manifest drifted MISSING_CONTENT
run
[[ "$RC" -ne 0 ]] || fail "a contract that disagrees with the manifest MUST fail — that is the gate
$OUT"
grep -q "contract=PASS_ONE" <<<"$OUT" || fail "the failure must name both pinned statuses:
$OUT"
echo "    contract != manifest                 exit $RC"

# ── 3. A wildcard on either side stays compatible ──────────────────────────
# Hand-written rows for load-dependent pages. If this started failing, the
# manifests would be "fixed" by deleting deliberate wildcards.
rm -f "$C"/*.json
contract wildcarded PASS_ONE
manifest wildcarded '*'
run
[[ "$RC" -eq 0 ]] || fail "a '*' manifest row is compatible with anything (exit $RC)
$OUT"
echo "    manifest wildcard                    exit 0"

# ── 4. A corpus with no manifest is NOT compared ───────────────────────────
# It must be reported as uncompared, not counted as agreement — one key
# drifting out of the corpus->manifest map would otherwise take hundreds of
# comparisons with it while the totals still looked healthy.
rm -f "$C"/*.json
mkdir -p "$TMP/contracts/no-such-corpus"
cat > "$TMP/contracts/no-such-corpus/orphan.json" <<'JSON'
{
  "Path": "no-such-corpus/orphan.pdf",
  "Source": "selftest fixture",
  "Owner": "rendering:quality",
  "RootCause": "FULL_CORPUS_BASELINE",
  "ImprovementPriority": "NONE",
  "Confidence": "MEDIUM",
  "Notes": "selftest fixture",
  "Pages": { "1": {
      "ExpectedRawStatus": "PASS", "ReleaseStatus": "PASS",
      "QualityStatus": "PIXEL_EXACT", "PixelAgreement": "MATCHES_ALL_REQUIRED",
      "ReferenceSituation": "REFS_AGREE", "RootCause": "RAW_PIXEL_MATCH",
      "Target": { "Mode": "REFERENCE_CONSENSUS", "Reason": "selftest fixture" },
      "ImprovementPriority": "NONE", "Confidence": "HIGH",
      "QualityReason": "selftest fixture", "Notes": "selftest fixture" } }
}
JSON
run
[[ "$RC" -eq 0 ]] || fail "an uncomparable corpus is not a disagreement (exit $RC)
$OUT"
grep -q "no manifest row:         1" <<<"$OUT" \
    || fail "the uncompared page must be COUNTED, not silently folded into agreement:
$OUT"
grep -q "comparable to a manifest row: 0" <<<"$OUT" \
    || fail "expected zero comparisons for a corpus with no manifest:
$OUT"
echo "    corpus with no manifest              exit 0 (counted, not compared)"

echo "==> check-contract-manifest-agreement.sh selftest OK"
