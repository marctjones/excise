#!/usr/bin/env python3
"""#1134 — ground-truth generator for RC17 de-redaction measurement.

Constructs redaction cases whose answer we KNOW, because we place it. Each case
is a redacted PDF plus a manifest row recording the answer and every generation
parameter. The manifest is the ground truth the recall scorer joins against;
nothing downstream can be trusted more than this generator is honest.

  scripts/gen-redaction-corpus.py --out test-pdfs/redaction-synthetic

Design (see #1134, #1135):
  - CONSTRUCTED ground truth, not collected pairs (none exist, legal minefield).
  - The dictionary a case is drawn from is RECORDED per case, because recall@N
    against a different dictionary measures dictionary coverage, not width
    discrimination -- a silent way to make the number meaningless.
  - excise has NO width-closing redaction mode (GlyphRemovalStrategy is only
    AnyOverlap/FullyContained), so the width-closing negative-control band is
    synthesized HERE, not produced by excise.
  - Widths come from tests/redaction-corpus/std14-widths.json (metric facts
    about the standard 14, same provenance as Excise.Core StandardFontMetrics).
"""
import argparse
import hashlib
import json
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
WIDTHS = json.load(open(os.path.join(ROOT, "tests/redaction-corpus/std14-widths.json")))

# A closed dictionary. Recall is measured against THIS set, recorded per case.
NAMES = ("James John Robert Michael William David Richard Joseph Thomas Charles "
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth "
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen "
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle "
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").split()

FONT_PS = {"Helvetica": "Helvetica", "Times-Roman": "Times-Roman", "Courier": "Courier"}


def text_width_pt(s, font, size):
    """Rendered width in points, exact for the standard 14."""
    w = WIDTHS[font]
    return sum(w[ord(c) - 32] for c in s if 32 <= ord(c) <= 126) / 1000.0 * size


def _pdf(objs):
    """Assemble numbered objects into a PDF with a correct xref."""
    out = bytearray(b"%PDF-1.7\n")
    offs = []
    for i, o in enumerate(objs, 1):
        offs.append(len(out))
        out += str(i).encode() + b" 0 obj\n" + o + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 " + str(len(objs) + 1).encode() + b"\n0000000000 65535 f \n"
    for o in offs:
        out += ("%010d 00000 n \n" % o).encode()
    out += (b"trailer\n<< /Size " + str(len(objs) + 1).encode()
            + b" /Root 1 0 R >>\nstartxref\n" + str(xref).encode() + b"\n%%EOF\n")
    return bytes(out)


def build_case(answer, font, size, method, context):
    """Return (pdf_bytes, meta). The answer sits in a line at a known x-span.

    Layout (points): left margin 72, baseline 700. The prefix is 'Name: ',
    the answer follows, then a suffix so there is surviving text on both sides
    (a redaction with nothing after it leaks nothing about width).
    """
    prefix, suffix = "Name: ", " (on file)"
    x0 = 72.0
    ax0 = x0 + text_width_pt(prefix, font, size)          # answer left edge
    aw = text_width_pt(answer, font, size)                # answer width
    ax1 = ax0 + aw                                         # answer right edge
    ps = FONT_PS[font]

    def line(s):
        return f"BT /F1 {size} Tf {x0} 700 Td ({s}) Tj ET\n".encode()

    ctx = ""
    if context == "rich":
        ctx = (f"BT /F1 {size} Tf {x0} 660 Td (Claimant name and account holder:) Tj ET\n")

    if method == "under-box":
        # Text intact, opaque box painted over the answer. Certain-recoverable.
        content = (line(prefix + answer + suffix)
                   + f"0 0 0 rg\n{ax0} 696 {aw} {size} re f\n".encode()
                   + ctx.encode())

    elif method == "width-preserving":
        # Answer glyphs removed, gap LEFT (what excise's real redaction does),
        # box drawn. Prefix and suffix keep their positions.
        content = (line(prefix)                                      # prefix at x0
                   + f"BT /F1 {size} Tf {ax1} 700 Td ({suffix}) Tj ET\n".encode()  # suffix at its ORIGINAL x
                   + f"0 0 0 rg\n{ax0} 696 {aw} {size} re f\n".encode()
                   + ctx.encode())

    elif method == "width-closing":
        # Answer removed AND suffix shifted left to close the gap. No width
        # channel. NEGATIVE CONTROL: residue recovery must find ~nothing.
        content = (line(prefix + suffix) + ctx.encode())

    elif method == "defended":
        # Width-preserving, but the gap is broken into random sub-widths by
        # decoy fills so no single width encodes the answer. The frontier.
        import struct
        seed = int(hashlib.sha256(answer.encode()).hexdigest()[:8], 16)
        pieces, x = [], ax0
        step = aw / 4
        for k in range(4):
            jitter = ((seed >> (k * 3)) % 7 - 3)          # deterministic ±3pt
            pieces.append(f"0 0 0 rg\n{x} 696 {max(1,step+jitter)} {size} re f\n")
            x += step
        content = (line(prefix)
                   + f"BT /F1 {size} Tf {ax1} 700 Td ({suffix}) Tj ET\n".encode()
                   + "".join(pieces).encode() + ctx.encode())
    else:
        raise ValueError(method)

    objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"endstream",
            f"<< /Type /Font /Subtype /Type1 /BaseFont /{ps} /Encoding /WinAnsiEncoding >>".encode()]

    meta = dict(answer=answer, font=font, sizePt=size, method=method, context=context,
                gapX0=round(ax0, 3), gapX1=round(ax1, 3), gapWidthPt=round(aw, 3),
                dictionary="names-%d" % len(NAMES))
    return _pdf(objs), meta


# (band, font, size, method, context, string-kind)
PLAN = [
    ("B0", "Helvetica",   12, "under-box",        "none", "dict"),
    ("B1", "Helvetica",   12, "width-preserving", "none", "dict"),
    ("B1", "Times-Roman", 12, "width-preserving", "none", "dict"),
    ("B2", "Helvetica",   12, "width-preserving", "none", "dict-long"),
    ("B6", "Helvetica",   12, "width-preserving", "none", "random"),
    ("B7", "Helvetica",   12, "width-preserving", "rich", "dict"),
    ("B8", "Helvetica",   12, "width-closing",    "none", "dict"),
    ("B9", "Helvetica",   12, "defended",         "none", "dict"),
]


def strings_for(kind):
    if kind == "dict":
        return NAMES
    if kind == "dict-long":
        return [n for n in NAMES if len(n) >= 8]
    if kind == "random":
        # Not in the dictionary; width bound with no dict anchor. Deterministic.
        return ["XqmzdR", "KwbfjL", "Pvgnyt", "Zdxkqw"]
    raise ValueError(kind)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="test-pdfs/redaction-synthetic")
    ap.add_argument("--per-band", type=int, default=8,
                    help="cases per (band,font,method) row of the plan")
    args = ap.parse_args()

    out = os.path.join(ROOT, args.out)
    os.makedirs(out, exist_ok=True)
    manifest = []

    for band, font, size, method, context, kind in PLAN:
        pool = strings_for(kind)
        for answer in pool[:args.per_band]:
            cid = f"{band}-{font.split('-')[0].lower()}{size}-{method}-{answer}"
            pdf, meta = build_case(answer, font, size, method, context)
            open(os.path.join(out, cid + ".pdf"), "wb").write(pdf)
            meta.update(id=cid, band=band)
            manifest.append(meta)

    with open(os.path.join(out, "manifest.jsonl"), "w") as f:
        for m in manifest:
            f.write(json.dumps(m) + "\n")

    # A hash over the generation parameters. #1135's scorer refuses to compare
    # runs whose manifest hash differs, so a recall gain from an EASIER corpus
    # cannot masquerade as a better tool.
    h = hashlib.sha256("\n".join(sorted(
        f"{m['id']}|{m['band']}|{m['font']}|{m['method']}|{m['gapWidthPt']}" for m in manifest
    )).encode()).hexdigest()[:16]
    open(os.path.join(out, ".manifest-hash"), "w").write(h + "\n")

    print(f"generated {len(manifest)} cases -> {out}")
    print(f"manifest hash: {h}")
    bands = {}
    for m in manifest:
        bands[m["band"]] = bands.get(m["band"], 0) + 1
    print("bands:", dict(sorted(bands.items())))


if __name__ == "__main__":
    main()
