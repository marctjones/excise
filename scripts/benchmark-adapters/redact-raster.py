#!/usr/bin/env python3
"""Raster-baseline redaction adapter for the redaction benchmark (#1121).

Usage: redact-raster.py <input.pdf> <output.pdf> <term>
Exit:  0 = redacted (prints the occurrence count), 2 = tool error.

THE ANCHOR, not a joke entry. It rasterises every page and paints the term's
region black — the pdf-redact-tools / CoverUP approach. That is one END of the
trade-off curve: PERFECT Leak (no text survives anywhere, so nothing is
extractable or width-recoverable) bought with TOTAL Collateral (every character
on the page stops being text) and destroyed Fidelity (a scanned image, not a
document). Without it, "Leak = 0" looks like the goal and the benchmark rewards
destroying the document; with it, every other tool's score reads as "how much
fidelity did you keep while getting Leak down" — the actual question.

Rasterisation uses PyMuPDF purely as a renderer here; the redaction is the
rasterise-everything policy, not PyMuPDF's redaction API (that is the pymupdf
adapter).
"""
import sys

try:
    import pymupdf
except ImportError:                                   # older wheels
    import fitz as pymupdf

DPI = 150


def main() -> int:
    if len(sys.argv) != 4:
        print("usage: redact-raster.py <in> <out> <term>", file=sys.stderr)
        return 2
    src, dst, term = sys.argv[1], sys.argv[2], sys.argv[3]

    try:
        doc = pymupdf.open(src)
    except Exception as exc:                           # noqa: BLE001
        print(f"open failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2

    out = pymupdf.open()
    removed = 0
    scale = DPI / 72.0
    try:
        for page in doc:
            hits = page.search_for(term)
            removed += len(hits)
            pix = page.get_pixmap(dpi=DPI)
            # Paint each hit black in PIXEL space so even OCR cannot read it —
            # the region is gone, not merely un-extractable.
            for r in hits:
                pr = pymupdf.IRect(int(r.x0 * scale), int(r.y0 * scale),
                                   int(r.x1 * scale) + 1, int(r.y1 * scale) + 1)
                pr = pr & pix.irect                    # clamp to the pixmap
                if not pr.is_empty:
                    pix.set_rect(pr, (0, 0, 0))
            newpage = out.new_page(width=page.rect.width, height=page.rect.height)
            newpage.insert_image(page.rect, pixmap=pix)
        out.save(dst)
    except Exception as exc:                           # noqa: BLE001
        print(f"raster redaction failed: {type(exc).__name__}: {exc}", file=sys.stderr)
        return 2

    print(removed)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
