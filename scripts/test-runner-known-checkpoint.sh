#!/usr/bin/env bash
# Selftest for the #1371 fix: a FAILING step whose failure report-gates.sh
# classified KNOWN must be checkpointed (runner_step_mark_known /
# runner_checkpoint_known_failures, scripts/lib-runner.sh) so a later
# --resume skips it instead of re-running an accepted failure forever.
# Before this fix runner_step_mark only ever wrote a marker for rc=0, so a
# KNOWN row had no marker at all and re-ran on every resume (measured at 2.5h
# for render-quality-scan).
#
# Covers runner_step_should_run's extra checks on a KNOWN marker (which a
# PASS marker does not carry): the manifest's current knownIssue cell must
# still match what was accepted, and the cited issue's .rec cache
# (logs/runner-state/known-issues/<N>.rec, the same file report_gates.py's
# IssueVerifier writes for itself) must say OPEN. Both fail toward RE-RUN.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

fail() { echo "FAIL: $*" >&2; exit 1; }

export RUNNER_ROOT="$WORK" RUNNER_MANIFEST="$WORK/gates.tsv"
# shellcheck source=lib-runner.sh
. "$ROOT/scripts/lib-runner.sh"
export RUNNER_STATE_DIR="$WORK/state" RUNNER_SHA="deadbeef" RUNNER_CONFIG="Debug"
mkdir -p "$RUNNER_STATE_DIR" "$WORK/logs/runner-state/known-issues"

write_manifest() {   # <knownIssue cell for 'flaky'>
    {
        printf 'name\tclass\ttiers\tkind\ttarget\tfilter\tratchet\tknownIssue\tprereq\tprereqPolicy\tcheckpoint\toracle\tnote\n'
        printf 'flaky\tBLOCK\tfull\tscript\tscripts/flaky.sh\t-\t-\t%s\t-\tfail\tok\tself\ttest fixture\n' "$1"
        printf 'never-row\tBLOCK\tfull\tscript\tscripts/never.sh\t-\t-\t#7\t-\tfail\tnever\tself\ttest fixture (checkpoint=never)\n'
    } > "$WORK/gates.tsv"
}

write_rec() {   # <issue N> <state>
    printf 'issue=%s\nstate=%s\ntitle=fixture\nverified=2026-09-05T00:00:00Z\n%s\n' "$1" "$2" "$RUNNER_SENTINEL" \
        > "$WORK/logs/runner-state/known-issues/$1.rec"
}

hash="$(runner_target_hash script scripts/flaky.sh -)"

# --- runner_step_mark_known + runner_step_should_run --------------------

write_manifest "#7"
write_rec 7 OPEN
runner_step_mark_known flaky "$hash" "#7" "$WORK/logs/flaky.log"
runner_step_should_run flaky "$hash" \
    && fail "expected SKIP (marker matches, .rec says OPEN) but should_run said RUN"

write_rec 7 CLOSED
runner_step_should_run flaky "$hash" \
    || fail "expected RE-RUN once .rec says CLOSED, but should_run said SKIP"

write_rec 7 OPEN
runner_step_should_run flaky "$hash" \
    && fail "expected SKIP again once .rec says OPEN"

rm -f "$WORK/logs/runner-state/known-issues/7.rec"
runner_step_should_run flaky "$hash" \
    || fail "expected RE-RUN with no .rec at all, but should_run said SKIP"

write_rec 7 OPEN
write_manifest "#7/narrowed"
runner_step_should_run flaky "$hash" \
    || fail "expected RE-RUN once the manifest's knownIssue cell changed, but should_run said SKIP"

write_manifest "#7"
runner_step_should_run flaky "$hash" \
    && fail "expected SKIP once the manifest cell matches the marker again"

other_hash="$(runner_target_hash script scripts/flaky.sh "-x changed")"
runner_step_should_run flaky "$other_hash" \
    || fail "expected RE-RUN when the target hash no longer matches the marker"

echo "PASS: runner_step_mark_known / runner_step_should_run"

# --- runner_checkpoint_known_failures ------------------------------------

rm -f "$RUNNER_STATE_DIR"/*.ckpt
write_manifest "#7"
write_rec 7 OPEN

log_dir="$WORK/run1"; mkdir -p "$log_dir"
cat > "$log_dir/ledger.jsonl" <<EOF
{"name":"flaky","status":"FAIL","rc":1,"durationSeconds":3,"kind":"script","target":"scripts/flaky.sh","filter":"-"}
{"name":"never-row","status":"FAIL","rc":1,"durationSeconds":3,"kind":"script","target":"scripts/never.sh","filter":"-"}
EOF
cat > "$log_dir/report.json" <<'EOF'
{"rows": [
  {"name": "flaky", "verdict": "KNOWN", "knownIssue": "#7", "log": "/tmp/flaky.log"},
  {"name": "never-row", "verdict": "KNOWN", "knownIssue": "#7", "log": "/tmp/never.log"}
]}
EOF

runner_checkpoint_known_failures "$log_dir"

marker="$(runner_marker_path flaky)"
[ -s "$marker" ] || fail "runner_checkpoint_known_failures wrote no marker for a KNOWN row"
grep -q '^status=KNOWN$' "$marker" || fail "marker missing status=KNOWN: $(cat "$marker")"
grep -q '^knownIssue=#7$' "$marker" || fail "marker missing knownIssue=#7: $(cat "$marker")"
grep -q '^log=/tmp/flaky.log$' "$marker" || fail "marker did not carry the row's evidence log: $(cat "$marker")"
runner_step_should_run flaky "$hash" \
    && fail "expected the freshly written marker to cause a SKIP"

never_marker="$(runner_marker_path never-row)"
[ ! -s "$never_marker" ] || fail "checkpoint=never row got a KNOWN marker; it must always re-run"

echo "PASS: runner_checkpoint_known_failures"
