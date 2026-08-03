#!/usr/bin/env python3
"""Regenerate test-pdfs/generated-regressions/hybrid-xrefstm-revision-probe.pdf.

A hybrid-reference PDF (PDF 32000-1 §7.5.8.4): a classic xref table, then an
incremental update whose trailer carries /XRefStm pointing at a cross-reference
stream. The stream relocates object 2 — the /Pages node — into an object
stream, changing /MediaBox from [0 0 200 300] to [0 0 200 350].

WHY THIS FIXTURE IS GENERATED AND CHECKED IN
--------------------------------------------
#872 (excise ignored /XRefStm and served a SUPERSEDED revision as current) was
found on a PDFium corpus fixture. That corpus is a gitignored mirror, so a test
that depends on it SKIPS on CI — which meant the gate that exists to catch a
#872 regression would not have run on the machine that blocks merges. The Linux
skip-budget gate caught exactly that.

Allow-listing the skip would have turned the gate green while removing the
coverage. Generating an equivalent fixture keeps both.

The page height is a single-number oracle:
    350  /XRefStm was honoured — the current revision
    300  the parser fell through to /Prev and served the superseded revision

Usage:  python3 scripts/generate-hybrid-xrefstm-fixture.py
"""
import os
import sys

OUT = os.path.join("test-pdfs", "generated-regressions",
                   "hybrid-xrefstm-revision-probe.pdf")


def build() -> bytes:
    parts = []

    def add(s):
        parts.append(s.encode("latin-1") if isinstance(s, str) else s)

    def here():
        return sum(len(p) for p in parts)

    add("%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
    off = {}

    def obj(num, body):
        off[num] = here()
        add(f"{num} 0 obj\n{body}\nendobj\n")

    obj(1, "<< /Type /Catalog /Pages 2 0 R >>")
    # The ORIGINAL /Pages, with the box the update supersedes.
    obj(2, "<< /Type /Pages /MediaBox [0 0 200 300] /Count 1 /Kids [3 0 R] >>")
    obj(3, "<< /Type /Page /Parent 2 0 R /Contents 4 0 R >>")

    content = "q 0 0 1 rg 20 20 50 50 re f Q"
    off[4] = here()
    add(f"4 0 obj\n<< /Length {len(content)} >>\nstream\n{content}\nendstream\nendobj\n")

    xref1 = here()
    add("xref\n0 5\n0000000000 65535 f \n")
    for i in range(1, 5):
        add(f"{off[i]:010d} 00000 n \n")
    add(f"trailer\n<< /Root 1 0 R /Size 5 >>\nstartxref\n{xref1}\n%%EOF\n")

    # ---- incremental update -------------------------------------------------
    # Object 2 is re-issued INSIDE an object stream, so it is reachable only
    # through the cross-reference stream — a classic-xref-only reader cannot
    # see it, which is the whole point of the hybrid form.
    new_pages = "<< /Type /Pages /MediaBox [0 0 200 350] /Count 1 /Kids [3 0 R] >>"
    header = "2 0 "
    objstm = header + new_pages
    off[5] = here()
    add(f"5 0 obj\n<< /Type /ObjStm /N 1 /First {len(header)} /Length {len(objstm)} >>\n"
        f"stream\n{objstm}\nendstream\nendobj\n")

    off[6] = here()

    # /W [1 2 1]: 1-byte type, 2-byte field 2, 1-byte field 3.
    # /Index [2 1 5 2]: one entry for object 2, then two for objects 5 and 6.
    def entry(kind, f2, f3):
        return f"{kind:02X} {f2:04X} {f3:02X}\n"

    xdata = (entry(2, 5, 0)            # obj 2 lives in object stream 5, index 0
             + entry(1, off[5], 0)     # obj 5 at its byte offset
             + entry(1, off[6], 0))    # obj 6 at its byte offset
    add(f"6 0 obj\n<< /Type /XRef /Filter /ASCIIHexDecode /Index [2 1 5 2] "
        f"/Length {len(xdata)} /Prev {xref1} /Root 1 0 R /Size 7 /W [1 2 1] >>\n"
        f"stream\n{xdata}endstream\nendobj\n")

    xref2 = here()
    add("xref\n5 2\n")
    add(f"{off[5]:010d} 00000 n \n{off[6]:010d} 00000 n \n")
    # /XRefStm is the hybrid marker: a reader that understands cross-reference
    # streams must consult it BEFORE following /Prev.
    add(f"trailer\n<< /Prev {xref1} /Root 1 0 R /Size 7 /XRefStm {off[6]} >>\n"
        f"startxref\n{xref2}\n%%EOF\n")

    return b"".join(parts)


def main() -> int:
    if not os.path.isdir(os.path.dirname(OUT)):
        print(f"run from the repo root; {os.path.dirname(OUT)} not found", file=sys.stderr)
        return 1
    data = build()
    with open(OUT, "wb") as f:
        f.write(data)
    print(f"wrote {OUT} ({len(data)} bytes)")
    print("expected: page height 350 (a reader that ignores /XRefStm sees 300)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
