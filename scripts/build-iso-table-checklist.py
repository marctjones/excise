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
"""
from __future__ import annotations
import json, re, sys, zipfile
import xml.etree.ElementTree as ET
from collections import Counter
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
        })

    counts = Counter(e["coveredBy"] for e in entries)
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
        "summary": {"tables": len(entries), "coveredBy": dict(sorted(counts.items()))},
        "entries": entries,
    }
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(entries)} ISO 32000-2 tables")
    for k, v in sorted(counts.items(), key=lambda kv: -kv[1]):
        print(f"   {v:4d}  {k}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
