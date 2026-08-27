#!/usr/bin/env bash
# Show the redaction-benchmark performance history — one row per archived run,
# newest last, so you can see excise's leak rate move as the code + bench evolve.
set -euo pipefail
cd "$(dirname "$0")/.."
H="tests/redaction-bench-history.jsonl"
[ -s "$H" ] || { echo "no history yet ($H) — run a bench + scripts/archive-bench-run.sh"; exit 0; }
python3 - "$H" <<'PY'
import json,sys
rows=[json.loads(l) for l in open(sys.argv[1]) if l.strip()]
print(f"{'date':20} {'excise':22} {'bench':17} {'hard-leak: excise/pymupdf/itext':32}")
print("-"*95)
for r in rows:
    ts=r["timestamp"][:19]
    ex=r["excise"]["describe"][:20]+("*" if r["excise"]["dirty"] else "")
    bench=r["bench"]["manifestSha"][:10]+f"/{r['bench']['realCases']}c"
    ht=r["metrics"]["leakByTierTool"].get("hard",{})
    def cell(t): 
        v=ht.get(t); return f"{v[0]}/{v[1]}" if v else "-"
    hard=f"{cell('excise')} / {cell('pymupdf')} / {cell('itext')}"
    print(f"{ts:20} {ex:22} {bench:17} {hard:32}")
PY
