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
        bool scrubDocumentCarriers = true)
    {
        if (page == null) throw new System.ArgumentNullException(nameof(page));

        area = area.Normalize();

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

        IReadOnlyList<ContentOperator> working = content.Operators;

        // Pass 1: text glyph removal (if there's any text on the page).
        var letters = page.Letters;
        if (letters.Count > 0)
        {
            var remover = new GlyphRemover();
            working = remover.ProcessOperations(working, letters, area, strategy);
        }

        // Pass 2: image XObject removal (#279). Walks the operator list
        // tracking CTM and drops image Do invocations whose transformed
        // unit-square AABB overlaps the redaction area.
        working = ImageRedactor.ProcessOperations(working, page, area, strategy, out _);
        ImageRedactor.PruneUnusedImageXObjects(page, working);

        page.SetContentStream(new ContentStream(working));
    }

    /// <summary>
    /// Redact multiple areas in a single pass. Each area is applied
    /// sequentially, so overlapping areas behave correctly.
    /// </summary>
    public static void RedactAreas(
        this PdfPage page,
        System.Collections.Generic.IEnumerable<PdfRectangle> areas,
        GlyphRemovalStrategy strategy = GlyphRemovalStrategy.AnyOverlap,
        bool scrubDocumentCarriers = true)
    {
        if (page == null) throw new System.ArgumentNullException(nameof(page));

        var list = new System.Collections.Generic.List<PdfRectangle>();
        foreach (var a in areas) list.Add(a.Normalize());
        if (list.Count == 0) return;
        if (list.Count == 1) { page.RedactArea(list[0], strategy, scrubDocumentCarriers); return; }

        // #919: this used to be `foreach (area) page.RedactArea(area)`, and each
        // RedactArea re-parsed the whole content stream, re-extracted every
        // letter (SetContentStream invalidates the cache) and rewrote the page.
        // Redacting a common word from a six-page form cost 10.6s for 544
        // matches — ~24ms of rework per match, and ~17ms per additional
        // rectangle even when the rectangle covered no glyphs at all.
        //
        // The per-area DECISIONS are unchanged; only the parse, the letter
        // extraction and the write are hoisted out of the loop.
        if (scrubDocumentCarriers)
            page.Document.ScrubMetadata(scrubAttachments: false);
        foreach (var area in list)
        {
            InteractiveRedactionScrubber.ScrubArea(page, area);
            StructureTreeRedactionScrubber.ScrubArea(page, area);
        }

        var content = page.GetContentStream();
        if (content.Operators.Count == 0) return;

        // Flattening rewrites the page, so it must happen before the letters
        // are taken — and any area may trigger it.
        foreach (var area in list)
        {
            if (FormXObjectFlattener.FlattenOverlapping(
                    page, content.Operators, area, out var flattened, out var inlinedForms))
            {
                page.SetContentStream(new ContentStream(flattened));
                content = page.GetContentStream();
                FormXObjectFlattener.PruneInlinedForms(page, content.Operators, inlinedForms);
                if (content.Operators.Count == 0) return;
            }
        }

        IReadOnlyList<ContentOperator> working = content.Operators;

        // ONE letter extraction for every area. `letters` describes the page as
        // it was; as areas remove glyphs, entries for already-removed glyphs
        // simply stop matching (LetterFinder matches on text), so later areas
        // see the shrinking text without needing a re-extract.
        var letters = page.Letters;
        var remover = new GlyphRemover();
        foreach (var area in list)
        {
            if (letters.Count > 0)
                working = remover.ProcessOperations(working, letters, area, strategy);
            working = ImageRedactor.ProcessOperations(working, page, area, strategy, out _);
        }

        ImageRedactor.PruneUnusedImageXObjects(page, working);
        page.SetContentStream(new ContentStream(working));
    }
}
