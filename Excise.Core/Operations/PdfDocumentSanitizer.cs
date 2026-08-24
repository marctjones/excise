using System.Text;
using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Operations;

/// <summary>
/// Removes redacted terms from document-level text carriers (issue #608).
/// </summary>
/// <remarks>
/// Glyph removal is only as strong as the weakest carrier still holding the
/// string. A document routinely restates the text of its own pages in places
/// the content stream knows nothing about:
///
///   /Info          — Title, Author, Subject, Keywords
///   /Metadata      — the XMP packet, which is plain-text XML
///   /AcroForm /XFA — form-template and form-data XML packets
///   /Outlines      — bookmark titles, shown in the reader's sidebar
///   annotation /Contents — comment and markup text
///
/// A redacted name surviving in a bookmark title is visible in the navigation
/// pane without even opening the page. None of these carriers are reachable by
/// text extraction, so a content-stream assertion reports the document clean.
///
/// <para>
/// Scrubbing is <b>surgical</b>: only the offending substring is excised, and
/// unrelated values are left alone. The tempting alternative — deleting /Info,
/// /Metadata and /Outlines wholesale — would satisfy every leak assertion while
/// destroying the document's metadata and navigation. Callers that genuinely
/// want scorched earth should strip those dictionaries explicitly.
/// </para>
/// <para>
/// This is the DOCUMENT-level half of redaction. The PAGE-level half (content
/// stream, annotations, form fields, structure tree) is handled by
/// <c>PdfPageRedactionExtensions.RedactArea</c>.
/// </para>
/// </remarks>
public static class PdfDocumentSanitizer
{
    private static readonly string[] InfoKeys =
        { "Title", "Author", "Subject", "Keywords", "Creator", "Producer" };

    /// <summary>
    /// Shortest term we will act on. Excising one- and two-character fragments
    /// from every metadata string would corrupt unrelated values for no security
    /// benefit.
    /// </summary>
    private const int MinTermLength = 3;

    /// <summary>
    /// Remove every occurrence of <paramref name="terms"/> from the document's
    /// non-page text carriers.
    /// </summary>
    /// <returns>True if any carrier was modified.</returns>
    /// <param name="caseSensitive">
    /// Must match how the CALLER matched page content. #905: RedactText defaults
    /// to case-INsensitive glyph removal while this scrub was hard-coded to
    /// Ordinal, so redacting "smith" cleared the page and left "Smith" sitting in
    /// /Info /Title — the tool reporting success over a document that still names
    /// the person. An under-redaction is the failure that matters here, so the
    /// default is case-INsensitive: over-scrubbing metadata is recoverable, a
    /// surviving name is not.
    /// </param>
    public static bool ScrubTerms(
        PdfDocument document, IEnumerable<string> terms, bool caseSensitive = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(terms);

        var actionable = terms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length >= MinTermLength)
            .Distinct(caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (actionable.Count == 0) return false;

        var changed = false;
        changed |= ScrubInfo(document, actionable, caseSensitive);
        changed |= ScrubXmpMetadata(document, actionable, caseSensitive);
        changed |= XfaXmlCarrier.ScrubTerms(document, actionable, caseSensitive).Changed;
        changed |= ScrubOutlines(document, actionable, caseSensitive);
        changed |= ScrubAnnotationContents(document, actionable, caseSensitive);
        changed |= ScrubFormFieldNames(document, actionable, caseSensitive);
        return changed;
    }

    private static bool ScrubInfo(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var info = document.Info;
        if (info == null) return false;

        var changed = false;
        foreach (var key in InfoKeys)
        {
            var value = info.GetStringOrNull(key);
            if (string.IsNullOrEmpty(value)) continue;

            var scrubbed = Excise(value, terms, caseSensitive);
            if (scrubbed == value) continue;

            if (scrubbed.Length == 0)
                info.Remove(key);
            else
                info[key] = new PdfString(scrubbed);

            changed = true;
        }
        return changed;
    }

    private static bool ScrubXmpMetadata(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        // #1129: EVERY reachable /Metadata packet, not just the catalog's.
        // §14.3.2 permits XMP on any object; a real CDC PDF kept the redacted
        // term in a page-level packet while the catalog packet scrubbed clean.
        var changed = false;
        foreach (var stream in document.EnumerateMetadataStreams())
        {
            // The XMP packet is plain-text XML. We treat it as text rather than
            // parsing it: a redacted name can appear in dc:title, dc:description,
            // pdf:Keywords, or a custom schema we have never heard of, and a
            // text-level excision catches all of them.
            var xmp = Encoding.UTF8.GetString(stream.DecodedData);
            var scrubbed = Excise(xmp, terms, caseSensitive);
            if (scrubbed == xmp) continue;

            // Write through the ENCODED bytes, not the decoded ones. The writer
            // serializes EncodedData; SetDecodedData only populates the decode
            // cache, so scrubbing that way would leave the secret in the saved
            // file while every in-memory read reported it gone.
            //
            // Storing the packet raw (dropping /Filter) is the conformant shape:
            // XMP must be readable without decompression anyway (§14.3.2).
            var bytes = Encoding.UTF8.GetBytes(scrubbed);
            stream.Remove("Filter");
            stream.Remove("DecodeParms");
            stream.SetEncodedData(bytes);
            stream["Length"] = new PdfInteger(bytes.Length);
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// #1130 — AcroForm field NAMES (<c>/T</c>) and tooltips (<c>/TU</c>) carry
    /// human-readable text. A passport form named a field "Your name as printed
    /// on your most recent U..." and leaked the redacted term there, in a
    /// carrier no per-area scrub reaches (the field's widget need not overlap a
    /// redaction).
    ///
    /// <para>Cut the term out, keeping the rest, like #1038 does for <c>/V</c>.
    /// <c>/T</c> is referenced by name from <c>/Kids</c> parent chains and JS
    /// <c>getField()</c>, so excising it can break form logic — but a surviving
    /// secret is the failure that matters (the carrier policy: under-redaction
    /// over form fidelity). This is document-level and runs once, so it covers
    /// every field, not only those over a redaction box.</para>
    /// </summary>
    private static bool ScrubFormFieldNames(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        // Walk the raw /AcroForm/Fields tree, recursing through /Kids. The
        // leaking /T is often on a NON-TERMINAL parent field (< /Kids [...]
        // /T (Your name ...) >), which GetAcroForm().Fields does not enumerate.
        var acro = document.Resolve(document.Catalog?.GetOptional("AcroForm") ?? PdfNull.Instance) as PdfDictionary;
        var rootFields = acro == null ? null
            : document.Resolve(acro.GetOptional("Fields") ?? PdfNull.Instance) as PdfArray;
        if (rootFields == null) return false;

        var changed = false;
        var visited = new HashSet<PdfDictionary>();
        var stack = new Stack<PdfObject>();
        foreach (var f in rootFields) stack.Push(f);

        while (stack.Count > 0)
        {
            if (document.Resolve(stack.Pop()) is not PdfDictionary node || !visited.Add(node))
                continue;

            foreach (var key in new[] { "T", "TU" })
            {
                if (document.Resolve(node.GetOptional(key) ?? PdfNull.Instance) is not PdfString str)
                    continue;
                var scrubbed = Excise(str.Value, terms, caseSensitive);
                if (scrubbed == str.Value) continue;
                node.SetString(key, scrubbed);
                changed = true;
            }

            if (document.Resolve(node.GetOptional("Kids") ?? PdfNull.Instance) is PdfArray kids)
                foreach (var k in kids) stack.Push(k);
        }
        return changed;
    }

    private static bool ScrubOutlines(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        if (document.Resolve(document.Catalog.GetOptional("Outlines") ?? PdfNull.Instance) is not PdfDictionary outlines)
            return false;

        var changed = false;
        var visited = new HashSet<PdfDictionary>();

        void Walk(PdfObject? node)
        {
            while (node != null)
            {
                if (document.Resolve(node) is not PdfDictionary item) return;
                if (!visited.Add(item)) return;   // guard against malformed cyclic /Next chains

                var title = item.GetStringOrNull("Title");
                if (!string.IsNullOrEmpty(title))
                {
                    var scrubbed = Excise(title, terms, caseSensitive);
                    if (scrubbed != title)
                    {
                        // An emptied bookmark keeps its destination but loses its
                        // label; removing the node entirely would renumber the
                        // outline tree and orphan its children.
                        item["Title"] = new PdfString(scrubbed.Length == 0 ? "[redacted]" : scrubbed);
                        changed = true;
                    }
                }

                Walk(item.GetOptional("First"));   // descend into children
                node = item.GetOptional("Next");   // then continue along siblings
            }
        }

        Walk(outlines.GetOptional("First"));
        return changed;
    }

    private static bool ScrubAnnotationContents(PdfDocument document, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var changed = false;

        for (int i = 1; i <= document.PageCount; i++)
        {
            var page = document.GetPage(i);
            if (document.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;

            foreach (var annotObj in annots)
            {
                if (document.Resolve(annotObj) is not PdfDictionary annot) continue;

                // /Contents is the comment text; /T is the author-supplied title.
                foreach (var key in new[] { "Contents", "T" })
                {
                    var value = annot.GetStringOrNull(key);
                    if (string.IsNullOrEmpty(value)) continue;

                    var scrubbed = Excise(value, terms, caseSensitive);
                    if (scrubbed == value) continue;

                    if (scrubbed.Length == 0)
                        annot.Remove(key);
                    else
                        annot[key] = new PdfString(scrubbed);

                    changed = true;
                }
            }
        }

        return changed;
    }

    private static string Excise(string value, IReadOnlyList<string> terms, bool caseSensitive)
    {
        var comparison = caseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        var result = value;
        foreach (var term in terms)
            result = result.Replace(term, string.Empty, comparison);
        return result.Trim();
    }
}
