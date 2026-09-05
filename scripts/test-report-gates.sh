#!/usr/bin/env bash
#
# SELFTEST for scripts/report_gates.py — the reducer that decides every runner's
# exit code (row report-gates-selftest, t0). A reducer whose failure branches were
# never seen red is #1012's shape, so every check below pins a verdict AND its
# contrast: a reducer that classified the case the other way fails the check.
#
# Hermetic, no dotnet, < 2 s. Two parts:
#
#   python3 scripts/report_gates.py --selftest   every verdict rule, IN-PROCESS.
#     Builds a temp root — <root>/tests/gates.tsv (a synthetic 12-row manifest with
#     the same 13-column header), <root>/logs/test-tier_t0_*/ plan.tsv + ledger.jsonl
#     + trx + logs, <root>/logs/runner-state/known-issues/ (the .rec memory) and a
#     fake `gh` first on PATH (OPEN for #2, CLOSED for #1, "Could not resolve to an
#     Issue" for any other N, exit 1 under GH_FAKE_OFFLINE=1) — then runs every case
#     through main() in one python process. One start instead of ~30 is what keeps
#     the whole selftest under 2 s on a loaded machine; a FAIL dumps the case's
#     output, stderr, plan and ledger to stderr so a flake is diagnosable from its
#     own output. The reducer is pointed at the temp root the same way a caller
#     would be: EXCISE_GATES_ROOT=<root> makes it read <root>/tests/gates.tsv,
#     <root>/logs/runner-state and every other repo-relative artifact under <root>
#     (so every GRADE reads NO DATA there — not what this test pins).
#
#   this script                                  the two checks that need the real thing:
#     (11) the tooling invariant against the REAL repo, and (14) the real wrapper
#     called from another cwd.
#
# Checks (each PASS/FAIL on one line; exit 1 on any FAIL):
#   (1)  a FAIL with no knownIssue is NEW, exit 1; an all-PASS run is bare 'VERDICT PASS', exit 0;
#        the header prints exactly sha7
#   (2)  a FAIL citing '#2' (OPEN) is KNOWN, exit 0; gh is asked once per distinct issue;
#        footer 'gh reachable, k checked'; the .rec is written with its sentinel
#   (3)  '#1' CLOSED on a PASSING row is STALE, exit 1 — and on a failing row too, never KNOWN;
#        '#99' (gh: no such issue) is INVALID, exit 1, remembered as state=INVALID so it still
#        fails offline — a cite naming no issue can never expire, so it never launders a red
#   (4)  '#2/SomeClass': every failed test inside SomeClass -> KNOWN; one outside -> NEW naming it;
#        a trx with zero failed tests can never launder a FAIL into KNOWN
#   (5)  a plan row with no ledger row is NOT RUN, exit 3; planned<of prints PARTIAL; a NOT RUN row
#        citing CLOSED #1 is STALE (exit 1 beats exit 3)
#   (6)  a SKIPPED row reads 'PASS with 1 SKIPPED' with its reason, exit 0; a SKIPPED row citing
#        CLOSED #1 is STALE
#   (7)  a GRADE row that FAILs / has NO_RESULT is 'NO DATA', exit 0; a NO_RESULT on ANY class is
#        printed in the grade block (never silently dropped); a GRADE or NO_RESULT row citing
#        CLOSED #1 is STALE — the expiry check runs on EVERY status
#   (8)  offline (GH_FAKE_OFFLINE=1): a .rec remembering CLOSED keeps #1 STALE, exit 1; no .rec ->
#        'unverified' KNOWN exit 0; a torn .rec (no sentinel) is not trusted; a blank line after
#        the sentinel is; --no-gh never calls gh
#   (9)  the summary never exceeds 20 lines (30 failing rows) and says '+N more (--full)'
#   (10) report.json is written; a sibling run of the same tier prints '(=)' for an unchanged
#        IMPROVE number, '(Δ +1)' for a moved one, another tier is '(no prior)'; re-reporting the
#        OLDEST run never diffs against a sibling that finished later
#   (11) tooling invariant against the REAL repo: every scripts/*.sh|*.py|*.ps1 is the first
#        'scripts/…' word of some row's target in tests/gates.tsv or listed in tests/gates-tooling.txt
#        (no exemptions — this file, report-gates.sh and report_gates.py are accounted for there)
#   (12) a LEGACY plan (no header, 4 columns) reads as tier=full; class/knownIssue come from the
#        manifest, Foo.chunkNN resolves to Foo; the STALE sweep is PLAN-scoped (a manifest-only
#        cite is neither swept nor asked of gh); a planned row the manifest no longer declares is
#        judged by its status (FAIL -> NEW), never demoted to informational
#   (13) the plan header: only= may hold spaces (still tier t0, PARTIAL); a '# tier=' line that
#        does not parse is an unreadable plan, exit 2 — never a silent legacy full-tier read
#   (14) the real wrapper resolves a relative LOG_DIR against the CALLER's cwd (no cd before exec)
#   (15) a torn last ledger line reads NOT RUN, says 'torn' in the detail and in report.json
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PY="$ROOT/scripts/report_gates.py"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/report-gates-selftest.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
FAILS=0

ok()  { printf 'PASS  %s\n' "$1"; }
bad() { printf 'FAIL  %s%s\n' "$1" "${2:+ — $2}"; FAILS=$((FAILS + 1)); }

# ---------------------------------------------------------------------------
# (11) tooling invariant against the REAL repo: two sources, no carve-outs.
# ---------------------------------------------------------------------------
find "$ROOT/scripts" -maxdepth 1 -type f \( -name '*.sh' -o -name '*.py' -o -name '*.ps1' \) | sed "s#^$ROOT/##" | sort -u > "$TMP/scripts.txt"
awk -F'\t' '/^#/ || /^[ \t]*$/ { next } !h { h = 1; next } { n = split($5, w, " "); for (i = 1; i <= n; i++) if (w[i] ~ /^scripts\//) { print w[i]; break } }' \
  "$ROOT/tests/gates.tsv" | sort -u > "$TMP/targets.txt"
awk '{ sub(/#.*/, ""); gsub(/^[ \t]+|[ \t]+$/, ""); if ($0 != "") print }' "$ROOT/tests/gates-tooling.txt" 2>/dev/null | sort -u > "$TMP/tooling.txt"
sort -u "$TMP/targets.txt" "$TMP/tooling.txt" > "$TMP/accounted.txt"
STRAY="$(comm -23 "$TMP/scripts.txt" "$TMP/accounted.txt" | tr '\n' ' ')"
if [ -z "$STRAY" ]; then ok "(11) every scripts/*.sh|*.py|*.ps1 is a gate row's target or listed in tests/gates-tooling.txt"; else bad "(11) stray script(s) neither a gate row's target nor in tests/gates-tooling.txt" "$STRAY"; fi
NT="$(wc -l < "$TMP/targets.txt" | tr -d ' ')"
if [ "$NT" -gt 10 ]; then ok "(11) the check saw the real manifest"; else bad "(11) the check saw the real manifest" "only $NT targets"; fi
for f in scripts/report-gates.sh scripts/report_gates.py scripts/test-report-gates.sh; do
  if grep -qx "$f" "$TMP/accounted.txt"; then ok "(11) $f is accounted for like any other script"; else bad "(11) $f is accounted for like any other script" "not a row target and not in tests/gates-tooling.txt"; fi
done

# ---------------------------------------------------------------------------
# (14) the real wrapper, called from another cwd with a RELATIVE log dir.
# A legacy one-row plan under an empty root (no manifest: the row is judged by
# its PASS status) — the reducer must find it from the caller's cwd, exit 0.
# ---------------------------------------------------------------------------
W="$TMP/wrapper-root"; D="$W/logs/test-tier_t0_20260905010000"; mkdir -p "$D"
printf 'alpha\tscript\tscripts/alpha.sh\t-\n' > "$D/plan.tsv"
printf '{"name":"alpha","status":"PASS","rc":0,"durationSeconds":1,"sha":"0123456789abcdef0123456789abcdef01234567","treeDirty":"no","config":"Debug","recorded":"2026-09-05T01:00:00Z","kind":"script","log":"%s/alpha.log"}\n' "$D" > "$D/ledger.jsonl"
printf 'ok\n' > "$D/alpha.log"
( cd "$W/logs" && EXCISE_GATES_ROOT="$W" "$ROOT/scripts/report-gates.sh" test-tier_t0_20260905010000 --no-gh > "$TMP/wrapper.out" 2> "$TMP/wrapper.err" )
WRC=$?
if [ "$WRC" -eq 0 ] && grep -q '^VERDICT PASS (exit 0)' "$TMP/wrapper.out"; then ok "(14) the wrapper resolves a relative LOG_DIR against the caller's cwd"; else bad "(14) the wrapper resolves a relative LOG_DIR against the caller's cwd" "exit $WRC: $(tr '\n' ' ' < "$TMP/wrapper.err" | cut -c1-160)"; fi

# ---------------------------------------------------------------------------
# every verdict rule, in-process (one python start)
# ---------------------------------------------------------------------------
python3 "$PY" --selftest
PYRC=$?
[ "$PYRC" -eq 0 ] || FAILS=$((FAILS + 1))

if [ "$FAILS" -gt 0 ]; then printf 'report-gates selftest: FAILED (%s bash check(s) failed, python --selftest exit %s)\n' "$FAILS" "$PYRC"; exit 1; fi
printf 'report-gates selftest: all checks passed\n'
