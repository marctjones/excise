#!/usr/bin/env python3
"""Generate the ADVERSARIAL redaction carrier corpus (#1183) (test-pdfs/redaction-adversarial/).

Sibling of gen-redaction-corpus.py, but a different question. That corpus stress-
tests the residue/de-redaction axis on ONE mechanism (a black box over text).
This one is a carrier CHECKLIST: one tiny fixture per place a secret can hide
that a redactor is likely to MISS -- invisible OCR text, a Form XObject, an
annotation, a form-field value, /ActualText, XMP, a bookmark title, per-glyph TJ
kerning, rotated text, a stacked duplicate.

It exists so the CROSS-TOOL bench (RedactionBenchmarkRunner) can survey pymupdf
and iText on the SAME traps CanaryInjectionLeakTests asserts on for excise --
descriptively, per the bench-is-a-survey rule. The point is to unveil where the
REFERENCE redactors leave the secret in a carrier they never scrub.

Each fixture names its secret in the filename (<carrier>--<TOKEN>.pdf) so the
bench redacts a KNOWN term instead of sampling visible text (the secret is often
in a carrier no extractor samples). Where realistic, the token ALSO appears in
visible page content, so every tool's find-step fires on the visible copy and
the survey shows whether the carrier copy survived the same redaction (#636/#608
are exactly this: a name in the body AND in /ActualText or XMP).
"""
import argparse, hashlib, json, os


def _pdf(objs):
    """Assemble numbered objects into a PDF with a correct xref (from gen-redaction-corpus.py)."""
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


def b(s):
    return s.encode("latin-1")


def stream(dict_body, data):
    d = data if isinstance(data, bytes) else b(data)
    return b(dict_body[:-2] + " /Length %d >>\nstream\n" % len(d)).replace(b" /Length", b" /Length") \
        if False else b(dict_body.rstrip(">").rstrip()) + b(" /Length %d >>\nstream\n" % len(d)) + d + b"\nendstream"


def content_obj(text):
    data = b(text)
    return b("<< /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"


# --- one builder per carrier -------------------------------------------------
# Each returns the full object list for a single-page PDF whose secret is TOKEN.

FONT = b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"


def base(content, extra_objs=None, page_extra="", catalog_extra=""):
    """Catalog(1) Pages(2) Page(3) Contents(4) Font(5) + extras from 6."""
    objs = [
        b("<< /Type /Catalog /Pages 2 0 R %s>>" % catalog_extra),
        b("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
          "/Resources << /Font << /F1 5 0 R >> %s>> /Contents 4 0 R %s>>"
          % (page_extra[0] if isinstance(page_extra, tuple) else "",
             page_extra[1] if isinstance(page_extra, tuple) else page_extra)),
        content_obj(content),
        FONT,
    ]
    if extra_objs:
        objs.extend(extra_objs)
    return objs


def c_invisible(t):
    # Secret ONLY in invisible (Tr 3) text -- the classic OCR-under-scan layer.
    return base("BT /F1 12 Tf 72 720 Td (Scanned page image) Tj ET\n"
                "BT /F1 12 Tf 3 Tr 72 700 Td (%s) Tj 0 Tr ET\n" % t)


def c_stacked(t):
    # Same string drawn twice at the same spot -- a duplicate to leave behind.
    return base("BT /F1 12 Tf 72 700 Td (%s) Tj ET\n"
                "BT /F1 12 Tf 72 700 Td (%s) Tj ET\n" % (t, t))


def c_tjperglyph(t):
    arr = "".join("(%s)18" % ch for ch in t)
    return base("BT /F1 12 Tf 72 700 Td [%s] TJ ET\n" % arr)


def c_rotated(t):
    return base("BT /F1 12 Tf 0.7071 0.7071 -0.7071 0.7071 250 400 Tm (%s) Tj ET\n" % t)


def c_formxobject(t):
    xobj = (b"<< /Type /XObject /Subtype /Form /BBox [0 0 300 20] "
            b"/Resources << /Font << /F1 5 0 R >> >>")
    data = b("BT /F1 12 Tf 0 4 Td (%s) Tj ET" % t)
    xobj = xobj + b(" /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"
    objs = base("BT /F1 12 Tf 72 730 Td (See attached block:) Tj ET\n"
                "q 1 0 0 1 72 700 cm /Fm0 Do Q\n",
                extra_objs=[xobj],
                page_extra="/XObject << /Fm0 6 0 R >> ".join(["", ""]) or "")
    # patch Resources to include the XObject
    objs[2] = b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                "/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 6 0 R >> >> "
                "/Contents 4 0 R >>")
    return objs


def c_shared_xobject(t):
    # One Form XObject (obj 7) Do'd from TWO pages (obj 3, obj 6). The secret is
    # inside the SHARED form, so redacting it on one page must not corrupt the
    # other page's use of the same object.
    data = b("BT /F1 12 Tf 0 4 Td (%s) Tj ET" % t)
    xobj = (b"<< /Type /XObject /Subtype /Form /BBox [0 0 300 20] "
            b"/Resources << /Font << /F1 5 0 R >> >>"
            + b(" /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream")
    pageres = ("/Resources << /Font << /F1 5 0 R >> /XObject << /Fm0 7 0 R >> >>")
    return [
        b("<< /Type /Catalog /Pages 2 0 R >>"),
        b("<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 2 >>"),
        b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + pageres + " /Contents 4 0 R >>"),
        content_obj("q 1 0 0 1 72 700 cm /Fm0 Do Q\n"),
        FONT,
        b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] " + pageres + " /Contents 8 0 R >>"),
        xobj,
        content_obj("q 1 0 0 1 72 500 cm /Fm0 Do Q\n"),
    ]


def c_annotation(t):
    ap = (b"<< /Type /XObject /Subtype /Form /BBox [0 0 300 20] "
          b"/Resources << /Font << /F1 5 0 R >> >>")
    data = b("BT /F1 12 Tf 2 4 Td (%s) Tj ET" % t)
    ap = ap + b(" /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"
    annot = b("<< /Type /Annot /Subtype /FreeText /Rect [72 690 372 710] "
              "/Contents (%s) /RC (%s) /AP << /N 7 0 R >> >>" % (t, t))
    objs = base("BT /F1 12 Tf 72 730 Td (Case note for %s below) Tj ET\n" % t,
                extra_objs=[annot, ap])
    objs[2] = b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R "
                "/Annots [6 0 R] >>")
    return objs


def c_acroform(t):
    ap = (b"<< /Type /XObject /Subtype /Form /BBox [0 0 300 20] "
          b"/Resources << /Font << /F1 5 0 R >> >>")
    data = b("BT /F1 12 Tf 2 4 Td (%s) Tj ET" % t)
    ap = ap + b(" /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"
    field = b("<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) "
              "/V (%s) /DV (%s) /Rect [72 690 372 710] /AP << /N 7 0 R >> "
              "/P 3 0 R >>" % (t, t))
    objs = base("BT /F1 12 Tf 72 730 Td (Applicant %s, form:) Tj ET\n" % t,
                extra_objs=[field, ap],
                catalog_extra="/AcroForm << /Fields [6 0 R] >> ")
    objs[2] = b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                "/Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R "
                "/Annots [6 0 R] >>")
    return objs


def c_actualtext(t):
    # Visible glyphs spell the secret; /ActualText restates it (the #636 carrier).
    return base("/Span << /ActualText (%s) >> BDC\n"
                "BT /F1 12 Tf 72 700 Td (%s) Tj ET\n"
                "EMC\n" % (t, t))


def c_xmp(t):
    xmp = ('<?xpacket begin="" id="W5M0MpCehiHzreSzNTczkc9d"?>'
           '<x:xmpmeta xmlns:x="adobe:ns:meta/"><rdf:RDF '
           'xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#">'
           '<rdf:Description xmlns:dc="http://purl.org/dc/elements/1.1/">'
           '<dc:title>%s</dc:title></rdf:Description></rdf:RDF></x:xmpmeta>'
           '<?xpacket end="w"?>' % t)
    data = b(xmp)
    meta = (b"<< /Type /Metadata /Subtype /XML /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"
    objs = base("BT /F1 12 Tf 72 700 Td (Title: %s) Tj ET\n" % t,
                extra_objs=[meta], catalog_extra="/Metadata 6 0 R ")
    return objs


def c_outline(t):
    objs = base("BT /F1 12 Tf 72 700 Td (Chapter: %s) Tj ET\n" % t,
                extra_objs=[
                    b("<< /Type /Outlines /First 7 0 R /Last 7 0 R /Count 1 >>"),
                    b("<< /Title (%s) /Parent 6 0 R /Dest [3 0 R /Fit] >>" % t),
                ],
                catalog_extra="/Outlines 6 0 R ")
    return objs



def c_alt_inline(t):
    # /Alt (alternate description) inline in a BDC property list, parallel to
    # /ActualText but the accessibility ALT text carrier.
    return base("/Span << /Alt (%s) >> BDC\n"
                "BT /F1 12 Tf 72 700 Td (%s) Tj ET\n"
                "EMC\n" % (t, t))



def c_ocg_hidden(t):
    # Optional-content group set OFF by default: the secret is drawn inside an
    # /OC marked-content region tied to a hidden layer — invisible on screen,
    # fully present and extractable.
    objs = base("BT /F1 12 Tf 72 730 Td (Visible line) Tj ET\n"
                "/OC /MC0 BDC\n"
                "BT /F1 12 Tf 72 700 Td (%s) Tj ET\n"
                "EMC\n" % t,
                extra_objs=[b"<< /Type /OCG /Name (Hidden) >>"],
                catalog_extra="/OCProperties << /OCGs [6 0 R] /D << /OFF [6 0 R] >> >> ")
    # page needs /Resources /Properties /MC0 -> the OCG
    objs[2] = b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] "
                "/Resources << /Font << /F1 5 0 R >> /Properties << /MC0 6 0 R >> >> "
                "/Contents 4 0 R >>")
    return objs


def c_embedded_file(t):
    # The secret lives in an attached embedded file stream (/EmbeddedFiles), not
    # the page — plus a visible body copy.
    data = b("secret note: %s\n" % t)
    ef = (b"<< /Type /EmbeddedFile /Length %d >>\nstream\n" % len(data)) + data + b"\nendstream"
    filespec = b("<< /Type /Filespec /F (note.txt) /EF << /F 7 0 R >> >>")
    objs = base("BT /F1 12 Tf 72 700 Td (Attachment holder %s) Tj ET\n" % t,
                extra_objs=[filespec, ef],
                catalog_extra="/Names << /EmbeddedFiles << /Names [(note.txt) 6 0 R] >> >> ")
    return objs



def c_image_baked(t):
    # The secret exists ONLY as pixels in a rasterised image — NO text layer.
    # Text extraction sees nothing; only OCR of the rendered page recovers it.
    # This is the #637 extraction-coverage bound in its starkest form: a term-
    # based redactor that does not OCR images cannot find or remove it.
    # Requires PIL; the main loop skips this carrier if PIL is unavailable.
    from PIL import Image, ImageDraw, ImageFont
    import zlib
    W, H = 1600, 240
    img = Image.new("L", (W, H), 255)
    dr = ImageDraw.Draw(img)
    fnt = None
    for p in ("/System/Library/Fonts/Supplemental/Arial.ttf",
              "/Library/Fonts/Arial.ttf",
              "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
              "/System/Library/Fonts/Helvetica.ttc"):
        try:
            fnt = ImageFont.truetype(p, 90); break
        except Exception:
            pass
    if fnt is None:
        fnt = ImageFont.load_default()
    dr.text((40, 60), "%s redact me" % t, fill=0, font=fnt)
    comp = zlib.compress(img.tobytes())
    PW, PH = 612, 92
    content = b("q %d 0 0 %d 0 0 cm /Im0 Do Q\n" % (PW, PH))
    imgobj = (b("<< /Type /XObject /Subtype /Image /Width %d /Height %d "
                "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode "
                "/Length %d >>\nstream\n" % (W, H, len(comp))) + comp + b"\nendstream")
    return [
        b("<< /Type /Catalog /Pages 2 0 R >>"),
        b("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] "
          "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>" % (PW, PH)),
        (b("<< /Length %d >>\nstream\n" % len(content)) + content + b"\nendstream"),
        imgobj,
    ]


def c_image_ocr_overlay(t):
    # #1192 / #1195 shape, but with a Flate image Excise.Core CAN decode — so the
    # region-level image-redaction path is testable without CCITT/JBIG2 codecs.
    # A FULL-PAGE Flate scan with the term baked into the pixels AND surrounding
    # baked text, under an invisible (Tr 3) OCR text layer positioned over the
    # term. Correct redaction: find the term via the invisible layer, then black
    # ONLY the term's rectangle in the image — deleting the whole image (current
    # behaviour) destroys the surrounding baked text (measurable collateral).
    # Requires PIL; the main loop skips this carrier if PIL is unavailable.
    from PIL import Image, ImageDraw, ImageFont
    import zlib
    W, H = 1224, 1584          # 2x the 612x792 page, full-page scan
    img = Image.new("L", (W, H), 255)
    dr = ImageDraw.Draw(img)
    fnt = None
    for fp in ("/System/Library/Fonts/Supplemental/Arial.ttf",
               "/Library/Fonts/Arial.ttf",
               "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
               "/System/Library/Fonts/Helvetica.ttc"):
        try:
            fnt = ImageFont.truetype(fp, 44); break
        except Exception:
            pass
    if fnt is None:
        fnt = ImageFont.load_default()
    # surrounding baked text (the collateral that whole-image deletion destroys)
    for i, line in enumerate([
            "PLAT OF SURVEY  -  parcel and easement schedule",
            "Bearings and distances per record of survey.",
            "Lot 14  Block 3   area 0.34 ac   zoning R-1",
            "Utility easement 10 ft along the rear line.",
            "Reference monument found at the NE corner."]):
        dr.text((80, 120 + i*90), line, fill=0, font=fnt)
    # the secret term, baked near the top-center
    term_px = (80, 700)
    dr.text(term_px, "%s LANE" % t, fill=0, font=fnt)
    comp = zlib.compress(img.tobytes())
    PW, PH = 612, 792
    # map baked term pixel position -> page coords for the invisible overlay.
    # image (cm PW PH) maps sample (sx,sy) [top-left origin] to page
    # (sx/W*PW, PH - sy/H*PH).
    tx = term_px[0] / W * PW
    ty = PH - term_px[1] / H * PH
    content = b("q %d 0 0 %d 0 0 cm /Im0 Do Q\n"
                "BT /F1 12 Tf 3 Tr %.1f %.1f Td (%s) Tj 0 Tr ET\n"
                % (PW, PH, tx, ty, t))
    imgobj = (b("<< /Type /XObject /Subtype /Image /Width %d /Height %d "
                "/ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode "
                "/Length %d >>\nstream\n" % (W, H, len(comp))) + comp + b"\nendstream")
    return [
        b("<< /Type /Catalog /Pages 2 0 R >>"),
        b("<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
        b("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 %d %d] "
          "/Resources << /Font << /F1 5 0 R >> /XObject << /Im0 6 0 R >> >> "
          "/Contents 4 0 R >>" % (PW, PH)),
        (b("<< /Length %d >>\nstream\n" % len(content)) + content + b"\nendstream"),
        FONT,
        imgobj,
    ]


CARRIERS = {
    "invisible-text":      ("INVISIBLESECRET",   c_invisible),
    "stacked-duplicate":   ("STACKEDSECRET",     c_stacked),
    "tj-perglyph":         ("TJGLYPHSECRET",     c_tjperglyph),
    "rotated-text":        ("ROTATEDSECRET",     c_rotated),
    "form-xobject":        ("XOBJECTSECRET",     c_formxobject),
    "annotation-contents": ("ANNOTCARRIERSECRET", c_annotation),
    "acroform-value":      ("FORMFIELDSECRET",   c_acroform),
    "actualtext":          ("ACTUALTEXTSECRET",  c_actualtext),
    "xmp-metadata":        ("XMPCARRIERSECRET",  c_xmp),
    "outline-title":       ("OUTLINESECRET",     c_outline),
    "alt-inline":          ("ALTSECRET",         c_alt_inline),
    "ocg-hidden":          ("OCGSECRET",         c_ocg_hidden),
    "embedded-file":       ("EMBEDDEDSECRET",    c_embedded_file),
    "shared-form-xobject": ("SHAREDXOBJSECRET",  c_shared_xobject),
    "image-baked-text":    ("IMAGEBAKEDSECRET",  c_image_baked),
    "image-ocr-overlay":   ("IMAGEOCROVERLAYSECRET", c_image_ocr_overlay),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(__file__), "..",
                                                   "test-pdfs", "redaction-adversarial"))
    args = ap.parse_args()
    out = os.path.abspath(args.out)
    os.makedirs(out, exist_ok=True)

    manifest = []
    for carrier, (token, fn) in sorted(CARRIERS.items()):
        try:
            pdf = _pdf(fn(token))
        except ImportError as e:
            # image-baked-text needs PIL to rasterise. Skip gracefully — the design
            # marks it synth, and check-bench-coverage.py will report it absent.
            print(f"skip {carrier}: {e}")
            continue
        # Guard: the token must be reachable before redaction. tj-perglyph splits
        # it per-glyph BY DESIGN; image-baked-text renders it to PIXELS (no byte
        # copy at all — that IS the trap). Both are exempt from byte presence.
        if carrier == "tj-perglyph":
            assert all(ch.encode() in pdf for ch in token), f"{carrier}: glyph missing"
        elif carrier == "image-baked-text":
            assert token.encode() not in pdf, f"{carrier}: token leaked as bytes (should be pixels only)"
        else:
            assert token.encode() in pdf, f"{carrier}: token not in fixture bytes"
        fname = f"{carrier}--{token}.pdf"
        open(os.path.join(out, fname), "wb").write(pdf)
        manifest.append({"id": carrier, "carrier": carrier, "token": token,
                         "file": fname, "bytes": len(pdf)})

    with open(os.path.join(out, "manifest.jsonl"), "w") as f:
        for m in manifest:
            f.write(json.dumps(m) + "\n")

    h = hashlib.sha256("\n".join(sorted(
        f"{m['id']}|{m['token']}|{m['bytes']}" for m in manifest)).encode()).hexdigest()[:16]
    open(os.path.join(out, ".manifest-hash"), "w").write(h + "\n")

    print(f"generated {len(manifest)} adversarial carrier fixtures -> {out}")
    print(f"manifest hash: {h}")
    for m in manifest:
        print(f"  {m['carrier']:22} {m['token']:20} {m['bytes']:5}B")


if __name__ == "__main__":
    main()
