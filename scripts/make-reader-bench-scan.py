#!/usr/bin/env python3
"""Build the reader benchmark's scanned-document fixture (#1543).

None of the local corpora holds a genuine multi-page scan (checked 2026-09-16:
no document with >= 5 pages, no fonts and an image on every page). A scan is
the most common real-world PDF a viewer meets, and it stresses the one path the
other fixtures do not: every page is one large raster.

So this makes one the way a scanner does, reproducibly: rasterise pages 1-20
of the IRS 1040 instructions at 200 dpi with Poppler, and wrap each page as a
single JPEG (DCTDecode) image with no text layer.

    scripts/make-reader-bench-scan.py            # writes test-pdfs/reader-bench/
"""
import pathlib, subprocess, sys, tempfile
from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "test-pdfs/smoke/irs-1040-instructions.pdf"
OUT = ROOT / "test-pdfs/reader-bench/scan-irs-20p-200dpi.pdf"
PAGES, DPI, QUALITY = 20, 200, 80

def main():
    if not SRC.exists():
        sys.exit(f"missing source {SRC} (run scripts/download-smoke-corpus.sh)")
    OUT.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        subprocess.run(["pdftoppm", "-r", str(DPI), "-f", "1", "-l", str(PAGES),
                        "-jpeg", "-jpegopt", f"quality={QUALITY}", str(SRC), f"{tmp}/p"], check=True)
        files = sorted(pathlib.Path(tmp).glob("p-*.jpg"))
        if len(files) != PAGES:
            sys.exit(f"expected {PAGES} pages, got {len(files)}")
        images = [Image.open(f).convert("RGB") for f in files]
        images[0].save(OUT, "PDF", save_all=True, append_images=images[1:],
                       resolution=DPI, quality=QUALITY)
    print(f"wrote {OUT} ({OUT.stat().st_size // 1024} KB, {PAGES} pages at {DPI} dpi)")

if __name__ == "__main__":
    main()
