using System.Collections.Generic;
using System.Linq;
using Excise.Core.Content;
using Excise.Core.Document;
using Excise.Core.Primitives;
using Excise.Core.Text;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// #1098 — rewrite a form field's <c>/AP</c> appearance stream to remove a
/// redacted term's GLYPHS, instead of dropping the appearance and relying on a
/// reader to regenerate it from <c>/NeedAppearances</c> (which non-Acrobat
/// viewers ignore, leaving an empty field).
///
/// <para>An appearance stream is an ordinary Form XObject content stream, so the
/// glyph-removal engine applies directly. Because the TERM is known there is no
/// coordinate mapping to get wrong: extract the appearance's own letters (in its
/// own coordinate space, using its own <c>/Resources</c>), match the term by
/// text, and remove exactly those glyphs. Fails CLOSED — any parse/rewrite
/// problem returns null so the caller falls back to dropping <c>/AP</c>, which
/// is leak-safe. The caller writes the result (#2041: into a copy, see
/// <see cref="InteractiveRedactionScrubber"/>).</para>
/// </summary>
internal static class AppearanceStreamRedactor
{
    /// <summary>
    /// #2041 — true when <paramref name="ap"/>'s own glyphs read
    /// <paramref name="term"/>, by the search redaction uses. False when they do
    /// not, or when the stream cannot be read.
    /// </summary>
    internal static bool Holds(
        PdfPage page, PdfStream ap, PdfDictionary? defaultResources, string term, bool caseSensitive,
        bool wholeWord = false)
    {
        try
        {
            var resources = page.Document.Resolve(ap.GetOptional("Resources") ?? PdfNull.Instance)
                as PdfDictionary ?? defaultResources;
            var letters = new TextExtractor(page) { IncludeFormFieldValues = false }
                .ExtractLettersFrom(ap.DecodedData, resources);
            return PdfDocumentRedactionExtensions.FindTextMatches(letters, term, caseSensitive, wholeWord).Count > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// #2041 — the content of <paramref name="ap"/> with every occurrence of
    /// <paramref name="term"/> cut out of its glyphs, WITHOUT touching the
    /// stream (the caller decides whether to write it in place or into a copy:
    /// an appearance can be shared by widgets this redaction does not scrub).
    /// Null when the stream cannot be read, holds no occurrence, or still reads
    /// the term after the rewrite: the caller then drops the appearance, the
    /// leak-safe move. Glyphs are matched in the stream's own space, so text
    /// drawn outside the widget's <c>/Rect</c> or clipped by its <c>/BBox</c>
    /// (a scrolled multiline field) is cut exactly like the visible lines.
    /// </summary>
    internal static byte[]? RewrittenContent(
        PdfPage page, PdfStream ap, PdfDictionary? defaultResources, string term, bool caseSensitive,
        bool wholeWord = false)
    {
        byte[] content;
        try { content = ap.DecodedData; }
        catch { return null; }
        if (content.Length == 0) return null;

        // The appearance's own /Resources; fall back to the AcroForm /DR that a
        // producer may share across every field rather than duplicate per stream.
        var resources = page.Document.Resolve(ap.GetOptional("Resources") ?? PdfNull.Instance)
            as PdfDictionary ?? defaultResources;

        ContentStream parsed;
        IReadOnlyList<Letter> letters;
        try
        {
            parsed = new ContentStreamParser(content, page, resources).Parse();
            // IncludeFormFieldValues off: we want THIS stream's glyphs, not the
            // page's synthetic AcroForm letters.
            letters = new TextExtractor(page) { IncludeFormFieldValues = false }
                .ExtractLettersFrom(content, resources);
        }
        catch { return null; }
        if (parsed.Operators.Count == 0 || letters.Count == 0) return null;

        var matches = PdfDocumentRedactionExtensions.FindTextMatchLines(letters, term, caseSensitive, wholeWord);
        if (matches.Count == 0) return null;

        // One box per line of a match that wraps (#1791).
        var areas = matches.SelectMany(m => m.Lines)
            .Select(PdfDocumentRedactionExtensions.BoundingBoxOf).ToList();

        byte[] newBytes;
        try
        {
            var newOps = new GlyphRemover().ProcessOperations(parsed.Operators, letters, areas);
            newBytes = new ContentStreamWriter().Write(new ContentStream(newOps));
        }
        catch { return null; }

        // Verify the term is actually GONE from the rewritten stream — not by
        // comparing bytes (re-serialisation reformats whitespace even when
        // nothing was removed), but by re-extracting. If the term survives, it
        // is drawn by a nested Form XObject this stream only invokes with Do (a
        // signature /AP/N → /FRM Do, #669): the split can't reach it. Do NOT
        // claim success — return null so the caller drops /AP (leak-safe),
        // which prunes the nested form and takes the term with it. #2041: ANY
        // occurrence left is a failure, not only "none removed": a partial
        // rewrite kept the rest of the term in the file while the caller
        // treated the appearance as clean.
        try
        {
            var after = new TextExtractor(page) { IncludeFormFieldValues = false }
                .ExtractLettersFrom(newBytes, resources);
            if (PdfDocumentRedactionExtensions.FindTextMatches(after, term, caseSensitive, wholeWord).Count > 0)
                return null;
        }
        catch { return null; }

        return newBytes;
    }
}
