#!/usr/bin/env python3
"""Generate the LAYOUT-WEIRDNESS corpus for width-policy measurement.

Sibling of gen-redaction-corpus.py (one mechanism: a box over text) and
gen-adversarial-redaction-corpus.py (one carrier per fixture). This one asks a
different question: when a redactor changes how much horizontal space a removed
word takes, what does the REST OF THE LAYOUT still say about that width?

Every fixture is a one-page PDF whose geometry is exact (standard-14 metrics,
no kerning), with a planted answer and a manifest row recording the answer and
every place the layout could restate its width:

  plain-left        baseline: left-aligned ragged text (the case close-width is built for)
  justified         a block justified to a column edge: the right edge is a ruler
  centered          centered lines: the line's START encodes its total width
  right-aligned     right-aligned lines: same, against the right edge
  two-column        two columns; the redaction must not disturb the other one
  wrapped-term      a two-word name split across a line break
  hyphen-wrapped    a single word split "Chris-/topher" across a line break
  underlined        an underline drawn to the exact width of the word
  boxed             a form-field box drawn around the word
  highlighted       a highlight bar drawn behind the word
  link              a /Link annotation whose /Rect wraps the word
  table-cell        text centred in a bordered table cell

Nothing here is a claim about what a redactor SHOULD do; it is a set of
situations with known ground truth so the behaviour can be measured.

  scripts/gen-redaction-weird-corpus.py --out test-pdfs/redaction-weird
"""
import argparse
import json
import os
import random

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
WIDTHS = json.load(open(os.path.join(ROOT, "tests/redaction-corpus/std14-widths.json")))

# Same closed set the other synthetic corpora use, so answers are comparable.
NAMES = ("James John Robert Michael William David Richard Joseph Thomas Charles "
         "Christopher Daniel Matthew Anthony Donald Mark Paul Steven Andrew Kenneth "
         "Mary Patricia Jennifer Linda Elizabeth Barbara Susan Jessica Sarah Karen "
         "Nancy Lisa Betty Margaret Sandra Ashley Kimberly Emily Donna Michelle "
         "Louise Farrar Anne Dorothy Carol Amanda Melissa Deborah Stephanie").split()

FILLER = ("the of and to in a is that for it as was with be by on not he this are or "
          "his from at which but have an had they you were their one all we can her "
          "has there been if more when will would who so no she other its may these "
          "into time two report filed office record request notice section review "
          "statement committee hearing document evidence matter party counsel court").split()

PAGE_W, PAGE_H = 612, 792


def tw(s, font, size):
    t = WIDTHS[font]
    return sum(t[ord(c) - 32] for c in s) * size / 1000.0


def esc(s):
    return s.replace("\\", "\\\\").replace("(", "\\(").replace(")", "\\)")


def fmt(v):
    return ("%.3f" % v).rstrip("0").rstrip(".")


class Page:
    """Accumulates content-stream operators and annotations for one page."""

    def __init__(self, font="Helvetica", size=11):
        self.ops = []
        self.annots = []
        self.font = font
        self.size = size
        self.decor = []          # what the layout restates about a word's width

    def text(self, x, y, s, tw_=0.0, font=None, size=None):
        f = font or self.font
        sz = size or self.size
        res = "F1" if f == "Helvetica" else "F2"
        pre = "%s Tw " % fmt(tw_) if tw_ else ""
        self.ops.append("BT /%s %s Tf %s%s %s Td (%s) Tj ET" % (res, fmt(sz), pre, fmt(x), fmt(y), esc(s)))

    def rect_fill(self, x, y, w, h, rgb=(0, 0, 0)):
        self.ops.append("q %s %s %s rg %s %s %s %s re f Q" % (
            fmt(rgb[0]), fmt(rgb[1]), fmt(rgb[2]), fmt(x), fmt(y), fmt(w), fmt(h)))

    def rect_stroke(self, x, y, w, h, lw=0.8):
        self.ops.append("q %s w %s %s %s %s re S Q" % (fmt(lw), fmt(x), fmt(y), fmt(w), fmt(h)))

    def link(self, x0, y0, x1, y1):
        self.annots.append(
            "<< /Type /Annot /Subtype /Link /Rect [%s %s %s %s] /Border [0 0 0] "
            "/A << /S /URI /URI (http://example.invalid/) >> >>" % (fmt(x0), fmt(y0), fmt(x1), fmt(y1)))


def build_pdf(page):
    objs = []
    objs.append("<< /Type /Catalog /Pages 2 0 R >>")
    objs.append("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")
    annots = ""
    n_fixed = 6                       # catalog, pages, page, contents, two fonts
    if page.annots:
        refs = " ".join("%d 0 R" % (n_fixed + 1 + i) for i in range(len(page.annots)))
        annots = " /Annots [%s]" % refs
    objs.append("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] /Contents 4 0 R "
                "/Resources << /Font << /F1 5 0 R /F2 6 0 R >> >>%s >>" % (PAGE_W, PAGE_H, annots))
    content = "\n".join(page.ops)
    objs.append("<< /Length %d >>\nstream\n%s\nendstream" % (len(content.encode("latin-1")), content))
    objs.append("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>")
    objs.append("<< /Type /Font /Subtype /Type1 /BaseFont /Times-Roman /Encoding /WinAnsiEncoding >>")
    objs.extend(page.annots)
    out = bytearray(b"%PDF-1.7\n")
    offs = []
    for i, o in enumerate(objs, 1):
        offs.append(len(out))
        out += ("%d 0 obj\n%s\nendobj\n" % (i, o)).encode("latin-1")
    xref = len(out)
    out += ("xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)).encode()
    for o in offs:
        out += ("%010d 00000 n \n" % o).encode()
    out += ("trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)).encode()
    return bytes(out)


def sentence(rng, n):
    return [rng.choice(FILLER) for _ in range(n)]


def wrap(words, font, size, width):
    lines, cur = [], []
    for w in words:
        trial = " ".join(cur + [w])
        if cur and tw(trial, font, size) > width:
            lines.append(cur)
            cur = [w]
        else:
            cur.append(w)
    if cur:
        lines.append(cur)
    return lines


# --------------------------------------------------------------------- cases
def case_plain_left(rng, name, font, size):
    p = Page(font, size)
    pre = " ".join(sentence(rng, 5)) + " signed by "
    post = " on behalf of the committee and attached below."
    x, y = 72, 640
    p.text(x, y, pre + name + post)
    return p, dict(term=name, x_name=x + tw(pre, font, size), y=y, name_w=tw(name, font, size))


def case_justified(rng, name, font, size):
    p = Page(font, size)
    W, x0, y = 380.0, 100.0, 660.0
    words = sentence(rng, 60)
    words[27] = name                              # plant the answer mid-paragraph
    lines = wrap(words, font, size, W)
    meta = dict(term=name, col_x0=x0, col_x1=x0 + W, name_w=tw(name, font, size), lines=[])
    sp = tw(" ", font, size)
    for i, ln in enumerate(lines):
        text = " ".join(ln)
        last = i == len(lines) - 1
        nat = tw(text, font, size)
        tw_ = 0.0 if last or len(ln) < 2 else (W - nat) / (len(ln) - 1)
        p.text(x0, y, text, tw_)
        meta["lines"].append(dict(y=y, has_name=name in ln, justified=not last))
        if name in ln:
            meta["y"] = y
        y -= size * 1.35
    return p, meta


def case_centered(rng, name, font, size):
    p = Page(font, size)
    cx, y = 306.0, 640.0
    rows = [" ".join(sentence(rng, 4)),
            "Prepared for " + name + " and family",
            " ".join(sentence(rng, 5)),
            " ".join(sentence(rng, 3))]
    meta = dict(term=name, cx=cx, name_w=tw(name, font, size), lines=[])
    for r in rows:
        w = tw(r, font, size)
        p.text(cx - w / 2, y, r)
        meta["lines"].append(dict(y=y, has_name=name in r))
        if name in r:
            meta["y"] = y
        y -= size * 1.6
    meta["term_at_end"] = False
    return p, meta


def case_right_aligned(rng, name, font, size):
    p = Page(font, size)
    R, y = 540.0, 640.0
    rows = [" ".join(sentence(rng, 4)), "Signed, " + name, " ".join(sentence(rng, 5)),
            " ".join(sentence(rng, 3))]
    meta = dict(term=name, R=R, name_w=tw(name, font, size), lines=[])
    for r in rows:
        w = tw(r, font, size)
        p.text(R - w, y, r)
        meta["lines"].append(dict(y=y, has_name=name in r))
        if name in r:
            meta["y"] = y
        y -= size * 1.6
    meta["term_at_end"] = True
    return p, meta


def case_two_column(rng, name, font, size):
    p = Page(font, size)
    colw = 230.0
    meta = dict(term=name, name_w=tw(name, font, size), lines=[])
    for cx0, plant in ((60.0, True), (330.0, False)):
        words = sentence(rng, 42)
        if plant:
            words[13] = name
        y = 660.0
        for ln in wrap(words, font, size, colw):
            p.text(cx0, y, " ".join(ln))
            if plant and name in ln:
                meta["y"] = y
            y -= size * 1.35
    return p, meta


def case_wrapped_term(rng, name, font, size):
    first, last = name, rng.choice([n for n in NAMES if n != name])
    p = Page(font, size)
    x, y = 72, 640
    l1 = " ".join(sentence(rng, 6)) + " signed by " + first
    l2 = last + " on behalf of the committee and " + " ".join(sentence(rng, 3)) + "."
    p.text(x, y, l1)
    p.text(x, y - size * 1.35, l2)
    return p, dict(term=first + " " + last, answer_parts=[first, last], y=y,
                   name_w=tw(first + " " + last, font, size))


def case_hyphen_wrapped(rng, name, font, size):
    # split the word roughly in half with a hyphen at the line end
    k = max(3, len(name) // 2)
    p = Page(font, size)
    x, y = 72, 640
    l1 = " ".join(sentence(rng, 6)) + " signed by " + name[:k] + "-"
    l2 = name[k:] + " on behalf of the committee and " + " ".join(sentence(rng, 3)) + "."
    p.text(x, y, l1)
    p.text(x, y - size * 1.35, l2)
    return p, dict(term=name, y=y, name_w=tw(name, font, size))


def _inline(rng, name, font, size):
    pre = " ".join(sentence(rng, 5)) + " signed by "
    post = " on behalf of the committee and attached below."
    x, y = 72, 640
    xn = x + tw(pre, font, size)
    return x, y, xn, pre, post


def case_underlined(rng, name, font, size):
    p = Page(font, size)
    x, y, xn, pre, post = _inline(rng, name, font, size)
    w = tw(name, font, size)
    p.text(x, y, pre + name + post)
    p.rect_fill(xn, y - 1.8, w, 0.6)
    return p, dict(term=name, x_name=xn, y=y, name_w=w,
                   decor=[dict(kind="underline", x0=xn, x1=xn + w, y=y - 1.8, width=w, pad=0.0)])


def case_boxed(rng, name, font, size):
    p = Page(font, size)
    x, y, xn, pre, post = _inline(rng, name, font, size)
    w = tw(name, font, size)
    p.text(x, y, pre + name + post)
    p.rect_stroke(xn - 2, y - 3, w + 4, size + 4)
    return p, dict(term=name, x_name=xn, y=y, name_w=w,
                   decor=[dict(kind="box", x0=xn - 2, x1=xn + w + 2, y=y - 3, width=w + 4, pad=4.0)])


def case_highlighted(rng, name, font, size):
    p = Page(font, size)
    x, y, xn, pre, post = _inline(rng, name, font, size)
    w = tw(name, font, size)
    p.rect_fill(xn - 1, y - 2.5, w + 2, size + 2, rgb=(1, 0.95, 0.3))    # drawn BEHIND the text
    p.text(x, y, pre + name + post)
    return p, dict(term=name, x_name=xn, y=y, name_w=w,
                   decor=[dict(kind="highlight", x0=xn - 1, x1=xn + w + 1, y=y - 2.5, width=w + 2, pad=2.0)])


def case_link(rng, name, font, size):
    p = Page(font, size)
    x, y, xn, pre, post = _inline(rng, name, font, size)
    w = tw(name, font, size)
    p.text(x, y, pre + name + post)
    p.link(xn, y - 2, xn + w, y + size)
    return p, dict(term=name, x_name=xn, y=y, name_w=w,
                   decor=[dict(kind="link-rect", x0=xn, x1=xn + w, y=y - 2, width=w, pad=0.0)])


def case_table_cell(rng, name, font, size):
    p = Page(font, size)
    cx0, cw, ch, y = 72.0, 200.0, 26.0, 600.0
    label = "Applicant: " + name
    for i in range(3):
        p.rect_stroke(cx0 + i * cw, y, cw, ch)
    w = tw(label, font, size)
    p.text(cx0 + cw + (cw - w) / 2, y + 8, label)                 # centred in the middle cell
    p.text(cx0 + 6, y + 8, "Case no.")
    p.text(cx0 + 2 * cw + 6, y + 8, "Filed")
    return p, dict(term=name, y=y + 8, name_w=tw(name, font, size), cell_cx=cx0 + cw + cw / 2,
                   term_at_end=True)


CASES = [
    ("plain-left", case_plain_left), ("justified", case_justified), ("centered", case_centered),
    ("right-aligned", case_right_aligned), ("two-column", case_two_column),
    ("wrapped-term", case_wrapped_term), ("hyphen-wrapped", case_hyphen_wrapped),
    ("underlined", case_underlined), ("boxed", case_boxed), ("highlighted", case_highlighted),
    ("link", case_link), ("table-cell", case_table_cell),
]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(ROOT, "test-pdfs", "redaction-weird"))
    ap.add_argument("--per-case", type=int, default=8)
    ap.add_argument("--seed", type=int, default=20260920)
    a = ap.parse_args()
    os.makedirs(a.out, exist_ok=True)
    rng = random.Random(a.seed)
    fonts = [("Helvetica", 11), ("Times-Roman", 12), ("Helvetica", 9), ("Times-Roman", 10)]
    manifest = []
    for cname, fn in CASES:
        pool = [n for n in NAMES if len(n) >= 5]
        rng.shuffle(pool)
        for i in range(a.per_case):
            name = pool[i % len(pool)]
            font, size = fonts[i % len(fonts)]
            page, meta = fn(rng, name, font, size)
            cid = "%s-%02d-%s" % (cname, i, name.lower())
            open(os.path.join(a.out, cid + ".pdf"), "wb").write(build_pdf(page))
            row = dict(id=cid, category=cname, answer=name, font=font, sizePt=size)
            row.update(meta)
            manifest.append(row)
    with open(os.path.join(a.out, "manifest.jsonl"), "w") as f:
        for r in manifest:
            f.write(json.dumps(r) + "\n")
    print("wrote %d fixtures across %d layout situations to %s" % (len(manifest), len(CASES), a.out))


if __name__ == "__main__":
    main()
