#!/usr/bin/env bash
#
# Cluster the corpus scan's DEFECT pages by root-cause signature.
#
# Three classes count as defects (see the agreement vocabulary in
# run-exploratory-corpus.sh):
#
#   EXCISE_SIDE_GAP   an oracle rendered the page and excise produced nothing
#   MISSING_CONTENT   excise rendered, but drew no ink in ANY tile the
#                     most-inked oracle drew in
#   DIFF              both rendered and no oracle agreed
#
# Everything else is either agreement, a corroborated refusal, or a question
# about the oracles rather than about excise.
#
# The point of clustering is that 37 defect pages are not 37 bugs. They are a
# handful of causes with a long tail, and filing them one per page would bury
# the two or three that matter. Signatures are the error message with digits
# normalised, so "Expected 'endobj', got '8' at position 623" and the same at
# position 4471 land together.
#
# Usage:
#   ./scripts/cluster-corpus-defects.sh            # all four all-pages reports
#   ./scripts/cluster-corpus-defects.sh --first    # the page-1 reports instead
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT" || exit 1
BIN="$ROOT/Excise.Rendering.Tests/bin/Debug/net10.0"

SUFFIX="all"
[ "${1:-}" = "--first" ] && SUFFIX="first"

python3 - "$BIN" "$SUFFIX" <<'PY'
import json, os, sys, re, collections

bin_dir, suffix = sys.argv[1], sys.argv[2]
if suffix == "all":
    files = [('pdf.js', 'exploratory-report-all.json'),
             ('veraPDF', 'exploratory-report-test-pdfs-verapdf-corpus-all.json'),
             ('Isartor', 'exploratory-report-test-pdfs-isartor-all.json'),
             ('PDFium', 'exploratory-report-test-pdfs-pdfium-all.json')]
else:
    files = [('pdf.js', 'exploratory-report.json'),
             ('veraPDF', 'exploratory-report-test-pdfs-verapdf-corpus-first.json'),
             ('Isartor', 'exploratory-report-test-pdfs-isartor-first.json'),
             ('PDFium', 'exploratory-report-test-pdfs-pdfium-first.json')]

ORACLES = [('mutool', 'mutoolStatus'), ('pdftocairo', 'cairoStatus'),
           ('ghostscript', 'ghostscriptStatus'), ('pdfbox', 'pdfboxStatus'),
           ('pdfium', 'pdfiumStatus')]
CRED = {"PASSWORD_REQUIRED", "UNSUPPORTED_ENCRYPTED"}

def classify(x):
    s = x.get('status')
    if s == 'PASS':            return 'PASS'
    if s == 'PASS_ONE':        return 'ORACLE_SPLIT'
    if s == 'DIFF':            return 'DIFF'
    if s == 'MISSING_CONTENT': return 'MISSING_CONTENT'
    states = [x.get(k) for _, k in ORACLES]
    ok = any(v == 'OK' for v in states)
    attempted = any(v is not None for v in states)
    if x.get('renderMs') is not None:
        return 'EXCISE_ONLY' if not ok else 'RENDERED'
    if s in CRED:              return 'CREDENTIAL_BLOCKED'
    if ok:                     return 'EXCISE_SIDE_GAP'
    return 'AGREED_REFUSAL' if attempted else 'UNCORROBORATED'


CORPUS_DIR = {'pdf.js': 'test-pdfs/pdfjs', 'veraPDF': 'test-pdfs/verapdf-corpus',
              'Isartor': 'test-pdfs/isartor', 'PDFium': 'test-pdfs/pdfium'}

def ClassifyMissingContent(label, x):
    """What does this page use that excise drew nothing for?

    Ordered most-specific first, because a page can carry several of these and
    the first match is the one worth filing against. Streams are inflated when
    the raw bytes give nothing away — an earlier version of this only regexed
    the compressed file and dumped 15 pages into 'other', which is a statement
    about the detector rather than about the pages.
    """
    import zlib
    rel = x.get('path') or ''
    path = os.path.join(CORPUS_DIR.get(label, ''), rel)
    if not os.path.exists(path):
        return 'file not found'
    d = open(path, 'rb').read()

    def has(pat):
        return re.search(pat, d) is not None

    if has(rb'/JBIG2Decode'):
        return 'JBIG2Decode (#874)'
    # The annotation subtypes are a long list (PDF 32000-1 Table 169) and an
    # incomplete one silently mislabels pages: RichMedia/3D/Screen were missing
    # here and sent a 10.8 MB veraPDF rich-media fixture to "needs manual
    # inspection". /Annots is the reliable signal that a page HAS annotations at
    # all, so fall back to it rather than enumerating forever.
    ANNOT_SUBTYPES = (rb'/Subtype\s*/(Widget|Ink|Line|Polygon|PolyLine|Square|Circle|FreeText'
                      rb'|Highlight|Stamp|Popup|RichMedia|3D|Screen|Movie|Sound|FileAttachment'
                      rb'|Caret|StrikeOut|Underline|Squiggly|Text|Redact|Watermark|PrinterMark)')
    if has(ANNOT_SUBTYPES):
        return ('annotation WITH /AP (#885)' if has(rb'/AP\s*<<')
                else 'annotation WITHOUT /AP (#885)')
    if has(rb'/Subtype\s*/Type3'):
        return 'Type3 font'
    if has(rb'/FontFile2|/FontFile3|/FontFile[^0-9]'):
        return 'embedded font, glyphs not rasterised (#886)'
    if has(rb'/JPXDecode'):
        return 'JPXDecode (JPEG 2000)'
    if has(rb'/CCITTFaxDecode'):
        return 'CCITTFaxDecode'
    if has(rb'/SMask'):
        return 'SMask / soft mask'
    if has(rb'/ShadingType|/PatternType'):
        return 'shading or pattern'
    if has(rb'/DCTDecode'):
        return 'DCTDecode image'

    # WEAK SIGNAL, deliberately last of the raw checks: a page merely HAVING
    # /Annots does not mean the annotation is what failed to draw. Everything
    # more specific — JBIG2, a named annotation subtype, Type3, an embedded font
    # — has already had its chance above, so reaching here means annotations are
    # the only distinguishing feature left.
    ANNOTS_FALLBACK = rb'/Annots'

    # INLINE IMAGES use abbreviated keys (BI ... ID ... EI, PDF 32000-1 §8.9.7)
    # and share none of the spellings above: /BPC not /BitsPerComponent, /CS not
    # /ColorSpace, /DCT not /DCTDecode. Checked before inflation because the
    # abbreviations sit in the raw content stream.
    if has(rb'\bBI\b[^\n]{0,200}?\bID\b') or (has(rb'/BPC') and has(rb'/CS')):
        return 'inline image (BI/ID/EI)'

    # Nothing obvious in the raw bytes. Inflate every stream and look again —
    # content and resources are routinely Flate-compressed, and object streams
    # (/Type /ObjStm, betrayed by /First) hide the page and annotation
    # dictionaries entirely. The earlier version decompressed one fixed-size
    # slice per stream and therefore missed both, which is how 22 pages ended up
    # labelled "needs manual inspection" when the file said what they were.
    inflated = b''
    for m in re.finditer(rb'stream\r?\n', d):
        chunk = d[m.end():]
        try:
            inflated += zlib.decompressobj().decompress(chunk)
        except Exception:
            pass
    if inflated:
        if re.search(ANNOT_SUBTYPES, inflated) or re.search(rb'/Annots', inflated):
            return ('annotation WITH /AP (#885)'
                    if re.search(rb'/AP\s*<<', inflated) or has(rb'/AP\s*<<')
                    else 'annotation WITHOUT /AP (#885)')
    if inflated:
        if re.search(rb'/Subtype\s*/Type3', inflated):
            return 'Type3 font (in compressed object)'
        if re.search(rb'/FontFile', inflated):
            return 'embedded font, glyphs not rasterised (#886)'
        if re.search(rb'/ShadingType|/PatternType', inflated):
            return 'shading or pattern (in compressed object)'
        if re.search(rb'\bTj\b|\bTJ\b|\bTf\b', inflated):
            return 'text operators present, no glyphs drawn'
        if re.search(rb'\bDo\b', inflated):
            return 'XObject invoked, nothing drawn'
        if re.search(rb'\bre\b.*\b[fFbB]\b', inflated, re.S):
            return 'vector fill present, nothing drawn'
    if has(ANNOTS_FALLBACK) or (inflated and re.search(ANNOTS_FALLBACK, inflated)):
        return ('annotation WITH /AP (#885)' if has(rb'/AP\s*<<')
                else 'annotation WITHOUT /AP (#885)')
    return 'unclassified — needs manual inspection'


rows, total = [], 0
for label, f in files:
    p = os.path.join(bin_dir, f)
    if not os.path.exists(p):
        print(f"  (missing report: {f})")
        continue
    for x in json.load(open(p)).get('entries') or []:
        total += 1
        c = classify(x)
        if c in ('EXCISE_SIDE_GAP', 'MISSING_CONTENT', 'DIFF'):
            rows.append((label, c, x))

print(f"  {total} pages scanned, {len(rows)} defect pages\n")

for cls in ('EXCISE_SIDE_GAP', 'MISSING_CONTENT', 'DIFF'):
    sub = [(l, x) for l, c, x in rows if c == cls]
    print("=" * 78)
    print(f"  {cls}: {len(sub)} page(s)")
    print("=" * 78)
    if not sub:
        print("    (none)\n")
        continue

    groups = collections.defaultdict(list)
    for label, x in sub:
        if cls == 'EXCISE_SIDE_GAP':
            key = re.sub(r'\d+', 'N', (x.get('errorMessage') or '').strip())[:66] or x.get('status')
        elif cls == 'MISSING_CONTENT':
            # No exception to group by — excise rendered, it just drew nothing.
            # "The page is blank" is the symptom; the actionable axis is what
            # the page actually uses, so inspect the file.
            key = ClassifyMissingContent(label, x)
        else:
            key = f"rank {x.get('exciseReferenceCenterRank')} of {(x.get('comparedOracles') or 0)+1}, oracles split {x.get('oracleDisagreeingPairs')}/{x.get('oracleComparisonPairs')}"
        groups[key].append((label, x))

    for key, items in sorted(groups.items(), key=lambda kv: -len(kv[1])):
        corpora = collections.Counter(l for l, _ in items)
        print(f"\n    [{len(items):3}]  {key}")
        print(f"           corpora: {dict(corpora)}")
        for label, x in items[:6]:
            oks = ",".join(n for n, k in ORACLES if x.get(k) == 'OK')
            extra = ""
            if cls == 'MISSING_CONTENT':
                extra = f"  missing={x.get('missingInkTiles')}/{x.get('referenceInkedTiles')}"
            elif cls == 'DIFF':
                extra = f"  diff={x.get('diffFraction'):.4f}" if x.get('diffFraction') is not None else ""
            print(f"             {label:8} {(x.get('path') or '')[-46:]:48}#p{x.get('pageNumber')}{extra}")
            if cls == 'EXCISE_SIDE_GAP':
                print(f"                      rendered by: {oks or 'none'}")
        if len(items) > 6:
            print(f"             ... and {len(items) - 6} more")
    print()
PY
