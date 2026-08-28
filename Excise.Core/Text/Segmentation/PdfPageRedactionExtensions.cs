using System.Collections.Generic;
using Excise.Core.Content;
using Excise.Core.Document;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// Public entry point for glyph-level redaction on a <see cref="PdfPage"/>.
/// Removes the characters whose glyph bounding boxes fall inside the given
/// area from the page's content stream — text-extraction tools reading the
/// resulting PDF will see no trace of the removed glyphs.
/// </summary>
public static class PdfPageRedactionExtensions
{
    /// <summary>
    /// Redact all glyphs overlapping <paramref name="area"/>.
    /// </summary>
    /// <param name="page">The page to mutate.</param>
    /// <param name="area">Area in content-stream coordinates. For rotated
    /// pages, callers should pre-transform visual coordinates into
    /// content-stream space before invoking.</param>
    /// <param name="strategy">How to decide whether a given glyph counts as
    /// inside the redaction area. Defaults to the most conservative option
    /// (any-overlap) — appropriate for privacy work where a partial hit
    /// still leaks information.</param>
    /// <param name="scrubDocumentCarriers">Strip the document-level text
    /// carriers that have no position — <c>/Info</c> and the XMP
    /// <c>/Metadata</c> packet — on by default (#897). See the remarks for why
    /// this is a WHOLESALE strip and not the term-based scrub
    /// <see cref="PdfDocumentRedactionExtensions.RedactText"/> uses.</param>
    /// <remarks>
    /// Side-effect: the page's <c>/Contents</c> stream is rewritten.
    /// Subsequent calls to <see cref="PdfPage.Letters"/> will re-extract
    /// against the new content. Call <see cref="PdfDocument.Save(string)"/>
    /// on the owning document to persist.
    ///
    /// <para>
    /// Second side-effect, deliberate and on by default: the owning DOCUMENT's
    /// <c>/Info</c> keys and XMP packet are removed. A page-scoped call with a
    /// document-scoped effect deserves the explanation:
    /// </para>
    /// <para>
    /// <b>Why the asymmetry with RedactText.</b> <c>RedactText</c> scrubs
    /// carriers BY TERM because it has one — the caller typed it. An area
    /// redaction has only a rectangle. Deriving terms from the glyphs inside it
    /// and substring-deleting those from every metadata value actively damages
    /// the document: a box over one ordinary sentence yields terms like
    /// <c>you got time file</c>, which turn <c>Younger</c> into <c>Ynger</c> and
    /// <c>profile</c> into <c>pro</c>. So: <b>RedactText scrubs by term because
    /// it has one; RedactArea strips wholesale because it does not.</b> You
    /// cannot name what was in the box, so remove the carriers rather than
    /// guess at their contents (#897).
    /// </para>
    /// <para>
    /// <b>Not covered here.</b> Outline (bookmark) titles are left alone —
    /// wholesale-destroying a document's navigation because one box was drawn
    /// on one page is disproportionate, and no positional rule is honest (a
    /// bookmark naming the redacted text can point at any page). Stated as a
    /// known gap in #897 rather than half-solved. Annotations ARE handled, but
    /// positionally, by <c>InteractiveRedactionScrubber</c> — which reaches
    /// only annotations on THIS page overlapping THIS box.
    /// </para>
    /// <para>
    /// Embedded files are NOT dropped: <c>ScrubMetadata(scrubAttachments:
    /// false)</c>. Attachment removal is a wider promise than this parameter
    /// makes, and it already has its own home in the GUI's redacted-copy flow
    /// (<c>RedactedCopySafetyService</c>) and under <c>RemoveAllMetadata</c>.
    /// </para>
    /// </remarks>
    public static void RedactArea(
        this PdfPage page,
        PdfRectangle area,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool scrubDocumentCarriers = true,
        bool closeWidth = false)   // #1145 — opt-in width-closing
        => page.RedactAreaInternal(area, area, strategy, scrubDocumentCarriers, closeWidth);

    /// <summary>
    /// Core single-area redaction. <paramref name="area"/> drives the text /
    /// carrier / glyph passes; <paramref name="imageArea"/> drives the image
    /// pass (#1195). They differ only when the caller has a thin glyph-match
    /// centreline for text but the full glyph bbox for images — see
    /// <c>RedactText</c>. Public <see cref="RedactArea"/> passes the same rect
    /// for both.
    /// </summary>
    internal static void RedactAreaInternal(
        this PdfPage page,
        PdfRectangle area,
        PdfRectangle imageArea,
        GlyphRemovalStrategy strategy,
        bool scrubDocumentCarriers,
        bool closeWidth)
    {
        if (page == null) throw new System.ArgumentNullException(nameof(page));

        area = area.Normalize();
        imageArea = imageArea.Normalize();

        // Positionless document-level carriers (#897). Idempotent — RedactAreas
        // applies it once per rectangle — because both underlying operations
        // are removals of keys that may already be absent.
        if (scrubDocumentCarriers)
            page.Document.ScrubMetadata(scrubAttachments: false);
        InteractiveRedactionScrubber.ScrubArea(page, area);

        // Structure-tree carriers (#636). Must run BEFORE the content stream is
        // rewritten: it reads the page's original operators and letters to learn
        // which /MCID spans the area covers and which words are about to vanish.
        // A tagged PDF restates the same text in /ActualText, /Alt and /E, and
        // those survive glyph removal untouched — leaving a document whose
        // glyphs are gone, whose text extraction reports clean, and whose
        // structure tree still spells out the redacted name to Acrobat and to
        // every screen reader.
        StructureTreeRedactionScrubber.ScrubArea(page, area);

        var content = page.GetContentStream();

        // Short-circuit on empty pages — no ops means no work, and building
        // an empty content stream would overwrite any (legal-but-empty)
        // stream that was there.
        if (content.Operators.Count == 0) return;

        // Pass 0: flatten Form XObjects overlapping the area (#355). Form
        // content streams are invisible to the text/image passes below, so a
        // form drawing over the redaction area would only be covered, not
        // removed. Inlining the form into the page stream — and re-extracting
        // letters from it — is what lets the glyph pass find and delete that
        // text. Idempotent: a second RedactArea call sees no forms left.
        if (FormXObjectFlattener.FlattenOverlapping(
                page, content.Operators, area, out var flattened, out var inlinedForms))
        {
            page.SetContentStream(new ContentStream(flattened));
            content = page.GetContentStream(); // re-parse: bounds + letters now in page space
            // Drop the now-orphaned form objects so the writer can't re-emit
            // their content — flattening alone would leak the redacted text,
            // since the writer serializes every in-use object (no GC).
            FormXObjectFlattener.PruneInlinedForms(page, content.Operators, inlinedForms);
            if (content.Operators.Count == 0) return;
        }

        // Pass 0.5: inline marked-content carriers (#1182/#1185). /ActualText,
        // /Alt, /E in a content-stream BDC/DP property list survive glyph removal —
        // the structure-tree scrubber reaches only StructElem carriers, not the
        // inline form. Runs BEFORE the glyph pass, on the SAME operator list that
        // still carries the glyph bounding boxes, so a carrier span is matched by
        // the glyphs it ENCLOSES (/ActualText substitutes text that differs from
        // the painted glyphs, so content-matching alone misses it). The dicts are
        // mutated in place and flow through the glyph pass into the written stream.
        MarkedContentCarrierScrubber.Scrub(content.Operators, page, area);

        IReadOnlyList<ContentOperator> working = content.Operators;

        // Pass 1: text glyph removal (if there's any text on the page).
        var letters = page.Letters;
        if (letters.Count > 0)
        {
            var remover = new GlyphRemover { CloseWidth = closeWidth };
            working = remover.ProcessOperations(working, letters, area, strategy);
        }

        // Pass 2: image XObject redaction (#279, region-level #1195). Uses
        // imageArea (the full glyph bbox), NOT the possibly-thin glyph-match
        // area, so region blackout covers the term's visible extent.
        working = ImageRedactor.ProcessOperations(working, page, imageArea, strategy, out _);
        ImageRedactor.PruneUnusedImageXObjects(page, working);

        page.SetContentStream(new ContentStream(working));
    }

    /// <summary>
    /// Redact multiple areas in one glyph-reconstruction pass.
    /// </summary>
    public static void RedactAreas(
        this PdfPage page,
        System.Collections.Generic.IEnumerable<PdfRectangle> areas,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool scrubDocumentCarriers = true,
        bool closeWidth = false)   // #1145 — opt-in width-closing
    {
        var list = areas.Select(a => a.Normalize()).ToList();
        page.RedactAreasInternal(list, list, strategy, scrubDocumentCarriers, closeWidth);
    }

    /// <summary>
    /// Core multi-area redaction. <paramref name="glyphAreas"/> drive the text /
    /// carrier / glyph passes; <paramref name="imageAreas"/> (index-aligned)
    /// drive the image pass (#1195). Public <see cref="RedactAreas"/> passes the
    /// same list for both; <c>RedactText</c> passes thin glyph centrelines and
    /// full glyph bboxes respectively.
    /// </summary>
    internal static void RedactAreasInternal(
        this PdfPage page,
        System.Collections.Generic.IReadOnlyList<PdfRectangle> glyphAreas,
        System.Collections.Generic.IReadOnlyList<PdfRectangle> imageAreas,
        GlyphRemovalStrategy strategy,
        bool scrubDocumentCarriers,
        bool closeWidth)
    {
        if (page == null) throw new System.ArgumentNullException(nameof(page));

        var list = glyphAreas.Select(a => a.Normalize()).ToList();
        var imageList = imageAreas.Select(a => a.Normalize()).ToList();
        if (list.Count == 0) return;
        if (list.Count == 1)
        {
            page.RedactAreaInternal(
                list[0], imageList.Count > 0 ? imageList[0] : list[0],
                strategy, scrubDocumentCarriers, closeWidth);
            return;
        }

        if (scrubDocumentCarriers)
            page.Document.ScrubMetadata(scrubAttachments: false);
        foreach (var area in list)
        {
            InteractiveRedactionScrubber.ScrubArea(page, area);
            StructureTreeRedactionScrubber.ScrubArea(page, area);
        }

        var content = page.GetContentStream();
        if (content.Operators.Count == 0) return;

        foreach (var area in list)
        {
            if (!FormXObjectFlattener.FlattenOverlapping(
                    page, content.Operators, area, out var flattened, out var inlinedForms))
                continue;

            page.SetContentStream(new ContentStream(flattened));
            content = page.GetContentStream();
            FormXObjectFlattener.PruneInlinedForms(page, content.Operators, inlinedForms);
            if (content.Operators.Count == 0) return;
        }

        // Pass 0.5: inline marked-content carriers (#1182/#1185), per area, BEFORE
        // the glyph pass — see the single-area path for why enclosure not content.
        foreach (var area in list)
            MarkedContentCarrierScrubber.Scrub(content.Operators, page, area);

        IReadOnlyList<ContentOperator> working = content.Operators;
        var letters = page.Letters;
        if (letters.Count > 0)
        {
            var remover = new GlyphRemover { CloseWidth = closeWidth };
            working = remover.ProcessOperations(working, letters, list, strategy);
        }

        // Image pass uses imageList (full glyph bboxes), not the glyph-match
        // areas — see RedactAreaInternal (#1195).
        foreach (var imageArea in imageList)
            working = ImageRedactor.ProcessOperations(working, page, imageArea, strategy, out _);

        ImageRedactor.PruneUnusedImageXObjects(page, working);
        page.SetContentStream(new ContentStream(working));
    }
}
