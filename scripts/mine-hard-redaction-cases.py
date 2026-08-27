#!/usr/bin/env python3
"""Find REAL corpus PDFs that create HARD redaction situations, and a term that
lives in the hard structure — candidate rows for tests/redaction-hard-cases.tsv (#1185).

Emits `path <tab> term <tab> difficulty <tab> category <tab> note` to stdout.
Candidates are NOT auto-added: review, then merge the good ones into the manifest.
The bench redacts each (file, term) and measures whether the term survives in a
carrier the tool did not scrub, across excise + reference redactors.

Categories mined here (see the manifest header for the full curated set):
  actualtext, cid-type0, acroform-value, freetext-annotation, outline-title,
  invisible-text, tj-kerning-heavy.

Requires: qpdf, mutool. Usage: python3 scripts/mine-hard-redaction-cases.py [corpus...]
"""
import os, re, subprocess, sys, glob

ROOT = os.path.join(os.path.dirname(__file__), "..")
CORPORA = sys.argv[1:] or ["pdfua", "verapdf-corpus", "pdfjs", "pdfium", "poppler",
                           "federal", "ghent", "altona", "pdf20", "smoke"]

def run(cmd, timeout=30):
    try: return subprocess.run(cmd, capture_output=True, timeout=timeout).stdout
    except Exception: return b""

def expand(p):   return run(["qpdf","--qdf","--object-streams=disable","--decode-level=all",p,"-"])
def mutext(p):   return run(["mutool","draw","-F","txt","-o","-",p]).decode("utf-8","ignore")

def good_token(s, minlen=4, maxlen=20):
    for tok in re.findall(r'[A-Za-z][A-Za-z0-9]{%d,%d}' % (minlen-1, maxlen-1), s):
        return tok
    return None

def files(corpus):
    return sorted(glob.glob(os.path.join(ROOT, "test-pdfs", corpus, "**", "*.pdf"), recursive=True))

def rel(p): return os.path.relpath(p, ROOT)

seen_cat = {}   # cap per category
CAP = 6

def emit(path, term, diff, cat, note):
    if seen_cat.get(cat, 0) >= CAP: return
    seen_cat[cat] = seen_cat.get(cat, 0) + 1
    print(f"{rel(path)}\t{term}\t{diff}\t{cat}\t{note}")

for corpus in CORPORA:
    for f in files(corpus):
        if all(seen_cat.get(c,0) >= CAP for c in
               ["actualtext","cid-type0","acroform-value","freetext-annotation",
                "outline-title","invisible-text","tj-kerning-heavy"]):
            break
        exp = expand(f)
        if not exp: continue
        vis = None  # lazy

        # actualtext — inline or StructElem /ActualText, term also visible
        if seen_cat.get("actualtext",0) < CAP:
            for m in re.findall(rb'/ActualText\s*\(((?:[^()\\]|\\.)*)\)', exp):
                tok = good_token(m.decode("latin-1","ignore"))
                if tok:
                    if vis is None: vis = mutext(f)
                    if tok in vis:
                        emit(f, tok, "hard", "actualtext", "term in /ActualText + visible glyphs"); break

        # cid-type0 — Type0+CIDFont, term extractable but not a literal in stream
        if seen_cat.get("cid-type0",0) < CAP and b"/Type0" in exp and b"/CIDFont" in exp:
            if vis is None: vis = mutext(f)
            for tok in re.findall(r'[A-Za-z][A-Za-z0-9]{5,18}', vis):
                if tok.encode() not in exp:   # absent as a literal -> CID-encoded
                    emit(f, tok, "hard", "cid-type0", "Type0/CIDFont; extracted but CID-encoded (byte!=char)"); break

        # acroform-value — text field /V
        if seen_cat.get("acroform-value",0) < CAP and b"/AcroForm" in exp:
            for m in re.findall(rb'/V\s*\(((?:[^()\\]|\\.)*)\)', exp):
                tok = good_token(m.decode("latin-1","ignore"))
                if tok: emit(f, tok, "hard", "acroform-value", "term in a form field /V"); break

        # freetext-annotation — annotation /Contents, term NOT in page text
        if seen_cat.get("freetext-annotation",0) < CAP and (b"/FreeText" in exp or b"/Annot" in exp):
            for m in re.findall(rb'/Contents\s*\(((?:[^()\\]|\\.)*)\)', exp):
                tok = good_token(m.decode("latin-1","ignore"))
                if tok:
                    if vis is None: vis = mutext(f)
                    if tok not in vis:
                        emit(f, tok, "hard", "freetext-annotation", "term in annotation /Contents, not page content"); break

        # outline-title — bookmark /Title token also in body (leak shape)
        if seen_cat.get("outline-title",0) < CAP and b"/Outlines" in exp:
            for m in re.findall(rb'/Title\s*\(((?:[^()\\]|\\.)*)\)', exp):
                tok = good_token(m.decode("latin-1","ignore"))
                if tok:
                    if vis is None: vis = mutext(f)
                    if tok in vis:
                        emit(f, tok, "hard", "outline-title", "term in outline /Title + body (AstraZeneca shape)"); break

        # invisible-text — text shown under render mode 3
        if seen_cat.get("invisible-text",0) < CAP and re.search(rb'\b3\s+Tr\b', exp):
            m = re.search(rb'3\s+Tr\b[^E]{0,120}?\(((?:[^()\\]|\\.)*)\)', exp)
            if m:
                tok = good_token(m.group(1).decode("latin-1","ignore"))
                if tok:
                    if vis is None: vis = mutext(f)
                    if tok in vis:
                        emit(f, tok, "hard", "invisible-text", "term drawn under 3 Tr (invisible), still extractable"); break

        # tj-kerning-heavy — a word split across >=4 TJ string pieces
        if seen_cat.get("tj-kerning-heavy",0) < CAP:
            for arr in re.findall(rb'\[((?:[^\[\]]){20,400}?)\]\s*TJ', exp):
                pieces = re.findall(rb'\(((?:[^()\\]|\\.)*)\)', arr)
                if len(pieces) >= 4:
                    word = b"".join(pieces).decode("latin-1","ignore")
                    tok = good_token(word)
                    if tok and len(pieces) >= 4:
                        emit(f, tok, "medium", "tj-kerning-heavy", f"word split across {len(pieces)} TJ pieces"); break

print(f"# candidates per category: {seen_cat}", file=sys.stderr)
