#!/usr/bin/env bash
# Selftest for runner_plan_expand_trx (scripts/lib-runner.sh): a {TRX:x} or
# {TRXARGS:x} consumer must point at the trx that WILL exist — this run's
# LOG_DIR when the producer runs or when --resume transports a checkpoint.
# The first resumed full run (2026-09-05,
# logs/full-suite_Debug_20260905_084349) had test-count-core/cli/avalonia read
# "no trx ... cannot tell which tests reported" because the expansion assumed
# LOG_DIR unconditionally.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

# shellcheck source=lib-runner.sh
RUNNER_ROOT="$ROOT" . "$ROOT/scripts/lib-runner.sh"
export RUNNER_STATE_DIR="$WORK/state"
export RUNNER_SHA=0000000
mkdir -p "$RUNNER_STATE_DIR" "$WORK/earlier" "$WORK/now"

# The earlier run's evidence: a producer trx beside its log.
printf '<TestRun><Results><UnitTestResult outcome="Passed"/></Results></TestRun>\n' > "$WORK/earlier/prod.trx"; : > "$WORK/earlier/prod.log"
printf '<TestRun><Results><UnitTestResult outcome="Passed"/></Results></TestRun>\n' > "$WORK/earlier/big.chunk01.trx"; : > "$WORK/earlier/big.chunk01.log"

plan="$WORK/now/plan.tsv"
write_plan() {
    {
        echo "# tier=t0 planned=5 of=5 only=- manifest=0123456789abcdef"
        printf 'prod\ttest\tSome/Some.csproj\tFullyQualifiedName~A\tBLOCK\t-\t-\tfail\tok\t-\n'
        printf 'fresh\ttest\tSome/Some.csproj\tFullyQualifiedName~B\tBLOCK\t-\t-\tfail\tok\t-\n'
        printf 'big.chunk01\ttest\tBig/Big.csproj\tFullyQualifiedName~C\tBLOCK\t-\t-\tfail\tok\t-\n'
        printf 'big.chunk02\ttest\tBig/Big.csproj\tFullyQualifiedName~D\tBLOCK\t-\t-\tfail\tok\t-\n'
        printf 'cons\tscript\tcount.sh {TRX:prod} {TRXARGS:fresh} {TRXARGS:big}\t-\tBLOCK\t-\t-\tfail\tok\t-\n'
    } > "$plan"
}

mark() {   # <name> <kind> <target> <filter> <log>
    local hash
    hash="$(runner_target_hash "$2" "$3" "$4")"
    {
        echo "name=$1"; echo "sha=0000000"; echo "config=Debug"; echo "rc=0"; echo "duration=1"
        echo "finished=2026-09-05T00:00:00Z"; echo "target=$hash"; echo "log=$5"; echo "$RUNNER_SENTINEL"
    } > "$(runner_marker_path "$1")"
}

# (1) nothing checkpointed: every reference resolves to this run's LOG_DIR
write_plan
runner_plan_expand_trx "$plan" "$WORK/now" || fail "expansion failed with no markers"
grep -q "count.sh $WORK/now/prod.trx --trx $WORK/now/fresh.trx --trx $WORK/now/big.chunk01.trx --trx $WORK/now/big.chunk02.trx" "$plan" \
    || fail "with no checkpoints every trx must be under LOG_DIR: $(grep '^cons' "$plan")"

# (2) eligible producer evidence is copied here from the earlier run.
write_plan
mark prod test Some/Some.csproj "FullyQualifiedName~A" "$WORK/earlier/prod.log"
mark big.chunk01 test Big/Big.csproj "FullyQualifiedName~C" "$WORK/earlier/big.chunk01.log"
runner_plan_expand_trx "$plan" "$WORK/now" || fail "expansion failed with markers"
grep -q "count.sh $WORK/now/prod.trx --trx $WORK/now/fresh.trx --trx $WORK/now/big.chunk01.trx --trx $WORK/now/big.chunk02.trx" "$plan" \
    || fail "checkpoint consumers must use transported evidence: $(grep '^cons' "$plan")"
cmp "$WORK/earlier/prod.trx" "$WORK/now/prod.trx" || fail "transport changed test evidence"
python3 - "$WORK/now/prod.trx.provenance.json" "$WORK/earlier" <<'PY'
import json, sys
p = json.load(open(sys.argv[1]))
assert p['sourceCommit'] == '0000000'
assert p['sourceRun'] == sys.argv[2]
assert len(p['sha256']) == 64
PY

# A different commit, missing TRX, or malformed TRX must execute again.
RUNNER_SHA=another-commit
runner_checkpoint_test_evidence prod test "$WORK/rejected" \
    && fail "cross-commit evidence was accepted"
[ ! -e "$WORK/rejected/prod.trx" ] || fail "cross-commit evidence was published"
RUNNER_SHA=0000000

# (3) missing source evidence rejects reuse; the producer must run here.
write_plan
rm -f "$WORK/earlier/prod.trx"
runner_checkpoint_test_evidence prod test "$WORK/rejected" \
    && fail "missing evidence was accepted"
runner_plan_expand_trx "$plan" "$WORK/now" || fail "expansion failed with a pruned evidence trx"
grep -q "count.sh $WORK/now/prod.trx " "$plan" \
    || fail "a pruned evidence trx must fall back to LOG_DIR: $(grep '^cons' "$plan")"

# (4) a changed row (hash mismatch) is not taken from the checkpoint, so it resolves to LOG_DIR
write_plan
printf 'not XML\n' > "$WORK/earlier/prod.trx"
runner_checkpoint_test_evidence prod test "$WORK/rejected" \
    && fail "malformed evidence was accepted"
printf '<TestRun/>\n' > "$WORK/earlier/prod.trx"
runner_checkpoint_test_evidence prod test "$WORK/rejected" \
    && fail "empty evidence was accepted"
printf '<TestRun><Results><UnitTestResult outcome="Passed"/></Results></TestRun>\n' > "$WORK/earlier/prod.trx"
mark prod test Some/Some.csproj "FullyQualifiedName~CHANGED" "$WORK/earlier/prod.log"
runner_plan_expand_trx "$plan" "$WORK/now" || fail "expansion failed with a stale marker"
grep -q "count.sh $WORK/now/prod.trx " "$plan" \
    || fail "a marker for a different command must not redirect the consumer: $(grep '^cons' "$plan")"

# A source can disappear between planning and execution. The rerun must not
# retain the plan-time TRX or attribution, even if it fails before testing.
runner_prepare_test_evidence prod test "$WORK/now"
[ ! -e "$WORK/now/prod.trx" ] || fail "rerun retained plan-time test evidence"
[ ! -e "$WORK/now/prod.trx.provenance.json" ] || fail "rerun retained checkpoint provenance"
[ -s "$WORK/now/big.chunk01.trx" ] || fail "preparation removed another row's evidence"
runner_prepare_test_evidence big.chunk01 script "$WORK/now"
[ -s "$WORK/now/big.chunk01.trx" ] || fail "non-test preparation removed evidence"

[ -z "$(ls "$WORK/now" | grep -v -E '^(plan.tsv|prod.trx(\.provenance.json)?|big.chunk01.trx(\.provenance.json)?)$')" ] || fail "expansion left temp files: $(ls "$WORK/now")"
echo "PASS: checkpoint TRX transport preserves evidence/provenance and rejects cross-commit, absent, and malformed evidence"
