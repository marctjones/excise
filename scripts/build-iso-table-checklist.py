#!/usr/bin/env python3
"""Derive an ISO 32000-2 TABLE checklist from Arlington's own ISO mapping (#1709).

WHAT THIS GIVES YOU that arlington-inventory.json does not: the spec's own
table structure, with each table's CAPTION -- which is in practice a one-line
statement of the obligation ("Entries in the catalog dictionary", "Standard
security handler user access permissions"). 438 of them. That is the skeleton
of a clause-level checklist, and it needs no copy of the ISO PDF, which the
repo pins by SHA-256 but does not contain.

THE COLUMN THAT MATTERS MOST is "Reason not included in Arlington PDF Model".
It partitions the 107 tables Arlington does not model onto exactly the sources
that must cover them, and -- the part worth not losing -- it identifies ~30
tables that are "Descriptive only" or "Example only" and are therefore NOT
obligations at all. A checklist that counted those would inflate its
denominator with prose, which is precisely how the existing capability
registry came to report a number nobody should trust.

⚠️ PARSE NOTE, learned the hard way. A naive positional read of the sheet
(take the cells of each row in order) MISALIGNS: blank cells are simply absent
from the XML, so every row with a gap shifts left. The first version of this
read table numbers as object names and reported 333 pairs across 3 distinct
tables, which is nonsense that looks plausible. Column position must come from
each cell's `r` attribute ("C12" -> column 2).

⚠️ THE SPREADSHEET DOES NOT NAME THE ARLINGTON OBJECT. Column A ("Arlington")
looks like it should, and a handoff note asserted it did; it does not. It
echoes the table number -- all 331 modelled rows, verified, the sole non-numeric
value being "F.1", itself a table number. Reading it as an object name yields
object names that are integers, which no downstream join can match and which
therefore fail loudly rather than quietly. Do not re-derive this.

The table -> object link lives instead in the `Note` column of each per-object
TSV in the Arlington model, which cites the ISO table an object was encoded
from ("Table 29", "Table 5 and Table 38 and Clause 7.10.5"). That covers 329 of
the 331 modelled tables.

⚠️ AND IT CITES OTHER STANDARDS' TABLES IN THE SAME FIELD AND THE SAME WORDS.
"ISO 21812-1:2019 Table 20", "ISO/TS 32004 ... Table 2", "Adobe Extension Level
3, Table 8.39b", "Well-Tagged PDF v1.0 Table 1". A bare `Table N` regex
attributes every one of those to ISO 32000-2 -- and `\b` matches the dot in
"8.39b", so it invents a Table 8 as well. That is the positional-parse failure
again in a new place: a wrong answer that looks like a right one. FOREIGN_NOTE
drops the whole note on any foreign designator (fail toward unmapped, never
toward misattributed), and every extracted id is then intersected with the
sheet's own id set, so an escapee cannot reach the output. ASSERTED below: the
intersection must lose nothing.
"""
from __future__ import annotations
import csv, json, re, sys, zipfile
import xml.etree.ElementTree as ET
from collections import Counter, defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / "test-pdfs/manifests/pdf-spec-registry/registry.json"
OUT = ROOT / "test-pdfs/manifests/iso32000-2-table-checklist.json"
NS = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"

# Which source owns a table Arlington does not model. Derived from the
# spreadsheet's own stated reason, so the routing is the PDF Association's
# judgement rather than ours.
ROUTE = [
    ("not-an-obligation", ("descriptive only", "example only", "summarized information only",
                           "descriptive of algorithm", "table content deleted")),
    ("annex-a-operators", ("content stream", "stream content")),
    ("hand-authored-syntax", ("lexical", "internal xrefstream", "semantic processing")),
    ("arlington-gap", ("tbd",)),
    ("arlington-builtin", ("pre-defined arlington",)),
]


# Which issue owns the work for each route, recorded IN THE DATA so the next
# reader of the checklist finds the plan without going to GitHub. Update this
# and regenerate when the plan changes; nothing derives it. Milestone P3.2.
ROUTE_TRACKING = {
    "arlington-object-model": {"issues": [1734, 1735, 1736, 1737, 1738, 1732],
        "note": "reader: generated fixtures (#1734-#1738); writer: delta check (#1732)"},
    "arlington-builtin": {"issues": [1735], "note": "name/number-tree primitives, exercised by the object-model fixtures"},
    "annex-a-operators": {"issues": [], "note": "covered by existing tests (OperatorParseRecognitionTests, renderer differentials); no open work"},
    "hand-authored-syntax": {"issues": [1740], "note": "hand-authored fixtures"},
    "arlington-gap": {"issues": [1741], "note": "decision: FDF, linearization, Annex L structure elements"},
    "unrouted": {"issues": [1741], "note": "decision: ICC (Tables 66-68) and Metadata (Table 348)"},
    "not-an-obligation": {"issues": [], "note": "descriptive or example tables; excluded from every denominator"},
}


def column(ref: str) -> int:
    n = 0
    for ch in re.match(r"([A-Z]+)", ref).group(1):
        n = n * 26 + (ord(ch) - 64)
    return n - 1


def route(reason: str) -> str:
    low = (reason or "").lower()
    for name, needles in ROUTE:
        if any(n in low for n in needles):
            return name
    return "unrouted"


# ---------------------------------------------------------------------------
# table -> Arlington object, from the per-object TSV `Note` column.
#
# ⚠️ THE OBVIOUS CHECK HERE CANNOT FAIL, AND WAS WRITTEN AND DISCARDED.
# The first version denylisted foreign source names and then asserted that no
# extracted id fell outside the sheet's own id set. It passed every mutation:
# delete the ISO branch and it still exits 0, quietly adding six mappings.
# Of course it does -- "ISO 21812-1:2019 Table 20" extracts the id "20", and
# 20 IS an ISO 32000-2 table, so it never escapes the valid set. The ids a
# denylist leak actually produces are 1-29 and 118, every one of them real.
# The dangerous case is invisible to a check that looks only at the output.
#
# So the classification is an ALLOWLIST on the text PRECEDING each citation,
# and the check is a pin on what that allowlist REJECTS. A citation belongs to
# ISO 32000-2 only if everything before it in the note is connective tissue --
# nothing, another Table citation, a Clause reference, a cell name, "see",
# "and". A qualifier naming any other document ("ISO 21812-1:2019 ",
# "Well-Tagged PDF v1.0 ") does not match, so the citation is dropped.
#
# That fails in BOTH directions, which is the point:
#   - loosen the grammar and a pinned rejection stops being rejected  -> FAIL
#   - a new Arlington revision cites a new foreign standard           -> FAIL
# A new standard is a thing a human must look at, not a thing to absorb.
# (?!\.\d) so "Table 8.39b" cannot yield the id 8 if it ever appears behind a
# benign prefix. The prefix pin covers today's data; this covers what it cannot.
TABLE_REF = re.compile(r"\bTable\s*([0-9]+[A-Za-z]?|F\.[0-9]+)(?!\.\d)\b")

BENIGN_PREFIX = re.compile(r"""^(?: \s | , | ; | \. | and | or | see | Text\ below | from
    | Table\s*(?:[0-9]+[A-Za-z]?|F\.[0-9]+)
    | Clause\s*[0-9][0-9.]*
    | [A-Za-z_][A-Za-z0-9_]*\ cell )*$""", re.X | re.I)

# Every prefix the grammar rejects, in Arlington fe4a1a88. Each names a
# document that is not ISO 32000-2, so each rejection is correct. The last is
# a "(same as Table 118)" aside inside a URL: 118 is a real 32000-2 table, but
# the note is about another object's dictionary, and dropping it costs no
# mapping (Table 118 is cited plainly elsewhere).
# Two prefixes that belong on this list are NOT on it, and the reason is the
# TABLE_REF guard above: "Adobe Extension Level 3, Table 8.39b" and "see Adobe
# PDF 1.7 reference, Table 8.103" cite DOTTED ids, which no longer tokenise at
# all, so there is no citation left for a prefix to reject. Rejecting them
# earlier and on their own shape is stronger than rejecting them by who wrote
# them. Adding the guard made this pin fire, which is the pin working.
EXPECTED_FOREIGN_PREFIXES = (
    "Adobe _Digital Signature Build Dictionary Specification_",
    "Adobe _Digital Signature Build Dictionary Specification_ Table 2 and",
    "ISO 19593-1",
    "ISO 19593-1 Table 2 and",
    "ISO 21812-1:2019",
    "ISO 21812-1:2019,",
    "ISO/TS 32004 Integrity protection, clause 5.2.3,",
    "Well-Tagged PDF v1.0",
    "Well-Tagged PDF v1.0 Table 1 and",
    "https://github.com/pdf-association/pdf-issues/issues/462 (same as",
)


def table_to_objects(tsv_dir: Path) -> tuple[dict[str, list[str]], set[str]]:
    """Map ISO 32000-2 table id -> the Arlington objects encoded from it.

    Returns the mapping and the set of distinct rejected prefixes, which the
    caller compares against EXPECTED_FOREIGN_PREFIXES.
    """
    mapping: dict[str, set[str]] = defaultdict(set)
    rejected: set[str] = set()
    for path in sorted(tsv_dir.glob("*.tsv")):
        with path.open(newline="", encoding="utf-8") as fh:
            for row in csv.DictReader(fh, delimiter="\t"):
                note = (row.get("Note") or "").strip()
                if not note or "able" not in note:
                    continue
                for m in TABLE_REF.finditer(note):
                    prefix = note[:m.start()].strip()
                    if BENIGN_PREFIX.fullmatch(prefix):
                        mapping[m.group(1)].add(path.stem)
                    else:
                        rejected.add(prefix)
    return {k: sorted(v) for k, v in mapping.items()}, rejected


def main() -> int:
    rev = next(s["revision"] for s in json.loads(REGISTRY.read_text())["sources"]
               if s["id"] == "arlington-pdf-model")
    xlsx = ROOT / f"test-pdfs/arlington/arlington-pdf-model-{rev}/Arlington-vs-ISO32K-Tables.xlsx"
    if not xlsx.is_file():
        print(f"missing {xlsx}\nrun: scripts/download-arlington-model.sh", file=sys.stderr)
        return 77

    z = zipfile.ZipFile(xlsx)
    strings = [
        "".join(t.text or "" for t in si.iter(NS + "t"))
        for si in ET.fromstring(z.read("xl/sharedStrings.xml"))
    ]
    rows = []
    for row in ET.fromstring(z.read("xl/worksheets/sheet1.xml")).iter(NS + "row"):
        cells = {}
        for c in row.iter(NS + "c"):
            v = c.find(NS + "v")
            if v is None:
                continue
            cells[column(c.get("r"))] = strings[int(v.text)] if c.get("t") == "s" else (v.text or "")
        rows.append(cells)

    # tsv/2.0 -- the same subset build-arlington-inventory.py reads. tsv/latest
    # carries 2 objects the inventory does not, and the join would then name
    # objects that have no keys.
    tsv_dir = ROOT / f"test-pdfs/arlington/arlington-pdf-model-{rev}/tsv/2.0"
    if not tsv_dir.is_dir():
        print(f"missing {tsv_dir}", file=sys.stderr)
        return 77
    mapping, rejected = table_to_objects(tsv_dir)
    if rejected != set(EXPECTED_FOREIGN_PREFIXES):
        unexpected = sorted(rejected - set(EXPECTED_FOREIGN_PREFIXES))
        vanished = sorted(set(EXPECTED_FOREIGN_PREFIXES) - rejected)
        print("THE FOREIGN-CITATION PIN NO LONGER HOLDS.", file=sys.stderr)
        if unexpected:
            print("  A Note cites a table of a document this script has not been told\n"
                  "  about. Read each one: if it is not ISO 32000-2, add it to\n"
                  "  EXPECTED_FOREIGN_PREFIXES; if it IS, widen BENIGN_PREFIX.\n"
                  + "".join(f"    {p!r}\n" for p in unexpected), file=sys.stderr)
        if vanished:
            print("  A prefix that WAS rejected no longer is, so citations belonging to\n"
                  "  another standard are now being attributed to ISO 32000-2:\n"
                  + "".join(f"    {p!r}\n" for p in vanished), file=sys.stderr)
        return 1

    entries = []
    for r in rows[1:]:
        caption = r.get(2, "").strip()
        if not caption:
            continue
        m = re.match(r"Table\s+(\S+?)\s*[-–]\s*(.*)", caption)
        # The sheet ends with two prose summary rows ("... total target Table in
        # ISO 32000-2:2020 coverage of ..."). They carry a caption but are not
        # tables, and a derived artifact that ships them invites someone to
        # count them.
        if not m:
            continue
        modelled = bool(r.get(0, "").strip())
        reason = r.get(3, "").strip()
        entries.append({
            "table": m.group(1),
            "obligation": m.group(2).strip(),
            "caption": caption,
            "inArlington": modelled,
            "reasonNotInArlington": reason or None,
            "coveredBy": "arlington-object-model" if modelled else route(reason),
            # [] on a modelled table is a real finding, not an absence of data:
            # Arlington claims the table is encoded, yet no TSV Note cites it.
            "arlingtonObjects": mapping.get(m.group(1), []),
        })

    counts = Counter(e["coveredBy"] for e in entries)
    unmapped = sorted(e["table"] for e in entries
                      if e["coveredBy"] == "arlington-object-model" and not e["arlingtonObjects"])
    doc = {
        "schemaVersion": 1,
        "generatedBy": "scripts/build-iso-table-checklist.py",
        "source": {"id": "arlington-pdf-model", "revision": rev,
                   "file": "Arlington-vs-ISO32K-Tables.xlsx", "license": "Apache-2.0"},
        "policy": (
            "The ISO 32000-2 table inventory with each table's caption as its obligation "
            "summary, routed to the source that must cover it. DERIVED -- regenerate, never "
            "edit. 'not-an-obligation' rows are descriptive or example tables and must be "
            "excluded from any conformance denominator."),
        "summary": {
            "tables": len(entries),
            "coveredBy": dict(sorted(counts.items())),
            "tracking": ROUTE_TRACKING,
            "arlingtonObjectsNamed": sum(1 for e in entries if e["arlingtonObjects"]),
            "modelledButNoObjectCitesTheTable": unmapped,
        },
        "entries": entries,
    }
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(entries)} ISO 32000-2 tables")
    for k, v in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"   {v:4d}  {k}")
    named = sum(1 for e in entries if e["arlingtonObjects"])
    print(f"   table -> Arlington object: {named} tables named, via the TSV Note column")
    if unmapped:
        print(f"   modelled but no TSV Note cites them: {', '.join(unmapped)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
