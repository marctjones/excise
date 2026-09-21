#!/usr/bin/env python3
"""What excise does with the ISO 32000-2 object model: done / partial / not done (#1709).

Joins the DERIVED spec inventory (build-arlington-inventory.py) with the
OBSERVED behaviour (ArlingtonKeyObservationTests) and prints a status per key.
There is deliberately no percentage: a score over a denominator this uneven
tells you nothing useful, and the question is which keys, not how many.

The four states, and the distinction that matters most:

  exposed      a document in the corpus carried the key and excise's parser
               surfaced it with a spec-compatible type.
  MISTYPED     surfaced with the wrong kind. A defect.
  NOT EXPOSED  a document carried the key and excise did not surface it.
  unmeasured   no corpus document carried it. NOT a pass and NOT a failure --
               we simply have not asked. Reported separately and never folded
               into either column, because a denominator that quietly counts
               unmeasured things as fine is how the previous registry came to
               report 84.8%.
"""
from __future__ import annotations
import argparse, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
M = ROOT / "test-pdfs/manifests"


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--only", choices=["all", "new20", "security", "gaps"], default="all")
    ap.add_argument("--limit", type=int, default=40)
    args = ap.parse_args()

    inv_path, obs_path = M / "arlington-inventory.json", M / "arlington-observation.json"
    if not inv_path.is_file():
        print("no inventory: run scripts/build-arlington-inventory.py", file=sys.stderr); return 77
    if not obs_path.is_file():
        print("no observation: run scripts/observe-arlington-keys.sh", file=sys.stderr); return 77

    inv = json.loads(inv_path.read_text())
    obs = json.loads(obs_path.read_text())
    seen = {(k["Object"], k["Key"]): k for k in obs["keys"]}

    rows = []
    for e in inv["entries"]:
        o = seen.get((e["object"], e["key"]))
        occ = o["Occurrences"] if o else 0
        mis = o["TypeMismatches"] if o else 0
        state = "MISTYPED" if mis else "exposed" if occ else "unmeasured"
        rows.append(dict(obj=e["object"], key=e["key"], state=state, occ=occ, mis=mis,
                         new20=e["newInPdf20"], sec=e["alwaysInScope"],
                         dep=e["deprecated"], reach=e["reachableFromTrailer"]))

    sel = rows
    if args.only == "new20":   sel = [r for r in rows if r["new20"]]
    elif args.only == "security": sel = [r for r in rows if r["sec"]]
    elif args.only == "gaps":  sel = [r for r in rows if r["state"] != "exposed" and r["reach"]]

    exposed = [r for r in sel if r["state"] == "exposed"]
    mistyped = [r for r in sel if r["state"] == "MISTYPED"]
    unmeasured = [r for r in sel if r["state"] == "unmeasured"]

    print(f"ISO 32000-2 object model — Arlington @ {inv['source']['revision'][:12]}")
    print(f"corpus: {obs['corpus']['directory']} ({obs['corpus']['documents']} documents)")
    print(f"filter: --only {args.only}\n")
    print(f"  exposed by excise      {len(exposed):5d}")
    print(f"  MISTYPED (defect)      {len(mistyped):5d}")
    print(f"  unmeasured (no fixture){len(unmeasured):5d}")
    print(f"  ---------------------- {len(sel):5d} keys in this view\n")
    if mistyped:
        print("MISTYPED — excise surfaced the wrong kind:")
        for r in mistyped: print(f"   {r['obj']}.{r['key']}  ({r['mis']} occurrences)")
        print()
    print(f"exposed, most exercised first (top {args.limit}):")
    for r in sorted(exposed, key=lambda r: -r["occ"])[:args.limit]:
        tag = "".join(["2" if r["new20"] else " ", "S" if r["sec"] else " "])
        print(f"   [{tag}] {r['occ']:6d}  {r['obj']}.{r['key']}")
    print(f"\nunmeasured and reachable from the trailer (top {args.limit} by object):")
    for r in sorted(unmeasured, key=lambda r: (r["obj"], r["key"]))[:args.limit]:
        if not r["reach"]: continue
        tag = "".join(["2" if r["new20"] else " ", "S" if r["sec"] else " "])
        print(f"   [{tag}] {r['obj']}.{r['key']}")
    print("\nlegend: [2]=new in PDF 2.0  [S]=security/privacy relevant (always in scope)")
    print("unmeasured means no corpus document exercised the key — it is neither "
          "implemented nor missing until a fixture exists.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
