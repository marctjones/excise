#!/usr/bin/env python3
"""Measure what a redaction's WIDTH POLICY does to the page, and what it leaves behind.

For every document: redact one term twice -- the default policy (layout kept) and
`--close-width` -- then compare each output to the ORIGINAL, character by
character, using mutool's per-glyph positions (an oracle that is not excise).

Three questions, each measured rather than asserted:

  1. DRIFT.    Did the page move the way the policy says it should? Text before the
               removed word should not move; text after it should move left by the
               removed word's width under close-width and not at all by default; every
               other line should not move. Anything else is a weirdness, and is counted.
  2. LEAK.     What does the surviving layout still say about the removed width?
               Estimated from the OUTPUT geometry only (the original is never used by an
               estimator), by the kind of layout: the gap itself, the ragged/justified edge,
               the start of a centred or right-aligned line, an underline / box / highlight
               / link rectangle that did not move.
  3. RECOVER.  Feed that estimate to a width-fit dictionary attack and ask whether the
               true answer is among the words that fit. Synthetic cases only: real
               documents have no planted answer, so they report estimate-vs-truth error.

  scripts/bench-width-policy.py synthetic --corpus test-pdfs/redaction-weird
  scripts/bench-width-policy.py real --n-per-class 10

The dictionary is /usr/share/dict/propernames plus the corpus names when present
(1,312 first names on macOS). A different list changes the recovery numbers and
nothing else, so the list in use is printed with every run.
"""
import argparse
import bisect
import glob
import json
import math
import multiprocessing as mp
import os
import re
import statistics as st
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
WIDTHS = json.load(open(os.path.join(ROOT, "tests/redaction-corpus/std14-widths.json")))
EXCISE = os.environ.get("EXCISE_CLI") or "dotnet %s" % os.path.join(ROOT, "Excise.Cli/bin/Debug/net10.0/excise.dll")


# ------------------------------------------------------------------ plumbing
def sh(cmd, **kw):
    return subprocess.run(cmd, capture_output=True, text=True, **kw)


def excise(*args):
    return sh(EXCISE.split() + list(args), timeout=180)


def std_w(s, font, size):
    t = WIDTHS[font]
    return sum(t[ord(c) - 32] for c in s if 32 <= ord(c) < 32 + len(t)) * size / 1000.0


def stext_lines(pdf, page=1):
    """[[(char, x0, x1, y), ...], ...] one list per line, in mutool's reading order."""
    p = sh(["mutool", "draw", "-q", "-F", "stext", "-o", "-", pdf, str(page)], timeout=60)
    try:
        root = ET.fromstring(p.stdout)
    except ET.ParseError:
        return []
    out = []
    for ln in root.iter("line"):
        row = []
        for c in ln.iter("char"):
            q = [float(v) for v in c.get("quad").split()]
            row.append((c.get("c"), q[0], q[2], q[5]))
        if row:
            out.append(row)
    return out


def flat(lines):
    """Non-space characters with their original line index."""
    return [(li, k, c) for li, ln in enumerate(lines) for k, c in enumerate(ln) if not c[0].isspace()]


def load_dictionary():
    words = set()
    p = "/usr/share/dict/propernames"
    if os.path.exists(p):
        words |= {w.strip() for w in open(p, encoding="latin-1") if w.strip()}
    src = open(os.path.join(HERE, "gen-redaction-weird-corpus.py"), encoding="utf-8").read()
    m = re.search(r'NAMES = \((.*?)\)\.split\(\)', src, re.S)
    words |= set(" ".join(re.findall(r'"([^"]*)"', m.group(1))).split())
    return sorted(words)


def make_attacker(words):
    cache = {}

    def widths_for(font, size):
        k = (font, size)
        if k not in cache:
            cache[k] = sorted((std_w(w, font, size), w) for w in words)
        return cache[k]

    def attack(est, answer, font, size, tol=0.5):
        if est is None:
            return None
        ws = widths_for(font, size)
        keys = [w for w, _ in ws]
        lo, hi = bisect.bisect_left(keys, est - tol), bisect.bisect_right(keys, est + tol)
        cand = sorted(ws[lo:hi], key=lambda t: abs(t[0] - est))
        names = [w for _, w in cand]
        return dict(fit=len(names), hit=answer in names,
                    rank=(names.index(answer) + 1) if answer in names else 0)
    return attack


# ------------------------------------------------------------- redact + drift
def redact(src, term, close):
    out = tempfile.mktemp(suffix=".pdf")
    args = ["redact", src, out, term] + (["--close-width"] if close else [])
    p = excise(*args)
    ok = os.path.exists(out)
    q = sh(["qpdf", "--check", out]).returncode if ok else None
    return (out if ok else None), p.returncode, q


def find_span(f0, term):
    t = "".join(ch for ch in term if not ch.isspace()).lower()
    s = "".join(c[2][0] for c in f0).lower()
    i = s.find(t)
    if i < 0:
        return None
    return i, i + len(t)


def analyse_drift(orig, out, term):
    """Compare per-glyph positions. Returns a dict, or {'aligned': False,...}."""
    l0, l1 = stext_lines(orig), stext_lines(out)
    f0, f1 = flat(l0), flat(l1)
    span = find_span(f0, term)
    if span is None:
        return dict(aligned=False, reason="term-not-contiguous-in-original")
    i, j = span
    rest0 = f0[:i] + f0[j:]
    if [c[2][0] for c in rest0] != [c[2][0] for c in f1]:
        left = "".join(c[2][0] for c in f1).lower()
        return dict(aligned=False, reason="output-text-differs",
                    term_survives=term.replace(" ", "").lower() in left)
    term_chars = f0[i:j]
    line_of_term = term_chars[0][0]
    removed_w = term_chars[-1][2][2] - term_chars[0][2][1]
    same_line = len({c[0] for c in term_chars}) == 1
    before, after, other = [], [], []
    for (li, k, c0), (lj, kk, c1) in zip(rest0, f1):
        dx = c1[1] - c0[1]
        if li == line_of_term:
            (before if k < term_chars[0][1] else after).append(dx)
        else:
            other.append(dx)
    res = dict(aligned=True, removed_w=removed_w, single_line=same_line,
               before_max=max([abs(d) for d in before] or [0]),
               other_max=max([abs(d) for d in other] or [0]),
               other_moved=sum(1 for d in other if abs(d) > 0.5))
    if after:
        res.update(after_med=st.median(after), after_spread=max(after) - min(after))
        res["closure"] = -st.median(after) / removed_w if removed_w else None
    return res


# ------------------------------------------------------------------ estimators
PAGE_H = 792.0


def _line_at(lines, y_pdf, tol=9.0):
    """The line whose baseline is nearest y (PDF, y up). mutool's stext y points DOWN and
    reports the glyph box bottom, a few points below the baseline, hence the tolerance."""
    best, bd = None, tol
    for ln in lines:
        ys = [c[3] for c in ln if not c[0].isspace()]
        if not ys:
            continue
        d = abs(st.median(ys) - (PAGE_H - y_pdf))
        if d < bd:
            best, bd = ln, d
    return best


def _merged_line(lines, y_pdf, tol=9.0):
    """mutool splits a line at a large gap, so the two halves of a redacted line arrive as two
    'lines'. Join every piece on the same baseline into one left-to-right list of glyphs."""
    ln = _line_at(lines, y_pdf, tol)
    if ln is None:
        return None
    ys = st.median(c[3] for c in ln if not c[0].isspace())
    joined = []
    for o in lines:
        oy = [c[3] for c in o if not c[0].isspace()]
        if oy and abs(st.median(oy) - ys) < 3.0:
            joined.extend(o)
    return sorted(joined, key=lambda c: c[1])


def _extent(ln):
    ch = [c for c in ln if not c[0].isspace()]
    return min(c[1] for c in ch), max(c[2] for c in ch), len(ch)


def estimate_from_layout(out_pdf, row, mode):
    """Estimate the removed width from OUTPUT geometry (+ the layout category). None = no estimate."""
    cat = row["category"]
    lines = stext_lines(out_pdf)
    y = row.get("y")
    ln = _merged_line(lines, y) if y is not None else None
    if ln is None:
        return None, "line-not-found"
    # neighbouring cells / columns share the baseline: keep only this cell's or column's glyphs
    if cat == "table-cell":
        ln = [c for c in ln if abs((c[1] + c[2]) / 2 - row["cell_cx"]) <= 100.0]
    elif cat == "two-column":
        ln = [c for c in ln if c[1] < 300.0]
    if not any(not c[0].isspace() for c in ln):
        return None, "cell-empty"
    x0, x1, n = _extent(ln)
    size, font = row["sizePt"], row["font"]
    sp = std_w(" ", font, size)
    if mode == "default":
        if row.get("term_at_end"):
            return None, "no-right-anchor"
        # the gap itself: widest hole between neighbouring glyphs, minus the two spaces around it
        ch = [c for c in ln if not c[0].isspace()]
        gaps = [ch[k + 1][1] - ch[k][2] for k in range(len(ch) - 1)]
        return (max(gaps) - 2 * sp, "gap") if gaps else (None, "no-gap")
    others = [o for o in lines if o is not ln and sum(1 for c in o if not c[0].isspace()) >= 12]
    if cat in ("justified", "right-aligned"):
        if len(others) < 3:
            return None, "no-reference"
        R = st.median(_extent(o)[1] for o in others[:-1] if _extent(o)[2] >= 12) if cat == "justified" \
            else st.median(_extent(o)[1] for o in others)
        # a removed word at the END of a line leaves the space before it, which is advance but not ink
        return R - x1 - (sp if row.get("term_at_end") else 0.0), "edge"
    if cat == "centered":
        if len(others) < 2:
            return None, "no-reference"
        cx = st.median((_extent(o)[0] + _extent(o)[1]) / 2 for o in others)
        return 2 * (cx - x0) - (x1 - x0) - (sp if row.get("term_at_end") else 0.0), "centre"
    if cat == "table-cell":
        return 2 * (row["cell_cx"] - x0) - (x1 - x0) - (sp if row.get("term_at_end") else 0.0), "centre"
    return None, "no-alignment-reference"


def decoration_estimate(out_pdf, row):
    """Is the decoration still there, and what width does it state? (rectangles / link /Rect)"""
    dec = row.get("decor")
    if not dec:
        return None, "no-decoration"
    d = dec[0]
    q = tempfile.mktemp(suffix=".qdf")
    sh(["qpdf", "--qdf", "--object-streams=disable", "--stream-data=uncompress", out_pdf, q])
    try:
        txt = open(q, "rb").read().decode("latin-1")
    finally:
        try:
            os.remove(q)
        except OSError:
            pass
    if d["kind"] == "link-rect":
        for m in re.finditer(r"/Rect\s*\[\s*([-\d. ]+)\]", txt):
            v = [float(t) for t in m.group(1).split()]
            if len(v) == 4 and abs(v[0] - d["x0"]) < 0.6 and abs(v[1] - d["y"]) < 0.6:
                return (v[2] - v[0]) - d["pad"], "link-rect"
        return None, "decoration-removed"
    for m in re.finditer(r"([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+) re\b", txt):
        x, y, w, h = (float(g) for g in m.groups())
        if abs(x - d["x0"]) < 0.6 and abs(y - d["y"]) < 0.6:
            return w - d["pad"], d["kind"]
    return None, "decoration-removed"


def norm_letters(t):
    return re.sub(r"[\s\-]+", "", t.lower())


def needles_for(row):
    """The fragments that would show the answer survived. A wrapped or hyphenated term is
    checked by its PARTS: 'Betty Mary' or 'Chris-/topher' can survive as either half."""
    term = row["term"]
    if row["category"] == "hyphen-wrapped":
        k = max(3, len(term) // 2)
        return [term[:k], term[k:]]
    if row["category"] == "wrapped-term":
        return list(row.get("answer_parts") or term.split())
    return [term]


def text_still_contains(out_pdf, needles):
    t = norm_letters(sh(["mutool", "draw", "-q", "-F", "txt", "-o", "-", out_pdf, "1"]).stdout)
    return any(norm_letters(n) in t for n in needles if len(n) >= 3)


# ------------------------------------------------------------------- synthetic
def run_synthetic(a):
    attack = make_attacker(load_dictionary())
    rows = [json.loads(l) for l in open(os.path.join(a.corpus, "manifest.jsonl")) if l.strip()]
    results = []
    for r in rows:
        src = os.path.join(a.corpus, r["id"] + ".pdf")
        rec = dict(id=r["id"], category=r["category"], answer=r["answer"])
        for mode in ("default", "close"):
            out, rc, qpdf = redact(src, r["term"], mode == "close")
            m = dict(rc=rc, qpdf=qpdf)
            if out:
                d = analyse_drift(src, out, r["term"])
                m["drift"] = d
                m["term_survives"] = text_still_contains(out, needles_for(r))
                est, how = estimate_from_layout(out, r, mode)
                m["layout_est"], m["layout_how"] = est, how
                m["layout_attack"] = attack(est, r["answer"], r["font"], r["sizePt"])
                dest, dhow = decoration_estimate(out, r)
                m["decor_est"], m["decor_how"] = dest, dhow
                m["decor_attack"] = attack(dest, r["answer"], r["font"], r["sizePt"])
                m["true_w"] = r["name_w"]
                os.remove(out)
            rec[mode] = m
        results.append(rec)
    json.dump(results, open(a.json, "w"))
    report_synthetic(results)


def report_synthetic(results):
    cats = []
    for r in results:
        if r["category"] not in cats:
            cats.append(r["category"])
    print("\n== A. Did it still work? (text gone, valid file) ==")
    print("%-15s %3s | %-24s | %-24s" % ("situation", "n", "DEFAULT gone/qpdf-ok", "CLOSE-WIDTH gone/qpdf-ok"))
    for c in cats:
        rs = [r for r in results if r["category"] == c]
        cell = []
        for mode in ("default", "close"):
            gone = sum(1 for r in rs if r[mode].get("term_survives") is False)
            valid = sum(1 for r in rs if r[mode].get("qpdf") == 0)
            cell.append("%d/%d  %d/%d" % (gone, len(rs), valid, len(rs)))
        print("%-15s %3d | %-24s | %-24s" % (c, len(rs), cell[0], cell[1]))

    print("\n== B. Drift against the ORIGINAL: is it what the policy promises? ==")
    print("closure = how much of the removed width the text after it moved left (1.00 = fully closed, 0 = not at all)")
    print("%-15s | %-34s | %-44s" % ("situation", "DEFAULT  closure  other-lines-moved", "CLOSE-WIDTH  closure  spread>1pt  other-moved  aligned"))
    for c in cats:
        rs = [r for r in results if r["category"] == c]
        out = []
        for mode in ("default", "close"):
            ds = [r[mode].get("drift", {}) for r in rs]
            al = [d for d in ds if d.get("aligned")]
            cl = [d["closure"] for d in al if d.get("closure") is not None]
            med = ("%.2f" % st.median(cl)) if cl else " n/a"
            spread = sum(1 for d in al if d.get("after_spread", 0) > 1.0)
            moved = sum(1 for d in al if d.get("other_moved", 0) > 0)
            out.append((med, spread, moved, len(al), len(rs)))
        print("%-15s | %6s   %14s        | %6s   %9s   %11s   %7s" % (
            c, out[0][0], "%d/%d" % (out[0][2], out[0][3]),
            out[1][0], "%d/%d" % (out[1][1], out[1][3]), "%d/%d" % (out[1][2], out[1][3]),
            "%d/%d" % (out[1][3], out[1][4])))

    print("\n== C. What the surviving layout still says: recovery by the estimate ==")
    print("hit = the true answer is among the dictionary words that fit the estimated width (+/-0.5pt); fit = how many words fit")
    print("%-15s %3s | %-33s | %-33s | %-26s" % ("situation", "n", "DEFAULT (gap)", "CLOSE-WIDTH (layout edge/centre)",
                                                 "CLOSE-WIDTH (decoration)"))
    for c in cats:
        rs = [r for r in results if r["category"] == c]

        def cell(mode, key):
            xs = [r[mode].get(key) for r in rs]
            xs = [x for x in xs if x]
            if not xs:
                return "no estimate"
            hits = sum(1 for x in xs if x["hit"])
            fit = st.median(x["fit"] for x in xs)
            return "%d/%d hit, median %d fit" % (hits, len(xs), fit)
        print("%-15s %3d | %-33s | %-33s | %-26s" % (c, len(rs), cell("default", "layout_attack"),
                                                       cell("close", "layout_attack"), cell("close", "decor_attack")))
    print("\n== D. Why an estimate was or wasn't available (close-width) ==")
    for c in cats:
        rs = [r for r in results if r["category"] == c]
        how = {}
        for r in rs:
            for k in ("layout_how", "decor_how"):
                v = r["close"].get(k)
                if v and v not in ("no-decoration",):
                    how[v] = how.get(v, 0) + 1
        print("%-15s %s" % (c, ", ".join("%s x%d" % kv for kv in sorted(how.items()))))


# ------------------------------------------------------------------------ real
def classify_real(pdf):
    """Cheap page-1 layout class from glyph geometry (no redaction involved)."""
    lines = stext_lines(pdf)
    body = [ln for ln in lines if sum(1 for c in ln if not c[0].isspace()) >= 40]
    if len(body) < 6:
        short = [ln for ln in lines if 10 <= sum(1 for c in ln if not c[0].isspace()) <= 70]
        if len(short) >= 4:
            cen = [(_extent(l)[0] + _extent(l)[1]) / 2 for l in short]
            mode = st.median(cen)
            if sum(1 for c in cen if abs(c - mode) < 1.5) >= max(4, int(0.6 * len(short))):
                return "centered", lines
        return "sparse", lines
    x1s = [round(_extent(l)[1]) for l in body]
    common = max(set(x1s), key=x1s.count)
    frac = x1s.count(common) / len(x1s)
    x0s = sorted(_extent(l)[0] for l in body)
    cols = 1 + sum(1 for a, b in zip(x0s, x0s[1:]) if b - a > 60)
    if cols >= 2:
        return "multi-column", lines
    if frac >= 0.55:
        return "justified", lines
    return "left-ragged", lines


def pick_term(lines):
    text = " ".join("".join(c[0] for c in ln) for ln in lines).lower()
    cands = []
    for ln in lines:
        s = "".join(c[0] for c in ln)
        if len(s.strip()) < 35:
            continue
        for m in re.finditer(r"(?<= )([A-Za-z]{6,12})(?= )", s):
            w = m.group(1)
            if text.count(w.lower()) == 1:
                cands.append(w)
    return cands[len(cands) // 2] if cands else None


def real_candidate(path):
    try:
        cls, lines = classify_real(path)
        return path, cls, pick_term(lines)
    except Exception:
        return path, "error", None


def run_real(a):
    pools = ["smoke", "federal", "sample-pdfs", "pdfjs", "pdfium", "poppler", "itext"]
    files = []
    for p in pools:
        for f in sorted(glob.glob(os.path.join(a.testpdfs, p, "**", "*.pdf"), recursive=True)):
            if os.path.getsize(f) < 4_000_000:
                files.append(f)
    seen, uniq = set(), []
    for f in files:
        b = os.path.basename(f)
        if b not in seen:
            seen.add(b)
            uniq.append(f)
    print("classifying %d candidate documents (page 1, glyph geometry) ..." % len(uniq), flush=True)
    with mp.Pool(6) as pool:
        cls = pool.map(real_candidate, uniq)
    by = {}
    for path, c, term in cls:
        if term:
            by.setdefault(c, []).append((path, term))
    print("classes found:", {k: len(v) for k, v in sorted(by.items())})
    chosen = []
    for c, items in by.items():
        step = max(1, len(items) // a.n_per_class)
        chosen += [(c,) + it for it in items[::step][: a.n_per_class]]
    results = []
    for c, path, term in chosen:
        rec = dict(doc=os.path.basename(path), cls=c, term=term)
        for mode in ("default", "close"):
            out, rc, qpdf = redact(path, term, mode == "close")
            m = dict(rc=rc, qpdf=qpdf)
            if out:
                m["drift"] = analyse_drift(path, out, term)
                os.remove(out)
            rec[mode] = m
        rec["qpdf_input"] = sh(["qpdf", "--check", path]).returncode
        results.append(rec)
    json.dump(results, open(a.json, "w"))
    report_real(results)


def report_real(results):
    classes = sorted({r["cls"] for r in results})
    print("\n== REAL documents: drift against the original ==")
    print("%-13s %3s | %-30s | %-46s" % ("layout", "n", "DEFAULT ok/qpdf  other-moved", "CLOSE-WIDTH ok/qpdf closed>=.9 partial none spread>1pt other-moved"))
    for c in classes:
        rs = [r for r in results if r["cls"] == c]
        n = len(rs)
        cell = []
        for mode in ("default", "close"):
            ok = sum(1 for r in rs if r[mode].get("rc") == 0)
            qp = sum(1 for r in rs if r[mode].get("qpdf") == r["qpdf_input"] == 0 or r[mode].get("qpdf") == 0)
            ds = [r[mode].get("drift", {}) for r in rs]
            al = [d for d in ds if d.get("aligned")]
            moved = sum(1 for d in al if d.get("other_moved", 0) > 0)
            cl = [d.get("closure") for d in al if d.get("closure") is not None]
            closed = sum(1 for x in cl if x >= 0.9)
            part = sum(1 for x in cl if 0.1 <= x < 0.9)
            none = sum(1 for x in cl if x < 0.1)
            spread = sum(1 for d in al if d.get("after_spread", 0) > 1.0)
            cell.append((ok, qp, moved, len(al), closed, part, none, spread))
        d, k = cell
        print("%-13s %3d | %d/%d  %d/%d  moved %d/%d          | %d/%d  %d/%d   %2d      %2d     %2d     %2d       %d/%d   (aligned %d/%d)" % (
            c, n, d[0], n, d[1], n, d[2], d[3], k[0], n, k[1], n, k[4], k[5], k[6], k[7], k[2], k[3], k[3], n))


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("synthetic")
    s.add_argument("--corpus", default=os.path.join(ROOT, "test-pdfs", "redaction-weird"))
    s.add_argument("--json", default="bench-width-synthetic.json")
    r = sub.add_parser("real")
    r.add_argument("--testpdfs", default=os.path.join(ROOT, "test-pdfs"))
    r.add_argument("--n-per-class", type=int, default=10)
    r.add_argument("--json", default="bench-width-real.json")
    a = ap.parse_args()
    print("dictionary: %d words" % len(load_dictionary()))
    print("cli:", EXCISE)
    {"synthetic": run_synthetic, "real": run_real}[a.cmd](a)
