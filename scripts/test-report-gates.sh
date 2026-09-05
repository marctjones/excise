#!/usr/bin/env bash
#
# SELFTEST for scripts/report_gates.py — the reducer that decides every runner's
# exit code (row report-gates-selftest, t0). A reducer whose failure branches were
# never seen red is #1012's shape, so every check below pins a verdict AND its
# contrast: a reducer that classified the case the other way fails the check.
#
# Hermetic. Builds a temp root with:
#   <root>/tests/gates.tsv                     a synthetic 8-row manifest (same 13-column header)
#   <root>/logs/test-tier_t0_*/ …              synthetic plan.tsv + ledger.jsonl + one trx + logs
#   <root>/logs/runner-state/known-issues/     the known-issue memory (.rec files)
#   $TMP/bin/gh                                a fake gh first on PATH: OPEN for #2, CLOSED for #1,
#                                              exit 1 under GH_FAKE_OFFLINE=1
# The reducer is pointed at the temp root with EXCISE_GATES_ROOT=<root>: it then reads
# <root>/tests/gates.tsv, <root>/logs/runner-state and every repo-relative artifact
# under <root> (so every GRADE reads NO DATA here — that is not what this test pins).
# No dotnet; finishes in well under 2 s.
#
# Checks (each PASS/FAIL on one line; exit 1 on any FAIL):
#   (1)  a FAIL with no knownIssue is NEW, exit 1; an all-PASS run is bare 'VERDICT PASS', exit 0
#   (2)  a FAIL citing '#2' (OPEN) is KNOWN, exit 0; gh is asked once per distinct issue
#   (3)  '#1' CLOSED on a PASSING row is STALE, exit 1 — and on a failing row too, never KNOWN
#   (4)  '#2/SomeClass': every failed test inside SomeClass -> KNOWN; one outside -> NEW naming it;
#        a trx with zero failed tests can never launder a FAIL into KNOWN
#   (5)  a plan row with no ledger row is NOT RUN, exit 3; planned<of prints PARTIAL
#   (6)  a SKIPPED row reads 'PASS with 1 SKIPPED' with its reason, exit 0
#   (7)  a GRADE row that FAILs / has NO_RESULT is 'NO DATA', exit 0
#   (8)  offline (GH_FAKE_OFFLINE=1): a .rec remembering CLOSED keeps #1 STALE, exit 1; no .rec ->
#        'unverified' KNOWN exit 0; a torn .rec (no sentinel) is not trusted; --no-gh never calls gh
#   (9)  the summary never exceeds 20 lines (30 failing rows) and says '+N more (--full)'
#   (10) report.json is written; a sibling run of the same tier prints '(=)' for an unchanged
#        IMPROVE number, '(Δ +1)' for a moved one, and another tier is '(no prior)'
#   (11) tooling invariant against the REAL repo: every scripts/*.sh|*.py|*.ps1 is the first
#        'scripts/…' word of some row's target in tests/gates.tsv or listed in tests/gates-tooling.txt
#   (12) a LEGACY plan (no header, 4 columns) reads as tier=full; class/knownIssue come from the
#        manifest, Foo.chunkNN resolves to Foo, and the tier's manifest-only '#N' is still swept
set -uo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PY="$ROOT/scripts/report_gates.py"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/report-gates-selftest.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
FAKE="$TMP/root"
LOGS="$FAKE/logs"
RECS="$LOGS/runner-state/known-issues"
mkdir -p "$FAKE/tests" "$RECS" "$TMP/bin"
FAILS=0
SHA=0123456789abcdef0123456789abcdef01234567
SEQ=0

ok()  { printf 'PASS  %s\n' "$1"; }
bad() { printf 'FAIL  %s%s\n' "$1" "${2:+ — $2}"; FAILS=$((FAILS + 1)); }
# expect <desc> <shell test args...>
expect() { local d="$1"; shift; if [ "$@" ]; then ok "$d"; else bad "$d" "got: $(printf '%s ' "$@")"; fi; }
# expect_grep <desc> <regex> — against $OUT
expect_grep() { if printf '%s\n' "$OUT" | grep -Eq -- "$2"; then ok "$1"; else bad "$1" "no line matches /$2/"; fi; }
expect_nogrep() { if printf '%s\n' "$OUT" | grep -Eq -- "$2"; then bad "$1" "a line matches /$2/"; else ok "$1"; fi; }

# --- fake gh ---------------------------------------------------------------
cat > "$TMP/bin/gh" <<'EOF'
#!/bin/sh
printf '%s\n' "$*" >> "${GH_FAKE_LOG:-/dev/null}"
if [ "${GH_FAKE_OFFLINE:-0}" = 1 ]; then echo "gh: dial tcp: network is unreachable" >&2; exit 1; fi
case "$3" in
  1) echo '{"state":"CLOSED","title":"closed one"}' ;;
  2) echo '{"state":"OPEN","title":"open two"}' ;;
  *) echo "GraphQL: Could not resolve to an Issue with the number of $3" >&2; exit 1 ;;
esac
EOF
chmod +x "$TMP/bin/gh"

# --- synthetic manifest ------------------------------------------------------
M="$FAKE/tests/gates.tsv"
{
  printf '# synthetic manifest for scripts/test-report-gates.sh\n'
  printf 'name\tclass\ttiers\tkind\ttarget\tfilter\tratchet\tknownIssue\tprereq\tprereqPolicy\tcheckpoint\toracle\tnote\n'
  printf 'alpha\tBLOCK\tt0\tscript\tscripts/alpha.sh\t-\t-\t-\t-\tfail\tok\tself\tBLOCK with no knownIssue\n'
  printf 'beta\tBLOCK\tt0\tscript\tscripts/beta.sh\t-\t-\t#2\t-\tfail\tok\tself\tBLOCK citing OPEN #2\n'
  printf 'gamma\tBLOCK\tfull\tscript\tscripts/gamma.sh\t-\t-\t#1\t-\tfail\tok\tself\tBLOCK citing CLOSED #1; full-only so the t0 checks are not swept\n'
  printf 'delta\tBLOCK\tt0\ttest\tSome.Tests/Some.Tests.csproj\tFullyQualifiedName~SomeClass\t-\t#2/SomeClass\t-\tfail\tok\tself\tqualified knownIssue\n'
  printf 'epsilon\tIMPROVE\tt0\tscript\tscripts/epsilon.sh\t-\ttests/epsilon-baseline.tsv\t-\t-\tfail\tok\tself\tIMPROVE with a number in its log\n'
  printf 'zeta\tGRADE\tt0\tscript\tscripts/zeta.sh\t-\t-\t-\t-\tskip\tok\tindependent\tGRADE never blocks\n'
  printf 'eta\tSELFTEST\tt0\tscript\tscripts/test-eta.sh\t-\t-\t-\t-\tfail\tok\tself\tSELFTEST row\n'
  printf 'theta\tBLOCK\tt0\tscript\tscripts/theta.sh\t-\t-\t-\ttool:nonesuch\tskip\tok\tnone\tpolicy=skip row\n'
} > "$M"
T0_ROWS="alpha beta delta epsilon zeta eta theta"

# --- fixture builders --------------------------------------------------------
# mrow <name> — cls/known/kind of a synthetic manifest row (pure bash: no process per lookup)
mrow() {
  case "$1" in
    alpha)   cls=BLOCK;    known=-;            kind=script ;;
    beta)    cls=BLOCK;    known='#2';         kind=script ;;
    gamma)   cls=BLOCK;    known='#1';         kind=script ;;
    delta)   cls=BLOCK;    known='#2/SomeClass'; kind=test ;;
    epsilon) cls=IMPROVE;  known=-;            kind=script ;;
    zeta)    cls=GRADE;    known=-;            kind=script ;;
    eta)     cls=SELFTEST; known=-;            kind=script ;;
    theta)   cls=BLOCK;    known=-;            kind=script ;;
    *)       cls=BLOCK;    known=-;            kind=script ;;
  esac
}
# mkplan <dir> <tier> <planned> <of> <name>... — header + the manifest rows as the 10 plan columns
mkplan() {
  local d="$1" tier="$2" planned="$3" of="$4"; shift 4
  mkdir -p "$d"
  printf '# tier=%s planned=%s of=%s only=- manifest=0123456789abcdef\n' "$tier" "$planned" "$of" > "$d/plan.tsv"
  awk -F'\t' -v want="$*" 'BEGIN { n = split(want, w, " "); for (i = 1; i <= n; i++) order[w[i]] = i }
    /^#/ || /^[ \t]*$/ { next } $1 in order { row[order[$1]] = $1 "\t" $4 "\t" $5 "\t" $6 "\t" $2 "\t" $8 "\t" $9 "\t" $10 "\t" $11 "\t" $7 }
    END { for (i = 1; i <= n; i++) print row[i] }' "$M" >> "$d/plan.tsv"
  : > "$d/ledger.jsonl"
  SEQ=0
}
# led <dir> <name> <status> <rc> [reason] — one ledger row; class/knownIssue/kind from the manifest.
# recorded = 2026-09-05T$REC_HM:0<seq>Z so a fixture can be made newer than another (check 10).
led() {
  local d="$1" name="$2" status="$3" rc="$4" reason="${5:-}" cls known kind extra=""
  mrow "$name"
  [ "$kind" = test ] && extra=",\"trx\":\"$d/$name.trx\""
  [ -n "$reason" ] && extra="$extra,\"reason\":\"$reason\""
  printf '{"name":"%s","status":"%s","rc":%s,"durationSeconds":3,"sha":"%s","treeDirty":"no","config":"Debug","recorded":"2026-09-05T%s:0%sZ","kind":"%s","class":"%s","knownIssue":"%s","log":"%s/%s.log"%s}\n' \
    "$name" "$status" "$rc" "$SHA" "${REC_HM:-01:00}" "$(( SEQ % 10 ))" "$kind" "$cls" "$known" "$d" "$name" "$extra" >> "$d/ledger.jsonl"
  SEQ=$((SEQ + 1))
  case "$status" in
    PASS) printf 'ok\n' > "$d/$name.log" ;;
    *)    printf 'something happened\nFAIL: %s broke\n' "$name" > "$d/$name.log" ;;
  esac
  [ "$name" = epsilon ] && printf '==> no NEW unwired API (%s baselined, all triaged)\n' "${EPSILON_N:-123}" > "$d/epsilon.log"
  return 0
}
# run_t0 <dir> [name:STATUS:rc[:reason]]... — the standard t0 plan, every row PASS unless overridden
run_t0() {
  local d="$1"; shift
  mkplan "$d" t0 7 7 $T0_ROWS
  local n o st rc reason
  for n in $T0_ROWS; do
    st=PASS; rc=0; reason=""
    for o in "$@"; do
      case "$o" in "$n:"*) IFS=: read -r _ st rc reason <<< "$o" ;; esac
    done
    [ "$st" = ABSENT ] && continue
    led "$d" "$n" "$st" "$rc" "$reason"
  done
}
# trx <dir> <name> <failed-test-fqn>... — a minimal trx with those Failed results
trx() {
  local d="$1" name="$2"; shift 2
  {
    printf '<?xml version="1.0" encoding="utf-8"?>\n<TestRun>\n<Results>\n'
    printf '<UnitTestResult executionId="e" testId="t" testName="Some.Tests.SomeClass.Passing" outcome="Passed" />\n'
    local t; for t in "$@"; do printf '<UnitTestResult executionId="e" testId="t" testName="%s" outcome="Failed" />\n' "$t"; done
    printf '</Results>\n<ResultSummary><Counters total="%s" passed="1" failed="%s" /></ResultSummary>\n</TestRun>\n' "$(( $# + 1 ))" "$#"
  } > "$d/$name.trx"
}
# run_report <args...> — sets OUT and RC; gh calls logged to $GH_FAKE_LOG when set
run_report() {
  OUT="$(EXCISE_GATES_ROOT="$FAKE" PATH="$TMP/bin:$PATH" python3 "$PY" "$@" 2> "$TMP/stderr")"
  RC=$?
}
N=0
# nd — next fresh LOG_DIR into $d (no subshell: a $(...) call would never advance N)
nd() { N=$((N + 1)); d="$LOGS/test-tier_t0_2026090501$(printf '%02d' "$N")00"; }

# ===========================================================================
# (1) NEW on a bare FAIL; bare PASS on an all-green run
nd; run_t0 "$d" alpha:FAIL:1; run_report "$d"
expect "(1) bare FAIL -> exit 1" "$RC" -eq 1
expect_grep "(1) bare FAIL -> NEW row" '^NEW +alpha +BLOCK +rc=1'
expect_grep "(1) VERDICT names the NEW count" '^VERDICT FAIL — 1 NEW \(exit 1\)'
nd; run_t0 "$d"; run_report "$d"
expect "(1) all-PASS run -> exit 0" "$RC" -eq 0
expect_grep "(1) all-PASS run reads bare 'VERDICT PASS (exit 0)'" '^VERDICT PASS \(exit 0\)'
expect_nogrep "(1) all-PASS run lists no NEW" '^NEW '

# (2) KNOWN while the cited issue is OPEN; gh asked once per distinct issue
nd; run_t0 "$d" beta:FAIL:1; GH_FAKE_LOG="$TMP/gh.2" run_report "$d"
expect "(2) FAIL citing OPEN #2 -> exit 0" "$RC" -eq 0
expect_grep "(2) row is KNOWN #2 OPEN" '^KNOWN +beta +BLOCK +#2 OPEN'
expect_grep "(2) footer counts the gh check" 'gh reachable, 1 issue checked'
expect "(2) gh asked exactly once for #2" "$(grep -c 'issue view 2 ' "$TMP/gh.2")" -eq 1
expect "(2) .rec written for #2 with sentinel" "$(tail -n1 "$RECS/2.rec" 2>/dev/null)" = "--CKPT-OK--"

# (3) STALE: a CLOSED issue on a passing row, and on a failing row (never KNOWN)
nd; mkplan "$d" full 2 2 alpha gamma; led "$d" alpha PASS 0; led "$d" gamma PASS 0; run_report "$d"
expect "(3) CLOSED #1 on a PASSING row -> exit 1" "$RC" -eq 1
expect_grep "(3) passing row is STALE #1 CLOSED" '^STALE +gamma +BLOCK +#1 CLOSED'
expect_grep "(3) VERDICT reads FAIL — STALE #1" '^VERDICT FAIL — STALE #1 \(exit 1\)'
nd; mkplan "$d" full 2 2 alpha gamma; led "$d" alpha PASS 0; led "$d" gamma FAIL 1; run_report "$d"
expect "(3) CLOSED #1 on a FAILING row -> exit 1, not KNOWN" "$RC" -eq 1
expect_grep "(3) failing row is STALE, not KNOWN" '^STALE +gamma '
expect_nogrep "(3) failing row is not KNOWN" '^KNOWN +gamma '

# (4) qualifier '#2/SomeClass'
nd; run_t0 "$d" delta:FAIL:1; trx "$d" delta Some.Tests.SomeClass.A Some.Tests.SomeClass.B; run_report "$d"
expect "(4) every failed test inside SomeClass -> exit 0" "$RC" -eq 0
expect_grep "(4) row is KNOWN, all match" '^KNOWN +delta +BLOCK +#2 OPEN +2 failed, all match /SomeClass'
nd; run_t0 "$d" delta:FAIL:1; trx "$d" delta Some.Tests.SomeClass.A Some.Tests.OtherClass.Escapee; run_report "$d"
expect "(4) one failed test outside SomeClass -> exit 1" "$RC" -eq 1
expect_grep "(4) row is NEW naming the unmatched test" '^NEW +delta +BLOCK .*OtherClass\.Escapee'
nd; run_t0 "$d" delta:FAIL:1; trx "$d" delta; run_report "$d"
expect "(4) a trx with zero failed tests cannot launder the FAIL -> exit 1" "$RC" -eq 1
expect_grep "(4) zero-failed trx -> NEW (qualifier unverifiable)" '^NEW +delta .*unverifiable'
nd; run_t0 "$d" delta:FAIL:1; run_report "$d"
expect "(4) no trx at all -> exit 1" "$RC" -eq 1
expect_grep "(4) missing trx -> NEW" '^NEW +delta .*no trx'

# (5) NOT RUN and PARTIAL
nd; run_t0 "$d" theta:ABSENT:0; run_report "$d"
expect "(5) plan row without a ledger row -> exit 3" "$RC" -eq 3
expect_grep "(5) row is NOT RUN" '^NOT RUN +theta '
expect_grep "(5) VERDICT reads INCOMPLETE — 1 NOT RUN" '^VERDICT INCOMPLETE — 1 NOT RUN \(exit 3\)'
nd; run_t0 "$d"; sed -i.bak 's/planned=7 of=7/planned=6 of=7/' "$d/plan.tsv"; run_report "$d"
expect_grep "(5) planned<of prints PARTIAL in the header" '^excise gates .* PARTIAL planned 6/7'

# (6) SKIPPED
nd; run_t0 "$d" "theta:SKIPPED:77:SKIPPED tool nonesuch missing"; run_report "$d"
expect "(6) SKIPPED row -> exit 0" "$RC" -eq 0
expect_grep "(6) VERDICT reads 'PASS with 1 SKIPPED'" '^VERDICT PASS with 1 SKIPPED \(exit 0\)'
expect_grep "(6) SKIPPED row carries its reason" '^SKIPPED +theta +BLOCK +policy=skip +SKIPPED tool nonesuch missing'

# (7) GRADE never blocks
nd; run_t0 "$d" zeta:FAIL:1; run_report "$d"
expect "(7) GRADE row FAIL -> exit 0" "$RC" -eq 0
expect_grep "(7) GRADE FAIL reads NO DATA in the grade block" '^  zeta +NO DATA — FAIL rc=1'
expect_grep "(7) GRADE tally reports 0/1" 'GRADE 0/1 reported'
nd; run_t0 "$d" zeta:NO_RESULT:1; run_report "$d"
expect "(7) GRADE row NO_RESULT -> exit 0" "$RC" -eq 0
expect_grep "(7) NO_RESULT reads NO DATA" '^  zeta +NO DATA — NO_RESULT'

# (8) offline: the .rec memory
rm -f "$RECS"/*.rec
nd; run_t0 "$d" beta:FAIL:1; GH_FAKE_OFFLINE=1 run_report "$d"
expect "(8) offline, no .rec: KNOWN unverified -> exit 0" "$RC" -eq 0
expect_grep "(8) offline row reads unverified" '^KNOWN +beta +BLOCK +#2 unverified'
expect_grep "(8) footer says gh unreachable" 'gh unreachable, unverified'
printf 'issue=1\nstate=CLOSED\ntitle=closed one\nverified=2026-09-04T10:00:00Z\n--CKPT-OK--\n' > "$RECS/1.rec"
nd; mkplan "$d" full 2 2 alpha gamma; led "$d" alpha PASS 0; led "$d" gamma PASS 0; GH_FAKE_OFFLINE=1 run_report "$d"
expect "(8) offline with a remembered CLOSED .rec -> STALE, exit 1" "$RC" -eq 1
expect_grep "(8) STALE row shows the remembered verdict" '^STALE +gamma +BLOCK +#1 CLOSED \(remembered 2026-09-04\)'
GH_FAKE_LOG="$TMP/gh.8" run_report "$d" --no-gh
expect "(8) --no-gh still honours the .rec -> exit 1" "$RC" -eq 1
expect "(8) --no-gh never calls gh" "$(cat "$TMP/gh.8" 2>/dev/null | wc -l | tr -d ' ')" -eq 0
expect_grep "(8) --no-gh footer" 'knownIssue verification: --no-gh'
printf 'issue=1\nstate=CLOSED\ntitle=torn\n' > "$RECS/1.rec"
GH_FAKE_OFFLINE=1 run_report "$d"
expect "(8) a torn .rec (no sentinel) is not trusted -> exit 0" "$RC" -eq 0
expect_grep "(8) torn .rec -> unverified KNOWN on the cite (passing row stays PASS)" 'VERDICT PASS \(exit 0\)'
rm -f "$RECS"/*.rec

# (9) the summary never exceeds 20 lines
nd; mkdir -p "$d"
printf '# tier=t0 planned=30 of=30 only=- manifest=0123456789abcdef\n' > "$d/plan.tsv"; : > "$d/ledger.jsonl"
for i in $(seq -w 1 30); do
  printf 'fail%s\tscript\tscripts/fail%s.sh\t-\tBLOCK\t-\t-\tfail\tok\t-\n' "$i" "$i" >> "$d/plan.tsv"
  led "$d" "fail$i" FAIL 1
done
run_report "$d"
expect "(9) 30 failing rows -> exit 1" "$RC" -eq 1
expect "(9) summary is <= 20 lines" "$(printf '%s\n' "$OUT" | wc -l | tr -d ' ')" -le 20
expect_grep "(9) summary says +N more (--full)" '^\+[0-9]+ more \(--full\)'
expect_grep "(9) VERDICT counts all 30" '^VERDICT FAIL — 30 NEW'
run_report "$d" --full
expect "(9) --full prints more than 20 lines" "$(printf '%s\n' "$OUT" | wc -l | tr -d ' ')" -gt 20
expect_grep "(9) --full lists the last row" '^FAIL +NEW +fail30 '

# (10) report.json and Δ vs the newest prior report of the same tier
nd; dA="$d"; REC_HM=02:00 run_t0 "$dA"; run_report "$dA"
expect "(10) report.json written" -s "$dA/report.json"
expect "(10) report.json carries tier/verdict/exit/rows/grades/improve" \
  "$(python3 -c "import json,sys; d=json.load(open(sys.argv[1])); print(all(k in d for k in ('tier','sha','started','finished','verdict','exit','counts','rows','grades','improve')) and d['tier']=='t0' and d['exit']==0)" "$dA/report.json")" = True
expect_grep "(10) first run: IMPROVE number has no prior" 'IMPROVE +held: epsilon 123 baselined \(no prior\)'
nd; dB="$d"; REC_HM=02:01 run_t0 "$dB"; run_report "$dB"
expect_grep "(10) sibling run, unchanged number -> (=)" 'IMPROVE +held: epsilon 123 baselined \(=\)'
nd; dC="$d"; REC_HM=02:02 EPSILON_N=124 run_t0 "$dC"; run_report "$dC"
expect_grep "(10) sibling run, moved number -> (Δ +1)" 'IMPROVE +held: epsilon 124 baselined \(Δ \+1\)'
dD="$LOGS/test-tier_t1_20260905019900"; mkplan "$dD" t1 1 1 epsilon; led "$dD" epsilon PASS 0; run_report "$dD"
expect_grep "(10) another tier has no prior" 'epsilon 123 baselined \(no prior\)'

# (11) tooling invariant against the REAL repo
# The three files of this deliverable are exempt until tests/gates.tsv carries the
# report-gates-selftest row and tests/gates-tooling.txt lists report-gates.sh/report_gates.py.
SELF_EXEMPT='scripts/report-gates.sh
scripts/report_gates.py
scripts/test-report-gates.sh'
find "$ROOT/scripts" -maxdepth 1 -type f \( -name '*.sh' -o -name '*.py' -o -name '*.ps1' \) | sed "s#^$ROOT/##" | sort -u > "$TMP/scripts.txt"
awk -F'\t' '/^#/ || /^[ \t]*$/ { next } !h { h = 1; next } { n = split($5, w, " "); for (i = 1; i <= n; i++) if (w[i] ~ /^scripts\//) { print w[i]; break } }' \
  "$ROOT/tests/gates.tsv" | sort -u > "$TMP/targets.txt"
awk '{ sub(/#.*/, ""); gsub(/^[ \t]+|[ \t]+$/, ""); if ($0 != "") print }' "$ROOT/tests/gates-tooling.txt" 2>/dev/null | sort -u > "$TMP/tooling.txt"
printf '%s\n' "$SELF_EXEMPT" | sort -u > "$TMP/exempt.txt"
sort -u "$TMP/targets.txt" "$TMP/tooling.txt" "$TMP/exempt.txt" > "$TMP/accounted.txt"
STRAY="$(comm -23 "$TMP/scripts.txt" "$TMP/accounted.txt" | tr '\n' ' ')"
if [ -z "$STRAY" ]; then ok "(11) every scripts/*.sh|*.py|*.ps1 is a gate row's target or listed in tests/gates-tooling.txt"; else bad "(11) stray script(s) neither a gate row's target nor in tests/gates-tooling.txt" "$STRAY"; fi
expect "(11) the check saw the real manifest" "$(wc -l < "$TMP/targets.txt" | tr -d ' ')" -gt 10

# (12) legacy plan (no header, 4 columns)
d="$LOGS/full-suite_Debug_20260905_019800"; mkdir -p "$d"
printf 'alpha\tscript\tscripts/alpha.sh\t-\nbeta.chunk01\ttest\tSome.Tests/Some.Tests.csproj\tFullyQualifiedName~X\n' > "$d/plan.tsv"
printf '{"name":"alpha","status":"PASS","rc":0,"durationSeconds":1,"sha":"%s","treeDirty":"yes","config":"Debug","recorded":"2026-09-05T01:00:00Z","kind":"script","log":"%s/alpha.log"}\n' "$SHA" "$d" > "$d/ledger.jsonl"
printf '{"name":"beta.chunk01","status":"FAIL","rc":1,"durationSeconds":1,"sha":"%s","treeDirty":"yes","config":"Debug","recorded":"2026-09-05T01:00:01Z","kind":"test","log":"%s/beta.chunk01.log"}\n' "$SHA" "$d" >> "$d/ledger.jsonl"
printf 'ok\n' > "$d/alpha.log"; printf 'FAIL: chunk broke\n' > "$d/beta.chunk01.log"
run_report "$d"
expect "(12) legacy plan: exit 1 (manifest sweep finds CLOSED #1 on gamma)" "$RC" -eq 1
expect_grep "(12) legacy header reads tier=full" '^excise gates +full @0123456'
expect_grep "(12) beta.chunk01 resolves to beta and is KNOWN #2" '^KNOWN +beta\.chunk01 +BLOCK +#2 OPEN'
expect_grep "(12) manifest-only #1 (gamma, full tier) is swept STALE" '^STALE +gamma +- +#1 CLOSED'
expect_grep "(12) legacy tree flag" '\(tree DIRTY\)'

if [ "$FAILS" -gt 0 ]; then printf 'report-gates selftest: %s check(s) FAILED\n' "$FAILS"; exit 1; fi
printf 'report-gates selftest: all checks passed\n'
