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
/// problem returns false so the caller falls back to dropping <c>/AP</c>, which
/// is leak-safe.</para>
/// </summary>
internal static class AppearanceStreamRedactor
{
    /// <summary>
    /// Rewrite the normal appearance (<c>/N</c>) of <paramref name="apDict"/> to
    /// remove <paramref name="term"/>. Returns true if it rewrote a stream and
    /// removed at least one occurrence.
    /// </summary>
    public static bool RedactTerm(
        PdfPage page, PdfDictionary apDict, PdfDictionary? defaultResources,
        string term, bool caseSensitive)
    {
        if (page.Document.Resolve(apDict.GetOptional("N") ?? PdfNull.Instance) is not PdfStream ap)
            return false;   // a /N that is a dict of states (buttons) has no readable text
        return RewriteStream(page, ap, defaultResources, term, caseSensitive);
    }

    private static bool RewriteStream(
        PdfPage page, PdfStream ap, PdfDictionary? defaultResources, string term, bool caseSensitive)
    {
        byte[] content;
        try { content = ap.DecodedData; }
        catch { return false; }
        if (content.Length == 0) return false;

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
        catch { return false; }
        if (parsed.Operators.Count == 0 || letters.Count == 0) return false;

        var matches = PdfDocumentRedactionExtensions.FindTextMatches(letters, term, caseSensitive);
        if (matches.Count == 0) return false;

        var areas = matches.Select(PdfDocumentRedactionExtensions.BoundingBoxOf).ToList();

        byte[] newBytes;
        try
        {
            var newOps = new GlyphRemover().ProcessOperations(parsed.Operators, letters, areas);
            newBytes = new ContentStreamWriter().Write(new ContentStream(newOps));
        }
        catch { return false; }

        // Replace the content, stored UNCOMPRESSED so the writer emits it
        // verbatim (the old /Filter no longer describes these bytes).
        ap.SetDecodedData(newBytes);
        ap.SetEncodedData(newBytes);
        ap.Remove("Filter");
        ap.Remove("DecodeParms");
        ap.SetInt("Length", newBytes.Length);
        return true;
    }
}
