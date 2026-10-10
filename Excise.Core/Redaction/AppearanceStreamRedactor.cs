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
    /// #2059 — true only when <paramref name="ap"/> can be SHOWN not to draw
    /// <paramref name="term"/>, so a widget of a scrubbed field that never drew
    /// the term may keep its appearance. Not the negation of <see cref="Holds"/>:
    /// a lying <c>/ToUnicode</c> reads the term's codes as other letters (#2043),
    /// so "reads something else" proves nothing on its own. The proof needs all of:
    /// <list type="bullet">
    ///   <item>the stream parses, invokes no XObject (<c>Do</c>) and holds no
    ///     inline image (text there is out of this check's sight);</item>
    ///   <item>every <c>Tf</c> names a font in the stream's resources that is a
    ///     simple, non-Type3 font whose codes can be read twice: either through
    ///     its encoding alone (no <c>/ToUnicode</c>), or, when it has one, through
    ///     a standard encoding the codes themselves spell (no <c>/Differences</c>,
    ///     no embedded program's built-in encoding);</item>
    ///   <item>neither the shown codes read as Latin-1 nor the extracted letters
    ///     contain the term, compared loosely (letters and digits only, case
    ///     folded), so a formatted or spaced copy counts as holding it;</item>
    ///   <item>no letter is unmapped (empty, U+FFFD, a control).</item>
    /// </list>
    /// A stream that draws no text at all is free. Anything else is false:
    /// the caller then rewrites or drops, the leak-safe moves.
    /// </summary>
    internal static bool ProvablyFree(
        PdfPage page, PdfStream ap, PdfDictionary? defaultResources, string term)
    {
        var looseTerm = Loose(term);
        if (looseTerm.Length == 0) return false;
        var asciiTerm = looseTerm.All(char.IsAscii);
        try
        {
            var document = page.Document;
            var content = ap.DecodedData;
            var resources = document.Resolve(ap.GetOptional("Resources") ?? PdfNull.Instance)
                as PdfDictionary ?? defaultResources;
            var operators = new ContentStreamParser(content, page, resources).Parse().Operators;
            if (operators.Count == 0 && content.Any(b => b is not (0x20 or 0x0A or 0x0D or 0x09 or 0x0C or 0x00)))
                return false;   // bytes the parser made nothing of: cannot tell what a viewer draws

            var fonts = document.Resolve(resources?.GetOptional("Font") ?? PdfNull.Instance) as PdfDictionary;
            var fontSet = false;
            var shown = new System.Text.StringBuilder();
            foreach (var op in operators)
            {
                if (op.InlineImageData != null) return false;
                switch (op.Name)
                {
                    case "Do" or "BI" or "ID" or "EI":
                        return false;
                    case "Tf":
                        if (fonts == null || op.Operands.Count < 1 || op.Operands[0] is not PdfName name
                            || !CodesReadTwice(document, fonts.GetOptional(name.Value), asciiTerm))
                            return false;
                        fontSet = true;
                        break;
                    case "Tj" or "'" or "\"" or "TJ":
                        if (!fontSet) return false;
                        foreach (var operand in op.Operands)
                        {
                            if (operand is PdfString s)
                                shown.Append(System.Text.Encoding.Latin1.GetString(s.Bytes));
                            else if (operand is PdfArray array)
                                foreach (var item in array.OfType<PdfString>())
                                    shown.Append(System.Text.Encoding.Latin1.GetString(item.Bytes));
                        }
                        break;
                }
            }
            if (shown.Length == 0) return true;
            if (Loose(shown.ToString()).Contains(looseTerm, System.StringComparison.Ordinal)) return false;

            var letters = new TextExtractor(page) { IncludeFormFieldValues = false }
                .ExtractLettersFrom(content, resources);
            if (letters.Any(l => l.IsCidFont || string.IsNullOrEmpty(l.Value)
                                 || l.Value.Any(c => c == '�' || char.IsControl(c))))
                return false;
            return !Loose(string.Concat(letters.Select(l => l.Value))).Contains(looseTerm, System.StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>Letters and digits only, upper-cased: the comparison form of <see cref="ProvablyFree"/>.</summary>
    private static string Loose(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>
    /// A simple, non-Type3 font whose codes mean the same thing to the extractor
    /// and to a byte read: no <c>/ToUnicode</c> (the encoding is the only
    /// mapping), or a <c>/ToUnicode</c> over a standard encoding (named, or a
    /// dictionary without <c>/Differences</c>; a missing base encoding only for a
    /// font with no embedded program, whose built-in encoding is Standard). The
    /// byte read is Latin-1, which the standard encodings agree with only in
    /// ASCII: for a term with any other letter (<paramref name="asciiTerm"/>
    /// false) a <c>/ToUnicode</c> cannot be checked, and the font does not pass.
    /// </summary>
    private static bool CodesReadTwice(PdfDocument document, PdfObject? raw, bool asciiTerm)
    {
        if (raw == null || document.Resolve(raw) is not PdfDictionary font) return false;
        if (font.GetNameOrNull("Subtype") is not ("Type1" or "TrueType" or "MMType1")) return false;
        if (font.GetOptional("ToUnicode") == null) return true;
        if (!asciiTerm) return false;

        var embedded = document.Resolve(font.GetOptional("FontDescriptor") ?? PdfNull.Instance) is PdfDictionary descriptor
                       && (descriptor.GetOptional("FontFile") != null || descriptor.GetOptional("FontFile2") != null
                           || descriptor.GetOptional("FontFile3") != null);
        return document.Resolve(font.GetOptional("Encoding") ?? PdfNull.Instance) switch
        {
            PdfName name => name.Value is "WinAnsiEncoding" or "MacRomanEncoding" or "StandardEncoding",
            PdfDictionary encoding => encoding.GetOptional("Differences") == null
                                      && (encoding.GetOptional("BaseEncoding") != null || !embedded),
            _ => !embedded,
        };
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
