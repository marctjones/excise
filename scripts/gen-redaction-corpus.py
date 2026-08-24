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

FONT_PS = {"Helvetica": "Helvetica", "Helvetica-Bold": "Helvetica-Bold",
           "Times-Roman": "Times-Roman", "Times-Italic": "Times-Italic",
           "Courier": "Courier"}  # Courier = monospace, the easy band


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


def build_case(answer, font, size, method, context, colour="black-on-white", pos="mid"):
    """Return (pdf_bytes, meta). The answer sits in a line at a known x-span.

    Layout (points): left margin 72, baseline 700. The prefix is 'Name: ',
    the answer follows, then a suffix so there is surviving text on both sides
    (a redaction with nothing after it leaks nothing about width).
    """
    # Position controls what sur1viving text anchors the gap:
    #   mid       -> "Name: <answer> (on file)"  (both anchors)
    #   line-start-> "<answer> is on file"        (no left anchor)
    #   line-end  -> "On file: <answer>"          (no right anchor)
    if pos == "line-start":
        prefix, suffix = "", " is on file"
    elif pos == "line-end":
        prefix, suffix = "On file: ", ""
    else:
        prefix, suffix = "Name: ", " (on file)"
    x0 = 72.0
    ax0 = x0 + text_width_pt(prefix, font, size)          # answer left edge
    aw = text_width_pt(answer, font, size)                # answer width
    ax1 = ax0 + aw                                         # answer right edge
    ps = FONT_PS[font]

    # colour -> (box fill rgb, text ink rgb). white-on-black and low-contrast
    # exercise the #1131 detector gap; a coloured highlight over READABLE text
    # must NOT be flagged as a bad redaction.
    box_rgb, ink_rgb, cover = {
        "black-on-white":   ("0 0 0", "0 0 0", True),    # black box over black text
        "white-on-black":   ("0 0 0", "1 1 1", False),   # white text on black fill, no cover needed
        "low-contrast":     ("0.15 0.15 0.15", "0.2 0.2 0.2", False),
        "highlight-readable": ("1 1 0", "0 0 0", False), # yellow highlight, readable -> NOT a leak
    }[colour]

    def line(s, ink="0 0 0"):
        return f"BT {ink} rg /F1 {size} Tf {x0} 700 Td ({s}) Tj ET\n".encode()

    ctx = ""
    if context == "rich":
        ctx = (f"BT /F1 {size} Tf {x0} 660 Td (Claimant name and account holder:) Tj ET\n")

    if method == "original":
        # Answer present, unredacted. A real tool redacts this; we then try to
        # recover from ITS output. The manifest records the answer + gap span
        # so the scorer knows what was there and where.
        content = (line(prefix + answer + suffix) + ctx.encode())

    elif method == "under-box":
        # Text intact; masked by colour. All variants leave the glyphs
        # extractable -- what differs is whether a HUMAN can read it, which is
        # exactly what separates a caught bad redaction from a missed one.
        if cover:  # box painted OVER the text
            content = (line(prefix + answer + suffix, ink_rgb)
                       + f"{box_rgb} rg\n{ax0} 696 {aw} {size} re f\n".encode())
        else:      # fill first, text on top in a near/exact-matching ink
            content = (f"{box_rgb} rg\n{ax0} 696 {aw} {size} re f\n".encode()
                       + line(prefix + answer + suffix, ink_rgb))
        content += ctx.encode()

    elif method == "width-preserving":
        # Answer glyphs removed, gap LEFT (what excise's real redaction does),
        # box drawn. Prefix and suffix keep their positions.
        content = (line(prefix)                                      # prefix at x0
                   + f"BT /F1 {size} Tf {ax1} 700 Td ({suffix}) Tj ET\n".encode()  # suffix at its ORIGINAL x
                   + f"{box_rgb} rg\n{ax0} 696 {aw} {size} re f\n".encode()
                   + ctx.encode())

    elif method == "width-closing":
        # Answer removed AND suffix shifted left to close the gap. No width
        # channel. NEGATIVE CONTROL: residue recovery must find ~nothing.
        content = (line(prefix + suffix) + ctx.encode())

    elif method == "defended":
        # The frontier / negative control: break the width channel so the
        # VISIBLE gap between surviving glyphs no longer equals the removed
        # string's width. Achieved by shifting the suffix to a position that
        # does NOT preserve the original layout -- a per-answer deterministic
        # offset the attacker cannot know. (This is what a width-defending
        # redactor does: quantize/randomize glyph shifts, Edact-Ray "repair".)
        seed = int(hashlib.sha256(answer.encode()).hexdigest()[:8], 16)
        offset = (seed % 40) - 20                          # deterministic ±20pt
        sx = max(ax0 + 4, ax1 + offset)                    # never overlap prefix
        content = (line(prefix)
                   + f"BT 0 0 0 rg /F1 {size} Tf {sx} 700 Td ({suffix}) Tj ET\n".encode()
                   + f"0 0 0 rg\n{ax0} 696 {max(2,sx-ax0)} {size} re f\n".encode()
                   + ctx.encode())
    else:
        raise ValueError(method)

    objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
            b"/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>",
            b"<< /Length " + str(len(content)).encode() + b" >>\nstream\n" + content + b"endstream",
            f"<< /Type /Font /Subtype /Type1 /BaseFont /{ps} /Encoding /WinAnsiEncoding >>".encode()]

    meta = dict(answer=answer, font=font, sizePt=size, method=method, context=context,
                colour=colour, position=pos,
                gapX0=round(ax0, 3), gapX1=round(ax1, 3), gapWidthPt=round(aw, 3),
                dictionary=None)  # filled by the caller from the case's kind
    return _pdf(objs), meta


# (band, font, size, method, context, string-kind, colour, position)
# Bands are difficulty; the extra columns are the diversity dimensions logged
# on #1135. Pairwise-ish, not full-factorial (that explodes) -- each row adds
# ONE hard axis to a known-good baseline so a recall delta attaches to a cause.
D_MID, D_BW = "mid", "black-on-white"
PLAN = [
    # --- certain channel: colour/contrast variants (the #1131 gap) ---
    ("B0", "Helvetica",   12, "under-box", "none", "dict", "black-on-white",    D_MID),
    ("B0", "Helvetica",   12, "under-box", "none", "dict", "white-on-black",    D_MID),
    ("B0", "Helvetica",   12, "under-box", "none", "dict", "low-contrast",      D_MID),
    ("B0", "Helvetica",   12, "under-box", "none", "dict", "highlight-readable", D_MID),
    # --- residue: font families ---
    ("B1", "Helvetica",     12, "width-preserving", "none", "dict", D_BW, D_MID),
    ("B1", "Times-Roman",   12, "width-preserving", "none", "dict", D_BW, D_MID),
    ("B1", "Times-Italic",  12, "width-preserving", "none", "dict", D_BW, D_MID),
    ("B1", "Helvetica-Bold",12, "width-preserving", "none", "dict", D_BW, D_MID),
    ("Bc", "Courier",       12, "width-preserving", "none", "dict", D_BW, D_MID),  # monospace: easy
    # --- residue: sizes ---
    ("B1", "Helvetica",    8, "width-preserving", "none", "dict", D_BW, D_MID),
    ("B1", "Helvetica",   18, "width-preserving", "none", "dict", D_BW, D_MID),
    # --- residue: string types ---
    ("B2", "Helvetica",   12, "width-preserving", "none", "dict-long", D_BW, D_MID),
    ("Bd", "Helvetica",   12, "width-preserving", "none", "date",      D_BW, D_MID),
    ("Bn", "Helvetica",   12, "width-preserving", "none", "digits",    D_BW, D_MID),
    # --- residue: gap position (anchor availability) ---
    ("Bp", "Helvetica",   12, "width-preserving", "none", "dict", D_BW, "line-start"),
    ("Bp", "Helvetica",   12, "width-preserving", "none", "dict", D_BW, "line-end"),
    # --- context for ranking ---
    ("B7", "Helvetica",   12, "width-preserving", "rich", "dict", D_BW, D_MID),
    # --- negative controls: must stay ~0 recall ---
    ("B6", "Helvetica",   12, "width-preserving", "none", "random", D_BW, D_MID),
    ("B8", "Helvetica",   12, "width-closing",    "none", "dict",   D_BW, D_MID),
    ("B9", "Helvetica",   12, "defended",         "none", "dict",   D_BW, D_MID),
]


# Closed sets, recorded per case. Digit-runs and dates collapse to few width
# classes (digits are near-equal width in most fonts) -> a HARD band for
# width discrimination, and a realistic secret (SSNs, account numbers).
DATES = ["01/15/1987", "12/03/1992", "07/22/1975", "09/30/2001",
         "03/11/1968", "11/08/1954", "06/19/1983", "02/27/1990"]
DIGITS = ["4012884012", "5555341220", "6011000990", "3782822463",
          "8842019375", "1029384756", "9998887776", "4444333322"]

def strings_for(kind):
    if kind == "dict":       return NAMES
    if kind == "dict-long":  return [n for n in NAMES if len(n) >= 8]
    if kind == "date":       return DATES
    if kind == "digits":     return DIGITS
    if kind == "random":     return ["XqmzdR", "KwbfjL", "Pvgnyt", "Zdxkqw"]
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

    # Originals for tool-vs-tool comparison: same strings/fonts/sizes as the
    # residue bands, but unredacted so excise and PyMuPDF redact them for real.
    ORIGINALS = [(b, f, sz, "original", cx, k, "black-on-white", p)
                 for (b, f, sz, m, cx, k, cl, p) in PLAN
                 if m == "width-preserving"]
    for band, font, size, method, context, kind, colour, pos in PLAN + ORIGINALS:
        pool = strings_for(kind)
        for answer in pool[:args.per_band]:
            safe = "".join(c if c.isalnum() else "_" for c in answer)
            cid = f"{band}-{font.lower()}{size}-{method}-{colour}-{pos}-{safe}"
            pdf, meta = build_case(answer, font, size, method, context, colour, pos)
            open(os.path.join(out, cid + ".pdf"), "wb").write(pdf)
            meta.update(id=cid, band=band, dictionary=kind)  # names/dict-long/date/digits/random
            manifest.append(meta)

    with open(os.path.join(out, "manifest.jsonl"), "w") as f:
        for m in manifest:
            f.write(json.dumps(m) + "\n")

    # A hash over the generation parameters. #1135's scorer refuses to compare
    # runs whose manifest hash differs, so a recall gain from an EASIER corpus
    # cannot masquerade as a better tool.
    h = hashlib.sha256("\n".join(sorted(
        f"{m['id']}|{m['band']}|{m['font']}|{m['sizePt']}|{m['method']}|{m['colour']}|{m['position']}|{m['gapWidthPt']}" for m in manifest
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
