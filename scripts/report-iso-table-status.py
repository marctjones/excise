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

⚠️ `objectKeys` IS NOT THE TABLE'S ENTRY LIST, AND CANNOT BE MADE INTO ONE.
It counts the keys of every Arlington object encoded from the table. For a
table describing one dictionary (Table 349, DocInfo) those coincide and the
row reads naturally. For a table describing something SHARED they diverge
hard: Table 5, "Entries common to all stream dictionaries", defines a handful
of entries but is cited by 33 objects carrying 489 keys between them, so the
row reads 489. Read the column as object scope, never as "the table defines
this many entries".

Attributing per KEY instead was measured and rejected, because it is both
sparser and wrong. Only 693 of 3,973 keys (17.4%) carry a table citation of
their own at all; and of the keys whose own Note cites Table 5, the distinct
names are BitsPerCoordinate, FunctionType, N, Length1 -- which are not Table 5
entries. Arlington's `Note` is a provenance breadcrumb ("this was encoded from
Table N"), not an entry list, and no parse of it yields one. The entry list
lives only in the ISO text, which this repo pins by SHA-256 and does not hold.

⚠️ THE COLUMNS DO NOT PARTITION EITHER. An object is cited by several tables
and a table names several objects, so summing any column double-counts.

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
            "objectKeys": len(keys),
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
        if route != "arlington-object-model":
            # A 0 here would read as a gap. These tables are not measured
            # THROUGH THE OBJECT MODEL at all -- the Annex A operator tables
            # are covered by OperatorParseRecognitionTests, which this join
            # cannot see. Absent evidence is not evidence of absence.
            print(f"   {len(rs):4d}  {route:<24s}    — not measurable via the object model")
            continue
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
    print(f"   {'table':<7s} {'objKeys':>7s} {'expo':>5s} {'mist':>5s} {'unme':>5s}  obligation")
    for r in sorted(sel, key=lambda r: (-r["exposed"], -r["objectKeys"]))[:args.limit]:
        print(f"   {r['table']:<7s} {r['objectKeys']:7d} {r['exposed']:5d} {r['mistyped']:5d} "
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

    print("\nobjKeys counts the keys of every object ENCODED FROM the table, which is not\n"
          "the table's own entry list: Table 5 is cited by 33 objects and reads 489. Read it\n"
          "as object scope. Per-key attribution was measured and is worse — 17.4% of keys\n"
          "carry any citation, and those citing Table 5 name BitsPerCoordinate and\n"
          "FunctionType, which are not Table 5 entries. The entry list is in the ISO text,\n"
          "which this repo pins by hash and does not contain.\n"
          "Columns do not partition: summing any of them double-counts.\n"
          "unmeasured means no corpus document exercised the key — neither implemented nor\n"
          "missing until a fixture exists. No overall percentage is printed, on purpose.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
