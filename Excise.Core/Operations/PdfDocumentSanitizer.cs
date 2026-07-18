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
    public static bool ScrubTerms(PdfDocument document, IEnumerable<string> terms)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(terms);

        var actionable = terms
            .Where(t => !string.IsNullOrWhiteSpace(t) && t.Length >= MinTermLength)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (actionable.Count == 0) return false;

        var changed = false;
        changed |= ScrubInfo(document, actionable);
        changed |= ScrubXmpMetadata(document, actionable);
        changed |= ScrubOutlines(document, actionable);
        changed |= ScrubAnnotationContents(document, actionable);
        return changed;
    }

    private static bool ScrubInfo(PdfDocument document, IReadOnlyList<string> terms)
    {
        var info = document.Info;
        if (info == null) return false;

        var changed = false;
        foreach (var key in InfoKeys)
        {
            var value = info.GetStringOrNull(key);
            if (string.IsNullOrEmpty(value)) continue;

            var scrubbed = Excise(value, terms);
            if (scrubbed == value) continue;

            if (scrubbed.Length == 0)
                info.Remove(key);
            else
                info[key] = new PdfString(scrubbed);

            changed = true;
        }
        return changed;
    }

    private static bool ScrubXmpMetadata(PdfDocument document, IReadOnlyList<string> terms)
    {
        if (document.Resolve(document.Catalog.GetOptional("Metadata") ?? PdfNull.Instance) is not PdfStream stream)
            return false;

        // The XMP packet is a plain-text XML document. We treat it as text
        // rather than parsing it: a redacted name can appear in dc:title,
        // dc:description, pdf:Keywords, or a custom schema we have never heard
        // of, and a text-level excision catches all of them.
        var xmp = Encoding.UTF8.GetString(stream.DecodedData);
        var scrubbed = Excise(xmp, terms);
        if (scrubbed == xmp) return false;

        // Write through the ENCODED bytes, not the decoded ones. The writer
        // serializes EncodedData; SetDecodedData only populates the decode cache,
        // so scrubbing that way would leave the secret in the saved file while
        // every in-memory read reported it gone — a leak that looks fixed.
        //
        // Storing the scrubbed packet raw (and dropping /Filter) keeps this
        // honest: XMP is required to be readable without decompression anyway
        // (ISO 32000-2 §14.3.2), so an uncompressed packet is the conformant
        // shape, not a shortcut.
        var bytes = Encoding.UTF8.GetBytes(scrubbed);
        stream.Remove("Filter");
        stream.Remove("DecodeParms");
        stream.SetEncodedData(bytes);
        stream["Length"] = new PdfInteger(bytes.Length);
        return true;
    }

    private static bool ScrubOutlines(PdfDocument document, IReadOnlyList<string> terms)
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
                    var scrubbed = Excise(title, terms);
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

    private static bool ScrubAnnotationContents(PdfDocument document, IReadOnlyList<string> terms)
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

                    var scrubbed = Excise(value, terms);
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

    private static string Excise(string value, IReadOnlyList<string> terms)
    {
        var result = value;
        foreach (var term in terms)
            result = result.Replace(term, string.Empty, StringComparison.Ordinal);
        return result.Trim();
    }
}
