import zlib, sys

# One page, plain text, then one of each VISIBLE annotation type.
# Deliberately NO /AP on the markup annotations: the appearance is excise's own
# synthesis, which is exactly the code path #1021/RC8-RC9 built.

content = []
def line(y, s, size=13):
    content.append(f"BT /F1 {size} Tf 60 {y} Td ({s}) Tj ET")

line(742, "excise - visible annotation demo", 18)
line(715, "Each row below carries a different annotation subtype.", 10)
for y, label in [
    (670, "Highlight annotation over this sentence."),
    (640, "Underline annotation under this sentence."),
    (610, "StrikeOut annotation through this sentence."),
    (580, "Squiggly annotation beneath this sentence."),
    (545, "Square and Circle annotations sit to the right ->"),
    (470, "Line and Ink annotations are drawn below."),
    (380, "Polygon and PolyLine annotations."),
    (300, "Text annotation (sticky note) and FreeText appear at the margin."),
]:
    line(y, label)

stream = "\n".join(content).encode("latin-1")
comp = zlib.compress(stream)

objs = {}
objs[1] = b"<< /Type /Catalog /Pages 2 0 R >>"
objs[2] = b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>"
objs[5] = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"
objs[4] = b"<< /Length %d /Filter /FlateDecode >>\nstream\n" % len(comp) + comp + b"\nendstream"

# Annotations. /C is the stroke colour, /IC the interior fill (12.5.6.8).
annots = []
def A(d): annots.append(d)

A("/Type /Annot /Subtype /Highlight /Rect [58 664 340 682] "
  "/QuadPoints [58 682 340 682 58 664 340 664] /C [1 0.92 0.23] /F 4 "
  "/T (demo) /Contents (Highlight)")
A("/Type /Annot /Subtype /Underline /Rect [58 634 340 652] "
  "/QuadPoints [58 652 340 652 58 634 340 634] /C [0.1 0.5 0.95] /F 4 "
  "/T (demo) /Contents (Underline)")
A("/Type /Annot /Subtype /StrikeOut /Rect [58 604 350 622] "
  "/QuadPoints [58 622 350 622 58 604 350 604] /C [0.9 0.15 0.15] /F 4 "
  "/T (demo) /Contents (StrikeOut)")
A("/Type /Annot /Subtype /Squiggly /Rect [58 574 345 592] "
  "/QuadPoints [58 592 345 592 58 574 345 574] /C [0.1 0.7 0.2] /F 4 "
  "/T (demo) /Contents (Squiggly)")
A("/Type /Annot /Subtype /Square /Rect [400 520 470 570] "
  "/C [0.85 0.2 0.2] /IC [1 0.9 0.6] /CA 1 /F 4 /T (demo) /Contents (Square)")
A("/Type /Annot /Subtype /Circle /Rect [485 520 555 570] "
  "/C [0.2 0.3 0.8] /IC [0.8 0.9 1] /F 4 /T (demo) /Contents (Circle)")
A("/Type /Annot /Subtype /Line /Rect [55 420 400 455] /L [65 430 390 448] "
  "/C [0.6 0.1 0.7] /F 4 /T (demo) /Contents (Line)")
A("/Type /Annot /Subtype /Ink /Rect [55 330 400 415] "
  "/InkList [[70 340 120 400 170 345 220 400 270 342 320 398 370 350]] "
  "/C [0.95 0.45 0.05] /F 4 /T (demo) /Contents (Ink)")
A("/Type /Annot /Subtype /Polygon /Rect [420 330 560 420] "
  "/Vertices [430 340 550 340 550 410 490 415 430 410] "
  "/C [0.1 0.6 0.6] /IC [0.85 1 1] /F 4 /T (demo) /Contents (Polygon)")
A("/Type /Annot /Subtype /PolyLine /Rect [55 230 400 290] "
  "/Vertices [70 240 150 285 230 240 310 285 385 240] "
  "/C [0.5 0.25 0.05] /F 4 /T (demo) /Contents (PolyLine)")
A("/Type /Annot /Subtype /Text /Rect [500 250 524 274] /Name /Comment "
  "/C [1 0.85 0.2] /F 4 /T (demo) /Contents (Sticky note: this is a Text annotation.)")
A("/Type /Annot /Subtype /FreeText /Rect [55 150 330 205] "
  "/DA (0 0 1 rg /Helv 12 Tf) /Contents (FreeText annotation) "
  "/C [0.95 0.95 0.8] /F 4 /T (demo)")
A("/Type /Annot /Subtype /Link /Rect [55 100 250 125] /Border [0 0 1] "
  "/C [0 0 1] /A << /S /URI /URI (https://example.org/) >>")

first = 6
refs = " ".join(f"{first+i} 0 R" for i in range(len(annots)))
for i, a in enumerate(annots):
    objs[first + i] = ("<< " + a + " >>").encode("latin-1")

objs[3] = ("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
           "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R "
           f"/Annots [{refs}] >>").encode("latin-1")

out = bytearray(b"%PDF-1.7\n%\xe2\xe3\xcf\xd3\n")
offsets = {}
for num in sorted(objs):
    offsets[num] = len(out)
    out += b"%d 0 obj\n" % num + objs[num] + b"\nendobj\n"

xref = len(out)
n = max(objs) + 1
out += b"xref\n0 %d\n" % n
out += b"0000000000 65535 f \n"
for num in range(1, n):
    out += b"%010d 00000 n \n" % offsets.get(num, 0)
out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (n, xref)

open(sys.argv[1], "wb").write(bytes(out))
print(f"wrote {sys.argv[1]}  ({len(annots)} annotations, {len(out)} bytes)")
