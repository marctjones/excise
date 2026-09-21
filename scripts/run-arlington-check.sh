#!/usr/bin/env bash
# Run veraPDF's Arlington checker (a container; see download-verapdf-arlington.sh)
# over PDFs and write one machine-readable record per file (#1709).
#
#   scripts/run-arlington-check.sh up                     start the container
#   scripts/run-arlington-check.sh check [opts] PATH...   PATH = file or directory
#   scripts/run-arlington-check.sh down                   stop and remove it
#   scripts/run-arlington-check.sh status
#
#   check options:
#     --profile arlington2.0   arlington1.0 ... arlington1.7, arlington2.0
#     --out DIR                default logs/arlington/<timestamp>
#     --timeout SECONDS        per file, default 300
#
# OUTPUT. DIR/<file>.json is veraPDF's full report; DIR/summary.tsv is one row
# per file: path, result, failedRules, failedChecks, passedRules, seconds, note.
# result is one of
#   COMPLIANT      no rule failed
#   NONCOMPLIANT   at least one rule failed (the file's STRUCTURE breaks the model)
#   PARSE_FAILED   veraPDF's own parser could not open it (see note)
#   ERROR          the service did not answer
# PARSE_FAILED is not "the file is invalid": veraPDF requires a startxref in the
# last 1024 bytes and does not reconstruct a damaged xref, so hand-built files
# like Arlington's own RuleBreaker-INVALID.pdf fail here yet open fine in
# parsers that do recover (qpdf, pdfium). Read it as "this checker could not
# judge it", never as a verdict.
#
# ⚠️ PICK THE PROFILE THAT MATCHES THE FILE. Validating a PDF 1.7 document
# against arlington2.0 reports every 1.x-only feature as "deprecated since PDF
# 2.0" (ProcSet alone is hundreds of checks on an ordinary document). That is
# a true statement about the 2.0 model and a useless one about a 1.7 file. For
# comparing an input with excise's OUTPUT, use the SAME profile on both sides
# and judge the difference.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

PIN=tools/vendor/verapdf-arlington/PINNED
NAME=excise-arlington
PORT="${ARLINGTON_PORT:-18080}"
BASE="http://127.0.0.1:$PORT"

pinned() { grep "^$1=" "$PIN" 2>/dev/null | cut -d= -f2-; }
need_pin() {
  [ -f "$PIN" ] || { echo "not installed: run scripts/download-verapdf-arlington.sh" >&2; exit 77; }
  ENGINE="$(pinned engine)"; REF="$(pinned image)"
}
alive() { curl -fsS --max-time 3 "$BASE/api/info" >/dev/null 2>&1; }

cmd_up() {
  need_pin
  if alive; then echo "already up: $BASE"; return; fi
  "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 || true
  # Bound to loopback only: it accepts arbitrary uploads and has no auth.
  "$ENGINE" run -d --name "$NAME" --platform linux/amd64 \
    -p "127.0.0.1:$PORT:8080" -e JAVA_OPTS=-Xmx1g "$REF" >/dev/null
  echo -n "waiting for $BASE "
  for _ in $(seq 1 60); do alive && { echo " up"; return; }; echo -n .; sleep 3; done
  echo; echo "did not come up in 180s: $ENGINE logs $NAME" >&2; exit 1
}

cmd_down() { need_pin; "$ENGINE" rm -f "$NAME" >/dev/null 2>&1 && echo "removed $NAME" || echo "not running"; }

cmd_status() {
  need_pin; echo "engine=$ENGINE image=$REF"
  if alive; then echo "up: $BASE  profiles: $(curl -fsS "$BASE/api/profiles/ids")"; else echo "down"; fi
}

cmd_check() {
  need_pin
  local profile=arlington2.0 out="" timeout=300 paths=()
  while [ $# -gt 0 ]; do
    case "$1" in
      --profile) profile="$2"; shift 2 ;;
      --out) out="$2"; shift 2 ;;
      --timeout) timeout="$2"; shift 2 ;;
      *) paths+=("$1"); shift ;;
    esac
  done
  [ ${#paths[@]} -gt 0 ] || { echo "usage: check [--profile P] [--out DIR] PATH..." >&2; exit 2; }
  alive || { echo "service is not up; run: $0 up" >&2; exit 77; }
  out="${out:-logs/arlington/$(date +%Y%m%d_%H%M%S)}"
  mkdir -p "$out"
  printf 'path\tresult\tfailedRules\tfailedChecks\tpassedRules\tseconds\tnote\n' > "$out/summary.tsv"

  local files=() p
  for p in "${paths[@]}"; do
    if [ -d "$p" ]; then while IFS= read -r f; do files+=("$f"); done < <(find "$p" -type f -iname '*.pdf' | sort)
    else files+=("$p"); fi
  done
  echo "profile=$profile files=${#files[@]} out=$out"

  local f safe t0 t1 code
  for f in "${files[@]}"; do
    safe="$(printf '%s' "$f" | sed 's|[/ ]|_|g')"
    t0=$(date +%s)
    code=$(curl -sS --max-time "$timeout" -F "file=@$f" "$BASE/api/validate/$profile" \
           -H 'Accept: application/json' -o "$out/$safe.json" -w '%{http_code}' 2>/dev/null || echo 000)
    t1=$(date +%s)
    python3 - "$f" "$out/$safe.json" "$code" "$((t1-t0))" >> "$out/summary.tsv" <<'PY'
import json, sys
path, rep, code, secs = sys.argv[1:5]
def row(result, fr="", fc="", pr="", note=""):
    print("\t".join([path, result, str(fr), str(fc), str(pr), secs, note.replace("\t", " ").replace("\n", " ")[:200]]))
if code != "200":
    row("ERROR", note=f"HTTP {code}"); sys.exit()
try:
    job = json.load(open(rep))["report"]["jobs"][0]
except Exception as e:
    row("ERROR", note=f"unreadable report: {e}"); sys.exit()
if "arlingtonResult" not in job:
    te = job.get("taskException") or {}
    row("PARSE_FAILED", note=te.get("exceptionMessage", "no arlingtonResult")); sys.exit()
d = job["arlingtonResult"][0].get("details", {})
row("COMPLIANT" if job["arlingtonResult"][0].get("compliant") else "NONCOMPLIANT",
    d.get("failedRules", ""), d.get("failedChecks", ""), d.get("passedRules", ""))
PY
    tail -1 "$out/summary.tsv" | awk -F'\t' '{printf "  %-13s rules_failed=%-4s checks_failed=%-6s %4ss  %s\n",$2,$3,$4,$6,$1}'
  done
  echo; echo "summary: $out/summary.tsv"
  awk -F'\t' 'NR>1{c[$2]++} END{for(k in c) printf "  %-13s %d\n",k,c[k]}' "$out/summary.tsv"
}

case "${1:-}" in
  up) cmd_up ;;
  down) cmd_down ;;
  status) cmd_status ;;
  check) shift; cmd_check "$@" ;;
  *) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 2 ;;
esac
