#!/usr/bin/env python3
"""What excise does about each ISO 32000-2 TABLE: the three artifacts joined (#1709).

    iso32000-2-table-checklist.json   the obligation, and who must cover it
  x arlington-inventory.json          the keys each Arlington object declares
  x arlington-observation.json        the keys excise actually surfaced
  = one row per ISO 32000-2 table

THERE IS DELIBERATELY NO OVERALL PERCENTAGE, and this is the whole reason the
artifact chain exists. The capability registry reported 84.8%; an audit of what
its checks actually asserted moved it to 59.3%. A single number over a
denominator this uneven -- 436 tables covering everything from "White-space
characters" to "Standard security handler user access permissions" -- cannot
be wrong in a way anyone would notice. Per-route counts are printed instead.

THE FOUR STATES PER KEY, and the two distinctions that carry the weight:

  exposed      a corpus document carried the key and excise surfaced it with a
               spec-compatible type.
  MISTYPED     surfaced with the wrong kind. A defect. NEVER folded into
               exposed -- a tool that counts its own wrong answers as coverage
               is the self-oracle failure in miniature.
  unmeasured   no corpus document carried the key. Not implemented and not
               missing: NOT ASKED. Never folded into either column.

⚠️ `exposed` IS A PROPERTY OF THE CORPUS, NOT OF THE TABLE. The default corpus
is 10 smoke documents, which surface 135 of 3,973 model keys. A table reading
0 exposed / 40 unmeasured is a statement about our fixtures, not about excise.
The corpus is named in the header for that reason.

⚠️ THE OBSERVER CANNOT SEE "carried but not surfaced". It records what excise
surfaced; a key a document carried and excise dropped is indistinguishable
from a key no document carried. Both land in `unmeasured`. So `unmeasured` is
an upper bound on "not asked", not a measurement of it.

⚠️ KEY COUNTS DO NOT PARTITION. An object is cited by several tables (Table 5,
"Entries common to all stream dictionaries", is cited by every stream object),
and a table names several objects. Summing the keys column double-counts. The
column is a per-row denominator, never a total.

⚠️ THE TABLE VIEW CANNOT SEE EVERY KEY EXCISE SURFACES. Arlington models
objects that ISO 32000-2 does not table at all -- the Adobe _Digital Signature
Build Dictionary Specification_ objects are the live example, and the smoke
corpus exercises 8 of their keys. A per-table view has no row to put them in,
by construction. The footer counts them so the omission is stated rather than
silently absorbed; read them in report-arlington-status.py, which is per-key
and has no such blind spot.

`tests` is null on every row, deliberately. Nothing in the repo maps an ISO
table to the tests that exercise it -- pdf20-operator-evidence.json indexes by
operator and mentions no table. Inventing a keyword match here would
manufacture the kind of citation the #1709 audit was opened to remove.
"""
from __future__ import annotations
import argparse, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
M = ROOT / "test-pdfs/manifests"

# Routes whose tables are NOT obligations. Excluded from every count printed
# here: they are descriptive or example tables, and a denominator that carries
# prose is how the previous registry came to report a number nobody trusts.
NOT_AN_OBLIGATION = "not-an-obligation"


def load(path: Path, hint: str) -> dict:
    if not path.is_file():
        # relative_to() raises on a path outside the repo, which --observation
        # accepts, so a missing file there would crash instead of reporting.
        try:
            shown = path.relative_to(ROOT)
        except ValueError:
            shown = path
        print(f"missing {shown}\n  run: {hint}", file=sys.stderr)
        raise SystemExit(77)
    return json.loads(path.read_text())


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--observation", action="append", metavar="FILE",
                    help="observation manifest; repeatable, unioned. "
                         "Default: arlington-observation.json")
    ap.add_argument("--only", choices=["all", "obligations", "gaps", "unmapped", "mistyped"],
                    default="obligations",
                    help="all: every table incl. non-obligations. obligations: the default. "
                         "gaps: obligations with nothing exposed. unmapped: modelled tables "
                         "no Arlington object cites. mistyped: rows with a defect.")
    ap.add_argument("--limit", type=int, default=40)
    ap.add_argument("--json", action="store_true", help="emit rows as JSON instead of a table")
    args = ap.parse_args()

    checklist = load(M / "iso32000-2-table-checklist.json",
                     "python3 scripts/build-iso-table-checklist.py")
    inventory = load(M / "arlington-inventory.json",
                     "python3 scripts/build-arlington-inventory.py")
    obs_paths = [Path(p) if Path(p).is_absolute() else ROOT / p
                 for p in (args.observation or ["test-pdfs/manifests/arlington-observation.json"])]

    # Union the observations. Occurrences add; a mismatch anywhere is a mismatch.
    seen: dict[tuple[str, str], dict[str, int]] = {}
    corpora = []
    for p in obs_paths:
        o = load(p, "scripts/observe-arlington-keys.sh")
        corpora.append(f"{o['corpus']['directory']} ({o['corpus']['documents']} docs)")
        for k in o["keys"]:
            acc = seen.setdefault((k["Object"], k["Key"]), {"occ": 0, "mis": 0})
            acc["occ"] += k["Occurrences"]
            acc["mis"] += k["TypeMismatches"]

    keys_by_object: dict[str, list[dict]] = {}
    for e in inventory["entries"]:
        keys_by_object.setdefault(e["object"], []).append(e)

    rows = []
    for e in checklist["entries"]:
        objs = e["arlingtonObjects"]
        keys = [k for o in objs for k in keys_by_object.get(o, [])]
        exposed = mistyped = 0
        for k in keys:
            s = seen.get((k["object"], k["key"]))
            if not s:
                continue
            if s["mis"]:
                mistyped += 1
            elif s["occ"]:
                exposed += 1
        rows.append({
            "table": e["table"],
            "obligation": e["obligation"],
            "coveredBy": e["coveredBy"],
            "objects": objs,
            "keys": len(keys),
            "exposed": exposed,
            "mistyped": mistyped,
            "unmeasured": len(keys) - exposed - mistyped,
            "tests": None,
        })

    obligations = [r for r in rows if r["coveredBy"] != NOT_AN_OBLIGATION]
    sel = {
        "all": rows,
        "obligations": obligations,
        "gaps": [r for r in obligations if r["exposed"] == 0],
        "unmapped": [r for r in obligations
                     if r["coveredBy"] == "arlington-object-model" and not r["objects"]],
        "mistyped": [r for r in obligations if r["mistyped"]],
    }[args.only]

    if args.json:
        json.dump({"corpora": corpora, "filter": args.only, "rows": sel},
                  sys.stdout, indent=1)
        print()
        return 0

    rev = checklist["source"]["revision"][:12]
    print(f"ISO 32000-2 table status — Arlington @ {rev}")
    print(f"corpus: {', '.join(corpora)}")
    print(f"filter: --only {args.only}\n")

    print(f"{len(rows)} tables in the spec; {len(rows) - len(obligations)} are descriptive or "
          f"example tables and are NOT obligations.")
    print(f"{len(obligations)} obligations, by the source that must cover them:")
    for route in sorted({r["coveredBy"] for r in obligations}):
        rs = [r for r in obligations if r["coveredBy"] == route]
        ex = sum(1 for r in rs if r["exposed"])
        print(f"   {len(rs):4d}  {route:<24s} {ex:4d} with at least one key exposed")

    mis = [r for r in obligations if r["mistyped"]]
    if mis:
        print("\nMISTYPED — excise surfaced the wrong kind (a defect):")
        for r in mis:
            print(f"   Table {r['table']}: {r['obligation']}  ({r['mistyped']} keys)")

    unmapped = [r for r in obligations
                if r["coveredBy"] == "arlington-object-model" and not r["objects"]]
    if unmapped:
        print("\nmodelled by Arlington, but no object's Note cites the table —")
        print("no object to join through, so nothing here is measurable yet:")
        for r in unmapped:
            print(f"   Table {r['table']}: {r['obligation']}")

    print(f"\nrows, most keys exposed first (top {args.limit}):")
    print(f"   {'table':<7s} {'keys':>5s} {'expo':>5s} {'mist':>5s} {'unme':>5s}  obligation")
    for r in sorted(sel, key=lambda r: (-r["exposed"], -r["keys"]))[:args.limit]:
        print(f"   {r['table']:<7s} {r['keys']:5d} {r['exposed']:5d} {r['mistyped']:5d} "
              f"{r['unmeasured']:5d}  {r['obligation'][:52]}")

    # Keys excise surfaced that live in an object no ISO 32000-2 table cites.
    # A per-table report cannot show these; saying so is the alternative to
    # letting the reader assume the view is total.
    reachable = {(o, k["key"]) for r in rows for o in r["objects"]
                 for k in keys_by_object.get(o, [])}
    orphan = {pair for pair, v in seen.items() if v["occ"] and pair not in reachable}
    if orphan:
        print(f"\n{len(orphan)} keys excise surfaced belong to objects NO ISO 32000-2 table "
              f"cites\n(Arlington models them from other specifications), so no row above "
              f"can show them:\n   " + ", ".join(sorted({o for o, _ in orphan})))

    print("\nkeys/exposed/unmeasured DO NOT SUM across rows: an object is cited by several\n"
          "tables and a table names several objects, so the columns double-count by design.\n"
          "unmeasured means no corpus document exercised the key — neither implemented nor\n"
          "missing until a fixture exists. No overall percentage is printed, on purpose.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
