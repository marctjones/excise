#!/usr/bin/env bash
# TOOLING — not a gate (tests/gates-tooling.txt): an A/B of the CLI's render cost between git refs.
# Absolute milliseconds move with machine load, so this prints a verdict only when the difference
# between refs is larger than the spread inside one ref (the credibility rule of
# run-gui-perf-scenarios.sh), and otherwise says BELOW-NOISE instead of calling a winner.
#
# WHY IT EXISTS
#   A reference-performance FAIL names a fixture, not a commit. This builds Excise.Cli at each ref in
#   its own detached worktree and renders ONE page from each in an interleaved order, so that load
#   drift lands on every ref equally. Pass the commit before a change and the change itself to
#   attribute a cost to that one commit; pass three refs to split two commits.
#
# USAGE
#   scripts/ab-render-bench.sh --refs "9bad3644^ 9bad3644 f09f85c2" \
#       [--pdf test-pdfs/pdfjs/bug1755507.pdf] [--page 1] [--dpi 150] [--runs 9] [--keep]
#   Defaults are the nested-forms-dct fixture of tests/reference-performance/fixtures.json.
#   The first ref is the baseline every other ref is compared with.
#
# WHAT IT MEASURES (per run, one fresh process)
#   renderMs  the CLI's in-process render phase (excludes process start, JIT and PNG write; #1387)
#   rssMB     peak resident set of that process (`/usr/bin/time -l`)
#   It also compares the written PNG of each ref with the baseline's, byte for byte, so a speed change
#   that is also a pixel change is visible.
#
# NOT MEASURED HERE: the other renderers' times. Use run-reference-performance-bench.sh --fixture <id>
# for the ratio against the oracles; this script answers "which commit moved excise's own number".
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REFS=""; PDF="test-pdfs/pdfjs/bug1755507.pdf"; PAGE=1; DPI=150; RUNS=9; KEEP=0
while [ $# -gt 0 ]; do
    case "$1" in
        --refs) REFS="$2"; shift 2 ;;
        --pdf) PDF="$2"; shift 2 ;;
        --page) PAGE="$2"; shift 2 ;;
        --dpi) DPI="$2"; shift 2 ;;
        --runs) RUNS="$2"; shift 2 ;;
        --keep) KEEP=1; shift ;;
        -h|--help) sed -n 2,28p "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "unknown argument: $1" >&2; exit 2 ;;
    esac
done
[ -n "$REFS" ] || { echo "--refs is required (two or more git refs, the first is the baseline)" >&2; exit 2; }
read -r -a REF_LIST <<< "$REFS"
[ "${#REF_LIST[@]}" -ge 2 ] || { echo "need at least two refs to compare" >&2; exit 2; }
case "$PDF" in /*) PDF_ABS="$PDF" ;; *) PDF_ABS="$ROOT/$PDF" ;; esac
[ -f "$PDF_ABS" ] || { echo "no such PDF: $PDF_ABS (corpora are gitignored; see scripts/check-test-prereqs.sh)" >&2; exit 2; }

WORK="${AB_BENCH_DIR:-$(mktemp -d "${TMPDIR:-/tmp}/ab-render-bench.XXXXXX")}"
mkdir -p "$WORK"
cleanup() {
    [ "$KEEP" = 1 ] && { echo "kept worktrees and samples under $WORK"; return; }
    for i in "${!REF_LIST[@]}"; do git -C "$ROOT" worktree remove --force "$WORK/ref$i" >/dev/null 2>&1 || true; done
    rm -rf "$WORK"
}
trap cleanup EXIT

# Build every ref first (the slow part), so no build runs between two timed renders.
for i in "${!REF_LIST[@]}"; do
    ref="${REF_LIST[$i]}"
    sha="$(git -C "$ROOT" rev-parse --short "$ref")"
    echo "== building ref$i = $ref ($sha)"
    git -C "$ROOT" worktree add --detach "$WORK/ref$i" "$ref" >/dev/null
    dotnet build "$WORK/ref$i/Excise.Cli/Excise.Cli.csproj" -c Release --nologo -v quiet >"$WORK/build$i.log" 2>&1 \
        || { echo "build failed for $ref; see $WORK/build$i.log" >&2; KEEP=1; exit 1; }
    CLI="$(find "$WORK/ref$i/Excise.Cli/bin/Release" -maxdepth 3 -type f -name excise -perm -u+x | head -1)"
    [ -n "$CLI" ] || { echo "no excise executable for $ref under Excise.Cli/bin/Release" >&2; KEEP=1; exit 1; }
    echo "$CLI" > "$WORK/cli$i"
done

time_cmd() { if /usr/bin/time -l true >/dev/null 2>&1; then echo "macos"; else echo "gnu"; fi; }
TIMER="$(time_cmd)"

# One render: prints "renderMs rssMB".
one_run() {
    local idx="$1" cli out json tlog rss
    cli="$(cat "$WORK/cli$idx")"; out="$WORK/out$idx.png"; tlog="$WORK/time$idx.txt"
    if [ "$TIMER" = macos ]; then
        json="$(/usr/bin/time -l "$cli" render "$PDF_ABS" -o "$out" --page "$PAGE" --dpi "$DPI" --json 2>"$tlog")"
        rss="$(awk '/maximum resident set size/ {printf "%.1f", $1/1048576}' "$tlog")"
    else
        json="$(/usr/bin/time -v "$cli" render "$PDF_ABS" -o "$out" --page "$PAGE" --dpi "$DPI" --json 2>"$tlog")"
        rss="$(awk -F: '/Maximum resident set size/ {printf "%.1f", $2/1024}' "$tlog")"
    fi
    python3 - "$json" "$rss" <<'PY'
import json, sys
d = json.loads(sys.argv[1])
low = {k.lower(): v for k, v in d.items()}
print(f"{low['renderms']:.1f} {sys.argv[2]}")
PY
}

N="${#REF_LIST[@]}"
echo "== warm-up (one discarded render per ref)"
for i in $(seq 0 $((N - 1))); do one_run "$i" >/dev/null; done

echo "== $RUNS interleaved rounds, order rotated each round"
for r in $(seq 1 "$RUNS"); do
    for k in $(seq 0 $((N - 1))); do
        i=$(( (k + r) % N ))
        echo "$(one_run "$i")" >> "$WORK/samples$i.txt"
    done
done

for i in $(seq 1 $((N - 1))); do
    if cmp -s "$WORK/out0.png" "$WORK/out$i.png"; then echo "pixels: ref$i is byte-identical to ref0"
    else echo "pixels: ref$i DIFFERS from ref0 (a pixel change as well as a speed change)"; fi
done

python3 - "$WORK" "$N" "${REF_LIST[@]}" <<'PY'
import statistics as st, sys
work, n, refs = sys.argv[1], int(sys.argv[2]), sys.argv[3:]
def load(i):
    rows = [l.split() for l in open(f"{work}/samples{i}.txt") if l.strip()]
    return [float(r[0]) for r in rows], [float(r[1]) for r in rows]
def spread(xs): return max(xs) - min(xs)
base_ms, base_rss = load(0)
print(f"\n{'ref':<16}{'renderMs median':>16}{'min':>8}{'spread':>8}{'rssMB median':>14}{'vs ref0':>10}  verdict")
for i in range(n):
    ms, rss = load(i)
    med = st.median(ms)
    if i == 0:
        print(f"{refs[i]:<16}{med:>16.1f}{min(ms):>8.1f}{spread(ms):>8.1f}{st.median(rss):>14.1f}{'(base)':>10}")
        continue
    delta = med - st.median(base_ms)
    floor = max(spread(ms), spread(base_ms))
    verdict = "BELOW-NOISE" if abs(delta) <= floor else ("SLOWER" if delta > 0 else "FASTER")
    print(f"{refs[i]:<16}{med:>16.1f}{min(ms):>8.1f}{spread(ms):>8.1f}{st.median(rss):>14.1f}{delta:>+10.1f}  {verdict} (noise floor {floor:.1f} ms)")
PY
