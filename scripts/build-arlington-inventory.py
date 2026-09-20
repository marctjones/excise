#!/usr/bin/env python3
"""Derive a PDF 2.0 object-model inventory from the Arlington PDF Model (#1703).

WHY THIS EXISTS. The capability registry's 282 capabilities were migrated from
four of excise's own internal matrices (see legacy-sources.json), so the
denominator is a record of what we previously decided to care about, not a
decomposition of ISO 32000-2. It cannot report what the spec contains that we
have never considered -- and it misses real features in BOTH directions:
object streams (which excise implements) and black point compensation (which
it does not) are equally absent from it.

This file is the opposite: every row is DERIVED, deterministically, from the
Arlington PDF Model at the revision pinned in registry.json. Nothing here is
hand-written, so nothing here can quietly reflect an opinion about scope.
Regenerate it; never edit it.

SCOPE, and its boundary. Arlington models the PDF OBJECT MODEL: dictionaries,
arrays, streams, their keys, types, required-ness and the links between them.
It deliberately does NOT model content-stream operators (ISO 32000-2 Annex A
already enumerates those completely -- 73 of them -- and the registry's
operators section covers them), lexical syntax, file structure, xref and
incremental updates, linearization, or filter/encryption ALGORITHMS. Those
need their own, separately sourced inventories; this one must not be read as
covering them. registry.json's arlingtonPolicy states the same split.

OUTPUT is an inventory, not a score. One row per (object, key) with what the
spec says about it. What excise DOES with each row is observed separately --
see scripts/observe-arlington-keys.py -- because a claim written by hand next
to a requirement written by hand is how the current registry drifted.
"""
from __future__ import annotations

import csv
import json
import re
import sys
from collections import deque
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
REGISTRY = ROOT / "test-pdfs/manifests/pdf-spec-registry/registry.json"
OUT = ROOT / "test-pdfs/manifests/arlington-inventory.json"

# Keys that are security- or privacy-relevant for a redaction tool, and that
# must stay in scope no matter how rare they are in a corpus. This is the ONE
# hand-curated list in this file, and it is deliberately short enough to read
# in one sitting: frequency ranking is the right default, but it makes
# rare-and-catastrophic keys look unimportant, which for this product is
# exactly backwards. A key matching any of these object/key patterns is
# flagged alwaysInScope.
SECURITY_OBJECTS = re.compile(
    r"^(Encryption|Signature|XFA|EmbeddedFile|Filespec|AFFileSpecification|"
    r"StructElem|StructTreeRoot|Metadata|InteractiveForm|Field|Annot|Redact|"
    r"Collection|Action)", re.I)

# Keys that are security-relevant WHEREVER they appear. Deliberately only the
# unambiguous ones: an earlier version included "V", "T" and "Contents", which
# are generic enough to match 3DMeasureAD3.V and flagged 1,182 keys -- too many
# to read, which defeats the purpose of a hand-curated list. Anything
# object-specific is covered by SECURITY_OBJECTS above instead.
SECURITY_KEYS = {
    "Encrypt", "XFA", "EmbeddedFiles", "EF", "AF", "JS", "JavaScript",
    "ActualText", "Alt", "OpenAction", "AA", "URI", "Launch",
    "SubmitForm", "ImportData", "GoToR", "GoToE", "PieceInfo", "Thumb",
}


def pinned_revision() -> str:
    data = json.loads(REGISTRY.read_text(encoding="utf-8"))
    return next(s["revision"] for s in data["sources"] if s["id"] == "arlington-pdf-model")


def parse_links(cell: str) -> list[str]:
    """Object names a key can point at. Arlington writes these as [A,B];[C],
    one bracket group per type in the parallel Type column, and wraps some in
    predicate functions (fn:Deprecated(...)). Names are what we need, so the
    predicates are unwrapped rather than interpreted."""
    return sorted({
        name.strip()
        for group in re.findall(r"\[([^\]]*)\]", cell or "")
        for name in group.split(",")
        if name.strip() and not name.strip().startswith("fn:")
    })


def main() -> int:
    rev = pinned_revision()
    tsv_dir = ROOT / f"test-pdfs/arlington/arlington-pdf-model-{rev}/tsv/2.0"
    if not tsv_dir.is_dir():
        print(f"Arlington model not present at {tsv_dir}\n"
              f"run: scripts/download-arlington-model.sh", file=sys.stderr)
        return 77

    objects: dict[str, list[dict]] = {}
    for path in sorted(tsv_dir.glob("*.tsv")):
        rows = []
        with path.open(encoding="utf-8", newline="") as fh:
            for row in csv.DictReader(fh, delimiter="\t"):
                key = (row.get("Key") or "").strip()
                if not key:
                    continue
                rows.append({
                    "key": key,
                    "type": (row.get("Type") or "").strip(),
                    "sinceVersion": (row.get("SinceVersion") or "").strip(),
                    "deprecatedIn": (row.get("DeprecatedIn") or "").strip(),
                    # Required carries predicate expressions like
                    # fn:IsRequired(fn:SinceVersion(2.0)); kept verbatim rather
                    # than coerced to a boolean we would have to invent.
                    "required": (row.get("Required") or "").strip(),
                    "links": parse_links(row.get("Link", "")),
                })
        objects[path.stem] = rows

    # Reachability from the file trailer. A key reachable only through, say,
    # 3D artwork is real ISO 32000-2 and still noise for this product -- this
    # gives that argument a structural basis instead of an opinion.
    reachable: set[str] = set()
    queue = deque(["FileTrailer"])
    while queue:
        name = queue.popleft()
        if name in reachable or name not in objects:
            continue
        reachable.add(name)
        for row in objects[name]:
            queue.extend(row["links"])

    entries = []
    for obj, rows in objects.items():
        for row in rows:
            entries.append({
                "object": obj,
                **row,
                "newInPdf20": row["sinceVersion"] == "2.0",
                "deprecated": bool(row["deprecatedIn"]),
                "reachableFromTrailer": obj in reachable,
                "alwaysInScope": bool(
                    SECURITY_OBJECTS.match(obj) or row["key"] in SECURITY_KEYS),
            })
    entries.sort(key=lambda e: (e["object"], e["key"]))

    doc = {
        "schemaVersion": 1,
        "generatedBy": "scripts/build-arlington-inventory.py",
        "source": {"id": "arlington-pdf-model", "revision": rev,
                   "license": "Apache-2.0", "modelSubset": "tsv/2.0"},
        "policy": (
            "A DERIVED inventory of the ISO 32000-2 object model: what the spec "
            "contains, never what excise does with it. Status is observed "
            "separately so a requirement and a claim about it are never written "
            "by the same hand. Covers objects/keys only -- content-stream "
            "operators, lexical syntax, file structure, xref, incremental "
            "updates, linearization and filter/encryption algorithms are out of "
            "scope for Arlington and need their own sources."),
        "summary": {
            "objects": len(objects),
            "keys": len(entries),
            "reachableObjects": len(reachable),
            "reachableKeys": sum(1 for e in entries if e["reachableFromTrailer"]),
            "newInPdf20": sum(1 for e in entries if e["newInPdf20"]),
            "deprecated": sum(1 for e in entries if e["deprecated"]),
            "alwaysInScope": sum(1 for e in entries if e["alwaysInScope"]),
        },
        "entries": entries,
    }
    OUT.write_text(json.dumps(doc, indent=1) + "\n", encoding="utf-8")
    s = doc["summary"]
    print(f"wrote {OUT.relative_to(ROOT)}")
    print(f"  {s['keys']} keys across {s['objects']} objects (Arlington @ {rev[:12]})")
    print(f"  reachable from the trailer: {s['reachableKeys']} keys / {s['reachableObjects']} objects")
    print(f"  new in PDF 2.0: {s['newInPdf20']}   deprecated: {s['deprecated']}   "
          f"always-in-scope (security): {s['alwaysInScope']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
