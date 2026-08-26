using Excise.Core.Document;
using Excise.Core.Primitives;

namespace Excise.Core.Text.Segmentation;

/// <summary>
/// RC18 CERTAIN-channel recovery from unscrubbed CARRIERS. A redaction that
/// rewrote the visible page content but missed a document-level carrier leaves
/// the term physically present and trivially readable — <c>/ActualText</c>,
/// <c>/Alt</c> or <c>/E</c> in the structure tree (#636), or an annotation's
/// <c>/Contents</c> (#608). Unlike the width-residue channel this asserts
/// nothing probabilistic: the text is right there in the bytes.
///
/// This is the READ mirror of the scrubbers (<see cref="StructureTreeRedactionScrubber"/>,
/// <c>PdfDocumentSanitizer</c>): what recovery reports here is exactly what
/// scrubbing is supposed to remove, so the unredact tool sees the leak the
/// scrub side is meant to close. Deliberately limited to the hidden-readable
/// carriers, NOT bulk /Info + XMP metadata — those are usually benign document
/// metadata, and dumping them would bury a real leak in noise. Indirect string
/// objects are resolved first (the #1155 shape a plain GetStringOrNull misses).
/// </summary>
public static class CarrierTextRecovery
{
    // The structure-tree carriers that spell real content (§14.9.4): the actual
    // text a span represents, a figure's alternate description, an abbreviation
    // expansion. Same set StructureTreeRedactionScrubber removes.
    private static readonly string[] StructCarriers = { "ActualText", "Alt", "E" };

    /// <summary>A recoverable string found in a carrier the visible page does not show.</summary>
    /// <param name="Carrier">Human-readable carrier name, e.g. "structure-tree /ActualText".</param>
    /// <param name="Text">The recovered text, verbatim.</param>
    /// <param name="PageNumber">1-based page for annotations; 0 for document-level structure elements.</param>
    public readonly record struct CarrierText(string Carrier, string Text, int PageNumber);

    public static IReadOnlyList<CarrierText> Scan(PdfDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var found = new List<CarrierText>();
        ScanStructureTree(doc, found);
        ScanAnnotations(doc, found);
        return found;
    }

    private static void ScanStructureTree(PdfDocument doc, List<CarrierText> found)
    {
        if (doc.Resolve(doc.Catalog?.GetOptional("StructTreeRoot") ?? PdfNull.Instance) is not PdfDictionary root)
            return;

        var stack = new Stack<PdfObject>();
        if (root.GetOptional("K") is { } k) stack.Push(k);
        var visited = new HashSet<PdfDictionary>();
        var guard = 0;
        while (stack.Count > 0 && guard++ < 200_000)
        {
            var node = doc.Resolve(stack.Pop());
            if (node is PdfArray arr)
            {
                foreach (var e in arr) stack.Push(e);
                continue;
            }
            if (node is not PdfDictionary elem || !visited.Add(elem)) continue;

            foreach (var carrier in StructCarriers)
            {
                var v = (doc.Resolve(elem.GetOptional(carrier) ?? PdfNull.Instance) as PdfString)?.Value;
                if (!string.IsNullOrWhiteSpace(v))
                    found.Add(new CarrierText($"structure-tree /{carrier}", v!, 0));
            }
            if (elem.GetOptional("K") is { } kids) stack.Push(kids);
        }
    }

    private static void ScanAnnotations(PdfDocument doc, List<CarrierText> found)
    {
        for (int i = 1; i <= doc.PageCount; i++)
        {
            var page = doc.GetPage(i);
            if (doc.Resolve(page.Dictionary.GetOptional("Annots") ?? PdfNull.Instance) is not PdfArray annots)
                continue;
            foreach (var annotObj in annots)
            {
                if (doc.Resolve(annotObj) is not PdfDictionary annot) continue;
                // /Contents is the note/markup text (§12.5.6). /T is skipped on
                // purpose: on a widget it is the FIELD NAME (e.g. "btn1"), noise
                // rather than hidden content.
                var v = (doc.Resolve(annot.GetOptional("Contents") ?? PdfNull.Instance) as PdfString)?.Value;
                if (!string.IsNullOrWhiteSpace(v))
                    found.Add(new CarrierText("annotation /Contents", v!, i));
            }
        }
    }
}
