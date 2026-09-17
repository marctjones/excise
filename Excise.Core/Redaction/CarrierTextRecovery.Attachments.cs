using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

public static partial class CarrierTextRecovery
{
    private static readonly HashSet<string> TextAttachmentExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".text", ".csv", ".tsv", ".xml", ".html", ".htm", ".xhtml", ".json",
        ".md", ".markdown", ".log", ".yaml", ".yml", ".ini", ".rtf", ".svg", ".eml",
    };

    /// <summary>
    /// Embedded files (§7.11.4): the document name tree, <c>/AF</c> arrays on
    /// the catalog, pages and annotations (§14.13), and
    /// <c>/FileAttachment</c> annotations (§12.5.6.15). Each is reported by its
    /// name, <c>/F</c>, <c>/UF</c>, <c>/Desc</c>, and payload.
    /// </summary>
    /// <remarks>
    /// <see cref="PdfDocument.GetEmbeddedFiles"/> is the starting set, and it
    /// is not the whole set: it reads the catalog <c>/AF</c> only when the name
    /// tree is empty, and never reads a page's or an annotation's <c>/AF</c>.
    /// Those are walked here and de-duplicated by file-specification identity.
    /// </remarks>
    private static void ScanAttachments(PdfDocument doc, Collector c)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var file in doc.GetEmbeddedFiles())
        {
            c.Token.ThrowIfCancellationRequested();
            if (!seen.Add(file.RawDictionary)) continue;
            var syntheticName = file.Name.StartsWith("_AF_", StringComparison.Ordinal)
                                || file.Name.StartsWith("_Annotation_", StringComparison.Ordinal);
            ReportAttachment(doc, c, file, syntheticName ? null : file.Name, file.PageNumber ?? 0, "attachment");
        }

        void FromAssociatedFiles(PdfDictionary? owner, int page, string source)
        {
            if (owner == null || doc.Resolve(owner.GetOptional("AF") ?? PdfNull.Instance) is not PdfArray af) return;
            foreach (var entry in af)
            {
                if (doc.Resolve(entry) is not PdfDictionary fs || !seen.Add(fs)) continue;
                var parsed = PdfEmbeddedFileParser.ParseFileSpecification(doc, fs, "");
                if (parsed != null)
                    ReportAttachment(doc, c, parsed, null, page, source);
            }
        }

        FromAssociatedFiles(doc.Catalog, 0, "attachment (catalog /AF)");
        for (var i = 1; i <= doc.PageCount; i++)
        {
            c.Token.ThrowIfCancellationRequested();
            var page = doc.GetPage(i);
            FromAssociatedFiles(page.Dictionary, i, "attachment (page /AF)");
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is PdfArray annots)
                foreach (var a in annots)
                    if (doc.Resolve(a) is PdfDictionary annot)
                        FromAssociatedFiles(annot, i, "attachment (annotation /AF)");
        }
    }

    private static void ReportAttachment(
        PdfDocument doc, Collector c, PdfEmbeddedFile file, string? treeName, int page, string carrier)
    {
        var fs = file.RawDictionary;
        var fsObj = doc.GetReferenceTo(fs)?.ObjectNum ?? 0;
        var uf = ReadText(doc, fs, "UF");
        var f = ReadText(doc, fs, "F");
        var displayName = uf ?? f ?? treeName ?? "(unnamed)";

        c.Text($"{carrier} name", treeName, page, fsObj);
        c.Text($"{carrier} /UF", uf, page, fsObj);
        if (!string.Equals(f, uf, StringComparison.Ordinal))
            c.Text($"{carrier} /F", f, page, fsObj);
        c.Text($"{carrier} /Desc", ReadText(doc, fs, "Desc"), page, fsObj, displayName);

        var hasEmbeddedStream = doc.Resolve(fs.GetOptional("EF") ?? PdfNull.Instance) is PdfDictionary;
        if (file.Bytes is null)
        {
            if (hasEmbeddedStream)
                c.Presence($"{carrier} payload", $"'{displayName}': embedded stream present but not decodable", page, fsObj);
            return;
        }

        ReportPayload(c, file.Bytes, file.MimeType, displayName, page, fsObj, carrier);
    }

    /// <summary>
    /// Classify an embedded payload: a PDF is opened and scanned recursively
    /// (page text and every carrier), text-like content is reported verbatim,
    /// anything else is reported as present.
    /// </summary>
    private static void ReportPayload(
        Collector c, byte[] bytes, string? mimeType, string displayName, int page, int obj, string carrier)
    {
        if (IsPdf(bytes))
        {
            ScanNestedPdf(c, bytes, displayName, page, obj, carrier);
            return;
        }

        if (IsTextLike(mimeType, displayName, bytes))
        {
            if (bytes.Length > MaxTextPayloadBytes)
            {
                c.Text($"{carrier} payload (text)", DecodeTextBytes(bytes[..MaxTextPayloadBytes]), page, obj, displayName);
                c.Presence($"{carrier} payload (text)", $"'{displayName}': only the first {MaxTextPayloadBytes} of {bytes.Length} bytes were read", page, obj);
            }
            else
            {
                c.Text($"{carrier} payload (text)", DecodeTextBytes(bytes), page, obj, displayName);
            }
            return;
        }

        c.Presence($"{carrier} payload (opaque)",
            $"'{displayName}': {bytes.Length} bytes, {(string.IsNullOrEmpty(mimeType) ? "unknown type" : mimeType)} — not decoded",
            page, obj);
    }

    private static void ScanNestedPdf(Collector c, byte[] bytes, string displayName, int page, int obj, string carrier)
    {
        if (c.Depth >= MaxAttachmentDepth)
        {
            c.Presence($"{carrier} payload (PDF)", $"'{displayName}': nested PDF not examined (depth limit {MaxAttachmentDepth})", page, obj);
            return;
        }
        if (bytes.Length > MaxNestedPdfBytes)
        {
            c.Presence($"{carrier} payload (PDF)", $"'{displayName}': nested PDF of {bytes.Length} bytes exceeds the scan bound", page, obj);
            return;
        }

        PdfDocument nested;
        try
        {
            nested = PdfDocument.Open(bytes);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            c.Presence($"{carrier} payload (PDF)", $"'{displayName}': nested PDF could not be opened ({ex.GetType().Name})", page, obj);
            return;
        }

        using (nested)
        {
            var child = c.Nested($"{carrier}[pdf:{displayName}]", c.Prefix.Length > 0 ? c.HostPage : page, deeper: true);
            for (var i = 1; i <= nested.PageCount; i++)
            {
                c.Token.ThrowIfCancellationRequested();
                string text;
                try
                {
                    text = LettersToText(nested.GetPage(i).GetLetters(c.Token));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
                {
                    child.Presence("page text", $"not examined: {ex.GetType().Name}", i);
                    continue;
                }
                child.Text("page text", text, i);
            }
            ScanInto(nested, child, includeHistory: true);
        }
    }

    private static bool IsPdf(byte[] bytes)
    {
        var window = Math.Min(bytes.Length, 1024);
        return bytes.AsSpan(0, window).IndexOf("%PDF-"u8) >= 0;
    }

    private static bool IsTextLike(string? mimeType, string displayName, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(mimeType))
        {
            // /Subtype is a PDF name: '/' in the MIME type is written #2F (§7.3.5),
            // and the lexer has already decoded it.
            var m = mimeType.ToLowerInvariant();
            if (m.StartsWith("text/", StringComparison.Ordinal) || m.EndsWith("json", StringComparison.Ordinal)
                || m.EndsWith("xml", StringComparison.Ordinal) || m.EndsWith("csv", StringComparison.Ordinal)
                || m.EndsWith("javascript", StringComparison.Ordinal) || m.EndsWith("yaml", StringComparison.Ordinal))
                return LooksLikeText(bytes);
        }
        var ext = Path.GetExtension(displayName);
        return ext.Length > 0 && TextAttachmentExtensions.Contains(ext) && LooksLikeText(bytes);
    }
}
